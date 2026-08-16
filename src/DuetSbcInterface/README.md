# DuetSbcInterface — C++ SBC-side SPI replica & jitter test

The C++ implementation of the **SBC side** of the RepRapFirmware SPI protocol. It is the transport
DuetControlServer uses: the managed SPI adapter and its transfer loop have been replaced by this
library, which DCS drives over a small C ABI.

It began as a jitter experiment — a reimplementation of the then-C# `SPI.cs` and `LinkService.cs`
used to **measure the latency** between a `RequestTransfer()` and the completion of the SPI transfer
that serves it, **without the .NET runtime**, to determine whether the observed 40 ms outliers came
from the managed runtime (GC / thread scheduling) or from below it (kernel, PREEMPT_RT config,
SPI/DMA, GPIO, contention). The bundled [jitter harness](#run-the-jitter-test) still measures exactly
that, and the transfer loop now runs in production on its own pinned real-time thread.

The device side of this same protocol already exists in C++ in
[`DuetCANMaster/src/SBC`](../DuetCANMaster/src/SBC) (RepRapFirmware). This project implements the SPI
transport and the IAP/firmware-update handshake; file handling, IPC and the object model stay in
DuetControlServer, which drives this library through the C ABI described below. USB transport is not
implemented (and is no longer supported by DCS).

## Layout

`src/` follows the same convention as [DuetCANMaster](../DuetCANMaster): one directory per module,
each header sitting next to the `.cpp` that implements it, with `src/` itself as the include root.
A module includes its own headers as `"Foo.h"` and another module's as `<Module/Foo.h>`.

```
src/                 duet_sbc(.a/.so)
  CApi.h/.cpp        C ABI for P/Invoke from DuetControlServer
  Config/            build-time defaults, the runtime Config struct, and the machine's fixed limits
  Hardware/          spidev and GPIO chardev wrappers
  Motion/            duet_motion(.a): the DDA ring, lookahead, segments and the step clock model
  Platform/          process/thread helpers (RT priority, affinity) and the lock-free ring buffer
  SBC/               transfer state machine, the interface loop, and the LinkEvents wire format
  Storage/           CRC16/CRC32
harness/             sbc_jitter_test — standalone latency/jitter test program
tests/               host-side unit tests (no hardware required)
scripts/             fetch-pi-sysroot.sh
cmake/               cross-compilation toolchain files
```

The wire protocol itself lives outside this project, in
[`lib/DuetSpiInterface`](../../lib/DuetSpiInterface) (`duet_spi_protocol`), because DuetCANMaster
consumes the same headers. It is the single source of truth for the wire layout. Its structs are laid out byte-for-byte to match the C# definitions in
`DuetControlServer/Link/Protocol/**` (verified with `static_assert` on sizes/offsets) and the device
side's `SbcMessageFormats.h`. See [Sharing with DuetCANMaster](#sharing-with-duetcanmaster).

## Build

Requires a C++17 compiler, CMake ≥ 3.21 (for presets) and Linux UAPI headers (`linux/gpio.h`,
`linux/spi/spidev.h`). Three presets are provided (see `CMakePresets.json`):

| Preset | Builds for | glibc it links against |
| --- | --- | --- |
| `native` | this machine | this machine's |
| `arm64` | aarch64 | this container's (2.36) — deployable to Bookworm |
| `arm64-sysroot` | aarch64 | the Pi's, from `pi-sysroot/` — for pre-Bookworm targets |

Each uses its own build tree (`build/<preset>`), and
`scripts/build.sh` picks between them the same way: `native` when it is already running on aarch64,
otherwise `arm64`, or `arm64-sysroot` when a sysroot is explicitly requested.

### Cross-compile in the devcontainer for a 64-bit Pi (recommended)

The devcontainer ships the `aarch64-linux-gnu` cross toolchain (`crossbuild-essential-arm64`). It is
based on Debian Bookworm, so that toolchain targets the same glibc (2.36) as Raspberry Pi OS
Bookworm and everything it builds is deployable as-is. Both aarch64 presets additionally
**statically link** the test binary, so it runs on any Pi OS release:

```sh
cd src/DuetSbcInterface
cmake --preset arm64
cmake --build --preset arm64 -j

# copy the (static, self-contained) binary to the Pi
scp build/arm64/harness/sbc_jitter_test pi@raspberrypi:~/
```

`file build/arm64/harness/sbc_jitter_test` should report `ARM aarch64 ... statically linked`.

`libduet_sbc.so` cannot be statically self-contained, so it links glibc dynamically and its
requirements have to be satisfiable on the target. Under `arm64` the highest version it asks for is
`GLIBC_2.17`, comfortably below Bookworm's 2.36, so it loads on the Pi unchanged. Check with:

```sh
aarch64-linux-gnu-objdump -p build/arm64/src/libduet_sbc.so | grep -o 'GLIBC_[0-9.]*' | sort -uV
```

#### Targeting an older Pi OS release

`arm64-sysroot` exists for the case where the target's glibc is *older* than the container's. It links
against a copy of the Pi's own libraries and refuses to configure if that copy is missing:

```sh
scripts/fetch-pi-sysroot.sh pi@raspberrypi          # one-off, into pi-sysroot/
cmake --preset arm64-sysroot
cmake --build --preset arm64-sysroot -j
```

Use `-DDUET_SBC_SYSROOT=<dir>` to point it at a sysroot kept somewhere else. `scripts/build.sh`
never reaches for this on its own — pass `--sysroot <dir>` or `--fetch-sysroot`.

### Build natively on the Pi

```sh
sudo apt install -y build-essential cmake linux-libc-dev   # one-time
cd src/DuetSbcInterface
cmake --preset native
cmake --build --preset native -j
```

### Linting

Every source is run through `clang-tidy` as it is compiled, and any check left enabled in
`.clang-tidy` fails the build (`--warnings-as-errors=*`). Install it with `sudo apt install -y
clang-tidy` (already present in the devcontainer); if it is missing, CMake warns at configure time
and builds without linting. Pass `-DDUET_SBC_CLANG_TIDY=OFF` for a plain, roughly 3x faster build.

This applies to every preset, sysroot builds included. A cross build needs two things the plain
`clang-tidy` command line does not carry, both of which CMake works out at configure time by asking
the compiler: the target triple (`-dumpmachine`, otherwise clang analyses aarch64 code as x86-64),
and the compiler's real include search path (`-E -v`, otherwise `--sysroot` sends clang looking for
libstdc++ inside the Pi sysroot, where GCC never looks for it).

