# CAN message format generator

The Duet 3 CAN message formats used to exist twice: as packed C++ structs in CANlib's
`lib/CANlib/src/CanMessageFormats.h`, and as a hand-written C# mirror in
`src/DuetControlServer/Link/Protocol/CanMessages`. Keeping two independent transcriptions of ~60 bit-packed
wire formats in step is tedious and easy to get subtly wrong — a mistake that shows up as corrupted
messages on the bus rather than as a compile error.

This tool removes the duplication. Both representations are generated from one neutral description:

```
Schema/can-messages.json
        |
        +--> generated/cpp/CanMessageFormats.h                                  (drop-in for CANlib)
        +--> generated/cpp/CanMessageGenericTables.h                            (drop-in for CANlib)
        +--> src/DuetControlServer/Link/Protocol/CanMessages/Generated/*.cs     (DuetControlServer)
        +--> generated/cpp/CanMessageLayoutProbe.cpp                            (C++ conformance harness)
        +--> generated/cpp/CanMessageGenericTablesProbe.cpp                     (C++ conformance harness)
        +--> src/UnitTests/Link/CanMessageLayout.g.cs                           (C# conformance harness)
        +--> src/UnitTests/Link/CanGenericTableLayout.g.cs                      (C# conformance harness)
```

## Usage

```sh
make can-messages          # regenerate everything from the schema
make can-messages-check    # fail if the checked-in output is stale (for CI)
make can-messages-verify   # check the generated C++ layouts against CANlib's own header
```

The generated files are checked in, so neither the DSF build nor the CANlib build depends on this tool.
Re-run `make can-messages` after editing the schema and commit the result.

`make can-messages-verify` needs a host C++ compiler and the `CANlib`, `RRFLibraries` and `CoreN2G`
submodules (`git submodule update --init lib/CANlib lib/RRFLibraries lib/CoreN2G`).

## How the layouts are guaranteed to match

The generator computes the byte and bit layout of every struct itself, reproducing what GCC does for a
`__attribute__((packed))` struct on a little-endian target:

* a bit cursor runs through the struct;
* a bitfield is placed at the cursor and advances it by its width, with **no** padding and **no** regard for
  the declared storage type — so bitfields straddle byte and storage-unit boundaries, and consecutive
  groups of different storage types pack together;
* a non-bitfield member first rounds the cursor up to the next byte boundary;
* the struct size is the cursor rounded up to a whole number of bytes.

Those rules were established empirically against g++, not assumed. Two of CANlib's messages depend on the
sharp edges: `CanMessageSetDefaultHeaterModel` and `CanMessageDeleteFilamentMonitor` declare 17- and 20-bit
groups that make the message an odd 5 bytes long, and `FilamentMonitorDataV2` has a 31-bit group, so its
second group starts at bit 31 rather than on a byte boundary. The generator reproduces all of this.

That computed layout is then asserted in both languages by generated harnesses:

* **`CanMessageLayoutProbe.cpp`** checks the size of every struct, the offset of every plain member, and —
  by setting each bitfield to all ones on an otherwise-zeroed struct and comparing the raw bytes — the exact
  bit position and width of every bitfield. It is compiled **twice**: once against CANlib's hand-written
  header, which proves the schema describes the real message formats faithfully, and once against the
  generated header, which proves the two are equivalent. `verify-cpp-layout.sh` also compiles CANlib's own
  `.cpp` files against the generated header to confirm it is a genuine drop-in replacement, not merely
  layout-compatible.
* **`compare-method-surface.py`** diffs the method surfaces of the two headers: same methods on the same
  structs, with the same arity and qualifiers. Layout checks are blind to methods, and only three of
  CANlib's translation units are available here, so a method that only RepRapFirmware or Duet3Expansion
  calls could otherwise disappear unnoticed — which is exactly what happened to
  `FilamentMonitorDataV2::ClearReservedFields` and to both structs' constructors. The generated header is
  allowed to add `const` and `constexpr` where CANlib omits them, since those only widen what callers may
  do; anything else must match.
* **`CanMessageLayout.g.cs`** asserts the same expectations against the generated C# structs as an NUnit
  fixture, so `dotnet test` catches any C#-side drift.

Both harnesses currently make 469 checks over 76 structs and 207 bitfields. The generic message parameter
tables are checked the same way, by `CanMessageGenericTablesProbe.cpp` and `CanGenericTableLayout.g.cs`:
143 checks over 22 tables and 121 parameters.

## Schema

### Structs

```jsonc
{
  "name": "CanMessageReset",
  "doc": "Reset message",                  // string or array of lines
  "messageType": "reset",                  // CanMessageType enumerator; C# gets the PascalCase spelling
  "requestIdField": "requestId",           // generates SetRequestId; otherwise ClearReservedFields is generated
  "members": [ ... ],
  "methods": [ ... ]
}
```