### Artifacts

- `harness/sbc_jitter_test` — the test program (static under the cross preset)
- `src/libduet_sbc.so` — shared library exposing the C ABI (for DCS P/Invoke)
- `src/libduet_sbc.a` — static library

> **P/Invoke `.so` note:** a shared library must link glibc dynamically, so the cross-built
> `libduet_sbc.so` needs the target's glibc to be no older than what it was linked against. The
> container and Raspberry Pi OS Bookworm both ship glibc 2.36, so the `arm64` build deploys as-is.
> Only for an older Pi OS release do you need a sysroot:
> ```sh
> scripts/fetch-pi-sysroot.sh pi@raspberrypi
> cmake --preset arm64-sysroot -DDUET_SBC_STATIC=OFF
> cmake --build --preset arm64-sysroot
> ```
> The standalone jitter test never needs this — it is statically linked.

## Run the jitter test

The Duet must be connected over SPI and running the device-side firmware. Real-time scheduling and
GPIO access require privileges (`CAP_SYS_NICE` for SCHED_FIFO, and access to the spidev/gpiochip
nodes), so run under `sudo` or grant the capabilities.

```sh
sudo ./build/native/harness/sbc_jitter_test \
    --spi-dev /dev/spidev0.0 --spi-hz 8000000 \
    --gpiochip /dev/gpiochip0 --tfr-pin 25 --dap-pin 24 \
    --core 3 --rate 1000
```

The producer sends `CanMessageMovementLinearShaped` messages exactly like `MotionService.cs`
(destination address 2, a batch of `--msgs-per-cycle` per cycle, incrementing seq). The message
layout mirrors CANlib's `CanMessageMovementLinearShaped`; CANlib itself cannot be included here
because it targets the 32-bit embedded ABI and its headers `static_assert` a 32-bit `unsigned long`,
which fails on 64-bit Linux — the same reason DCS reimplements these structs in C#.

To scope the latency on hardware, pass `--out-pin N`: that GPIO line goes **high** when the SBC has
data staged to transfer and **low** once the transfer completes (mirroring the DCS `#if DEBUG`
`SbcDataAvailable` pin). Trigger a scope on it against the SPI clock/CS to see the request→transfer
window directly.

Pin numbers, SPI device and frequency default to the same values as DCS; override them to match your
board wiring (see `--help`). Press Ctrl-C (or use `--seconds N`) to stop and print the report:

```
==== Results (N request-driven transfers, last M dropped) ====
  RequestTransfer -> transfer complete latency:
    mean / min / p50 / p90 / p99 / p99.9 / p99.99 / max
  Max pin wait during a transfer, max delay between transfers, glitches, missed edges
```

The last few transfers before shutdown can be perturbed by teardown, so the producer is stopped
first, in-flight transfers are given a moment to drain, and the final `--drop-last N` samples
(default 16) are excluded from the report.

## Latency / jitter tuning

The transfer path sleeps when idle (0% CPU) and wakes with minimal latency — it does **not** busy-wait:

- **Direct `poll()` on the GPIO edge fd**: the interface thread blocks in `poll()` directly on the
  TfrRdy line's event fd (plus an eventfd for `RequestTransfer`/stop wake-ups). There is no separate
  GPIO monitor thread, so a pin edge wakes the interface thread in a *single* hop (edge IRQ → thread),
  rather than the two-hop monitor-thread → condition-variable path. Pin the interface thread to an
  isolated core (`--core`) and give it real-time priority (`--if-prio`, default 50) so the kernel
  wakes it promptly.
- **Reliable producer**: the producer thread (standing in for `MotionService`) runs real-time and can
  be pinned with `--producer-core`, so it does not stall and open a keep-alive-sized (~25 ms) gap
  between transfers. A 25 ms "max delay between transfers" almost always means the producer missed its
  slot — pin it and give it real-time priority.

Recommended low-jitter invocation on a Pi with core 3 isolated (`isolcpus=3`), producer on core 2:

```sh
sudo ./sbc_jitter_test --core 3 --producer-core 2 --out-pin 23
```

If a 25 ms tail persists even with a pinned real-time producer on an isolated core, the cause is below
this code (kernel/PREEMPT_RT config, SPI/DMA completion, GPIO IRQ routing, or an un-isolated core)
rather than the transfer loop.

Compare the tail (p99.9 / max) directly against the C# instrumentation. Useful A/B knobs that mirror
the C# fixes: `--no-rt` (drop SCHED_FIFO), `--no-isolate` (drop core pinning), `--monitor-core`,
`--if-prio` / `--mon-prio`.

## Using it from DuetControlServer