Other struct-level keys:

| Key | Meaning |
| --- | --- |
| `packed` | Defaults to `true`. `false` gives natural alignment (`HeaterModel`, `MinCurMax`, `ShapingPair`). |
| `emit` | Which languages get a definition. `["csharp"]` mirrors a type CANlib declares in another header. |
| `existing` | Where a non-emitted type lives in each language. |
| `isUnion` | All members sit at offset 0. |
| `nestedIn` | C++ nests the struct inside its parent; C# emits it at the top level. |
| `templateParam` / `instantiations` | C++ keeps the template; C# gets one concrete struct per instantiation. |
| `setRequestIdAlsoClears` / `clearAlsoClears` | Non-reserved fields those helpers also zero. |
| `clearReservedFields` | Forces `ClearReservedFields` on a struct that is not itself a message. |
| `cppStaticAsserts` | Extra `static_assert`s copied into the C++ header verbatim. |

### Members

```jsonc
{ "kind": "field",    "name": "timeSent", "type": "u32" }
{ "kind": "array",    "name": "text",     "type": "char", "length": "60" }
{ "kind": "bitfield", "storage": "u16", "fields": [
    { "name": "requestId", "width": 12 },
    { "name": "zero",      "width": 4, "reserved": true } ] }
{ "kind": "union", "anonymous": true, "alternatives": [ ... ] }
```