DCS **does** use this library: it is the only SPI transport. The managed SPI adapter it replaced
(`DuetControlServer/Link/Adapter/SPI.cs`) has been removed, and `libduet_sbc.so` is built and shipped
alongside DuetControlServer by its `Makefile` (see [Packaging](#packaging)).

The managed side lives in [`DuetControlServer/Link/Native`](../DuetControlServer/Link/Native):

| File | Role |
| --- | --- |
| `NativeMethods.cs` | P/Invoke declarations for the C ABI |
| `LinkEvents.cs` | Managed mirror of `LinkEvents.h`, the ring record layouts |
| `NativeLink.cs` | Owns the handle, marshals the config, maps request ids to `TaskCompletionSource` |

`LinkService` starts the loop and runs one dispatcher thread that drains the inbound ring;
`LinkInterface` queues outgoing work. Nothing above `Link/` is aware the transport is native.

### Why rings instead of callbacks

The interface thread runs pinned and `SCHED_FIFO`. If it invoked managed callbacks directly, then
managed allocation, lock acquisition and GC pauses would all execute *on that thread*, mid-transfer —
reintroducing (and worsening) the very jitter this project exists to remove. So the boundary is two
lock-free ring buffers ([`RingBuffer.h`](src/Platform/RingBuffer.h)):

```
managed threads --> [outbound ring] --> interface thread (RT)   <- drained while staging a transfer
managed dispatcher <-- [inbound ring] <-- interface thread (RT) <- written as packets arrive
```

Producers serialise among themselves with a mutex the consumer never takes, so a producer can never
block the real-time thread. Record layouts are defined in
[`LinkEvents.h`](src/SBC/LinkEvents.h) and asserted on both sides — `NativeLink` verifies
the managed struct sizes at startup so a drift fails loudly instead of silently corrupting events.

The ring is covered by [`tests/RingBufferTests.cpp`](tests/RingBufferTests.cpp) (framing, wrap/skip
marker, full-ring rejection, and a threaded ordering/integrity soak); run it with `ctest` from the
build directory.

### Packaging

`src/DuetControlServer/Makefile` builds the `.so` for the package `ARCH` and copies it next to the
managed assemblies, so default P/Invoke probing finds it with no `DllImportResolver`:

```sh
cd src/DuetControlServer
make ARCH=arm64 CONFIG=Release publish
```

> **glibc:** a `.so` must link glibc dynamically, so a cross-built one must not need a newer glibc
> than the target has. The devcontainer is Debian Bookworm and targets the same glibc 2.36 as
> Raspberry Pi OS Bookworm, so no sysroot is needed. To target an older release, pass one:
> `make ARCH=arm64 DUET_SBC_SYSROOT=/path/to/pi-sysroot publish`
> (see `scripts/fetch-pi-sysroot.sh`).

## Sharing with DuetCANMaster

`duet_spi_protocol` now lives in [`lib/DuetSpiInterface`](../../lib/DuetSpiInterface) and is consumed
by **both** this project and the device side. DuetCANMaster picks it up through its `LIBRARIES_DIR`
include path, and its `SbcMessageFormats.h` is a thin alias over these headers plus the constants
that never cross the link. Change the wire format in `lib/DuetSpiInterface` only, so both sides move
together; the `static_assert`s there guard the layout.

## Error recovery

The interface never terminates on a transfer error. `PerformFullTransfer` recovers from any transfer
failure (bad format/checksum/protocol, SPI/GPIO I/O errors, or a controller reset/reboot) by
resynchronising and re-running the handshake, and the interface loop catches everything else (e.g. a
malformed incoming packet) and forces a clean resync via `ResetConnection`. Fast-failing errors (such
as a persistent protocol mismatch) are paced with a backoff that grows to a 1 s cap so recovery never
spins the CPU; timeouts are already paced by the pin wait. Recoveries are reported through the error
callback (`[recover] ...` on stderr in the harness) and counted as "Connection resyncs" in the report.

Recovery relies on the device side (DuetCANMaster) resetting when the SBC restarts a transfer, which
its `DataTransfer` state machine already does via its own transfer/connection timeouts. Only the
initial `Connect()` still throws — a failure there means the board is absent or fundamentally
incompatible at startup, which is worth surfacing rather than looping on.

## Notes / limitations

- v2 GPIO chardev uAPI is preferred (per-edge sequence numbers, needed for the fresh-edge logic in
  `WaitForTransfer`); it falls back to the legacy v1 uAPI on kernels < 5.10.
- The jitter metric is captured on the interface thread immediately after `PerformFullTransfer`
  returns, into a lock-free preallocated sample buffer, so measurement does not perturb the result.
- Higher-level behaviours (code channels, locks, object model, file requests) stay in
  DuetControlServer; this library handles the transport, the packet framing and IAP only.