Types are `u8`/`i8`/`u16`/`i16`/`u32`/`i32`/`u64`/`i64`/`f16`/`f32`/`char`/`bool`, or the name of another
struct. Bitfields may be marked `bool` (a `bool` property in C#) or `signed` (sign-extended on read).
`reserved` marks a spare field that the generated `SetRequestId`/`ClearReservedFields` zero. `unaligned`
makes the C++ side use the `LoadLE`/`StoreLE` helpers. `cppAccessPath` records that CANlib reaches the
member through a sub-struct, which only affects the C++ probe.

### Methods

Method bodies are written once, in a small neutral language, and rendered into both targets:

```jsonc
{
  "name": "GetActualDataLength", "returnType": "usize",
  "body": [ { "return": "(sizeof(self) - sizeof(perDrive)) + (numDrivers * sizeof(elem(perDrive)))" } ]
}
```

Statements are `return`, `set`/`to`, `orWith`/`value`, `storeLE`/`to`, `incr`, `let`, `if`/`then`/`else` and
`forRange`/`body`. Expressions are ordinary C-like infix, plus these intrinsics:

| Intrinsic | C++ | C# |
| --- | --- | --- |
| `sizeof(x)` | `sizeof(...)` | resolved to an integer literal |
| `countof(a)` | `ARRAY_SIZE(a)` | resolved to an integer literal |
| `elem(a)` | `a[0]` | element of `a` |
| `strnlen(a[, max])` | `Strnlen(a, ...)` | `CanText.Strnlen(...)` |
| `popcount(x)` | `Bitmap<uint32_t>(x).CountSetBits()` | `BitOperations.PopCount(x)` |
| `loadLE(f)` / `storeLE` | `LoadLEF32(&f)` etc. | direct field access (`Pack = 1` handles it) |
| `u8(x)`, `u32(x)`, … | C-style cast | C# cast, with `bool` mapped to 0/1 |
| `self` | `*this` | the enclosing struct |

`SetRequestId` and `ClearReservedFields` are **not** written in the schema. They are derived from which
fields are marked `reserved`, so the two languages can never disagree about what gets zeroed.

### Constructors

Two of CANlib's payload structs are built through constructors rather than setters, and
`DriverStateControl`'s parameterless one is what zeroes its reserved bits. A `constructors` array
reproduces them:

```jsonc
"constructors": [
  { "init": [ { "name": "mode", "value": "0" } ] },                    // DriverStateControl() noexcept : mode(0) { }
  { "explicit": true,
    "params": [ { "name": "m", "type": "u16" },
                { "name": "idlePc", "type": "u16", "default": "0" } ],
    "init": [ { "name": "mode", "value": "m" } ] },
  { "params": [ ... ], "body": [ ... ] }                               // an ordinary body instead of an init list
]
```

Constructors are emitted for C++ only: a C# struct cannot reproduce a zero-initialising parameterless
constructor, because `default` and array allocation bypass it. Where C# needs the same functionality the
schema declares an ordinary `Set` method with `"emit": ["csharp"]`.

## Generated C# conventions

* `[StructLayout(LayoutKind.Explicit, Pack = 1, Size = N)]` with a generator-computed `[FieldOffset]` on
  every field, so the layout is stated outright rather than inferred from CLR sequential-layout rules.
  Explicit layout is also what makes the C++ unions expressible.
* Bitfields become properties over a private backing integer, one per declared group where the group is a
  workable size. The three groups that are not (see above) fall back to a byte buffer addressed by absolute
  bit offset through `CanBitFields`.
* Fixed C arrays become `[InlineArray]` buffer structs (`ByteArray56`, `PerDriveValuesArray8`, …) so they
  stay blittable for `MemoryMarshal`.
* `char` arrays additionally get a `…String` property that decodes up to the first null byte.
* Names are PascalCased. Where PascalCasing would collide a constant with a field that C++ distinguishes by
  case alone — `CanMessageEnterTestMode`'s `Passwd` constant and `passwd` field — the constant takes a
  `Value` suffix (`PasswdValue`).

## Generic messages

Around twenty of the messages do not have a struct of their own. They share `CanMessageGeneric`, whose
payload is a `paramMap` bitmap plus the values of whichever G-code parameters are being sent, and a
**parameter table** tells both ends what those parameters are:

```jsonc
{
  "name": "M950FanParams",
  "messageType": "m950Fan",                 // where exactly one message type uses this table
  "params": [
    { "letter": "F", "type": "uint16" },
    { "letter": "Q", "type": "pwmFreq" },
    { "letter": "C", "type": "reducedString" },
    { "letter": "K", "type": "float", "doc": "tacho pulses/rev added at 3.5" },
    { "letter": "L", "type": "uint16Array", "maxLength": 4 }
  ]
}
```

Types are the `ParamDescriptor::ParamType` enumerators under schema names: `uint8`…`uint64`, `int8`…`int32`,
`float`, `float16`, `pwmFreq`, `char`, `string`, `reducedString`, `localDriver`, and the array types
`uint8Array`, `uint16Array`, `uint32Array` and `floatArray`, which need a `maxLength`.

The sender packs the parameters it is sending in table order with no padding of any kind — a fixed-size
parameter takes its element size, a string its bytes plus a null terminator, an array a length byte followed
by its elements — and sets bit *i* of `paramMap` for entry *i*. **A new parameter must therefore be added at
the end of its table**, or the paramMap bits and offsets of every existing one shift.

A letter outside `A..Z` cannot be supplied by a G-code command. CANlib uses that both to retire an entry
while keeping its position (`M569.1`'s `h`) and to reserve a parameter that the sender fills in itself
(`M915`'s `d`, a driver bitmap), so the generated code still lets a caller set one.

Each table produces:

* an entry in the C++ `CanMessageGenericTables.h`, with CANlib's per-entry macros expanded;
* an entry in `CanGenericTables`, plus the `CanParamType` and `CanParamDescriptor` that describe it.
  `CanParamType` keeps CANlib's numbering because the low nibble is the element size, which decides how far
  each parameter advances the write cursor;
* a typed builder, so that the letters and their types are checked by the compiler:

```csharp
M950FanBuilder builder = new();
builder.F(fanNumber).C(portName).K(pulsesPerRev);
// builder.K("oops") does not compile, and there is no Z method
```

The two hand-written pieces are `CanGenericWriter`, which does the packing (and `CanGenericParser`, its
counterpart, used to read back what it produced), and `CanMessageGenericConstructor`, which builds a message
from a `Code` — the equivalent of RepRapFirmware's `PopulateFromCommand`, and the path to use when the
parameters are whatever the user typed rather than known at the call site.

`ParamDescriptor` itself is not generated: it comes from CANlib's `CanMessageGenericTableFormat.h`, since it
is the type both ends of the link agree on. Its per-type macros are simply unused once the tables are
generated.

## Known gaps

* `CanTiming` is referenced rather than generated: it lives in CANlib's `CanSettings.h` and already has a
  hand-written C# counterpart in `Link/Protocol/Shared/CanTiming.cs` whose helper methods (`SetDefaults`,
  `EnableBrs`, …) are outside what the schema's expression language covers. Its layout is therefore not
  covered by the conformance harnesses.
* `CanMessageType` itself is still declared separately in `CanId.h` and `Link/Protocol/Shared/CanMessageType.cs`.
  Generating it from the schema would be a natural follow-up.
* `DebugPrint` is declaration-only: the C++ header declares it and CANlib's `CanMessageFormats.cpp` defines
  it. There is no C# equivalent.
* The two `cppPrivate` temperature fields (`CanSensorReport`, `CanHeaterReport`) are excluded from the C++
  probe's `offsetof` checks because they are not accessible from outside the struct; their position is still
  pinned by the struct size and the preceding members.
