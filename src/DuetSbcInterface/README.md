# DuetSbcInterface — C++ SBC-side SPI replica & jitter test

A standalone C++ reimplementation of the **SBC side** of the RepRapFirmware SPI protocol — the side
implemented in C# by [`DuetControlServer/Link/Adapter/SPI.cs`](../DuetControlServer/Link/Adapter/SPI.cs)
and the transfer loop in [`LinkService.cs`](../DuetControlServer/Link/LinkService.cs).

Its purpose is to **measure the jitter** between a `RequestTransfer()` and the completion of the SPI
transfer that serves it, **without the .NET runtime**. If the C++ implementation shows the same
~430 µs mean but no 40 ms outliers, the outliers are coming from the managed runtime (GC / scheduling
of managed threads) rather than the kernel or hardware. If the C++ version jitters too, the cause is
below .NET (kernel, PREEMPT_RT config, SPI/DMA, GPIO, contention).

The device side of this same protocol already exists in C++ in
[`DuetCANMaster/src/SBC`](../DuetCANMaster/src/SBC) (RepRapFirmware). This project deliberately does
**not** implement USB transport, IAP/firmware update, file handling, IPC or the object model — only
what is needed to exercise the SPI transport end to end.

## Layout

```
protocol/   duet_sbc_protocol  — shared wire formats, constants, CRC16/CRC32 (firmware-agnostic)
sbc/        duet_sbc(.a/.so)   — GPIO chardev, spidev, process helpers, transfer state machine,
                                 the interface loop, and a C ABI (CApi.h) for P/Invoke
harness/    sbc_jitter_test    — standalone latency/jitter test program
```

`protocol/` is the "shared code" pulled out of the interface: it is the single source of truth for
the wire layout and checksums. Its structs are laid out byte-for-byte to match the C# definitions in
`DuetControlServer/Link/Protocol/**` (verified with `static_assert` on sizes/offsets) and the device
side's `SbcMessageFormats.h`. See [Sharing with DuetCANMaster](#sharing-with-duetcanmaster).

## Build

Requires a C++17 compiler, CMake ≥ 3.21 (for presets) and Linux UAPI headers (`linux/gpio.h`,
`linux/spi/spidev.h`). Two presets are provided (see `CMakePresets.json`).

### Cross-compile in the devcontainer for a 64-bit Pi (recommended)

The devcontainer ships the `aarch64-linux-gnu` cross toolchain (`crossbuild-essential-arm64`). The
`pi-arm64` preset targets aarch64 and **statically links** the test binary, so it runs on Raspberry
Pi OS Bookworm regardless of its glibc version (the container's toolchain targets a newer glibc):

```sh
cd src/DuetSbcInterface
cmake --preset pi-arm64
cmake --build --preset pi-arm64 -j

# copy the (static, self-contained) binary to the Pi
scp build-arm64/harness/sbc_jitter_test pi@raspberrypi:~/
```

`file build-arm64/harness/sbc_jitter_test` should report `ARM aarch64 ... statically linked`.

### Build natively on the Pi

```sh
sudo apt install -y build-essential cmake linux-libc-dev   # one-time
cd src/DuetSbcInterface
cmake --preset native
cmake --build --preset native -j
```

### Artifacts

- `harness/sbc_jitter_test` — the test program (static under the cross preset)
- `sbc/libduet_sbc.so` — shared library exposing the C ABI (for DCS P/Invoke)
- `sbc/libduet_sbc.a` — static library

> **P/Invoke `.so` note:** a shared library must link glibc dynamically, so the cross-built
> `libduet_sbc.so` requires the target's glibc. To produce a Bookworm-compatible `.so`, either build
> it natively on the Pi, or fetch a Pi sysroot and do a glibc-matched dynamic build:
> ```sh
> scripts/fetch-pi-sysroot.sh pi@raspberrypi
> cmake --preset pi-arm64 -DDUET_SBC_SYSROOT=$(pwd)/pi-sysroot -DDUET_SBC_STATIC=OFF
> cmake --build --preset pi-arm64
> ```
> The standalone jitter test does not need this — it is statically linked.

## Run the jitter test

The Duet must be connected over SPI and running the device-side firmware. Real-time scheduling and
GPIO access require privileges (`CAP_SYS_NICE` for SCHED_FIFO, and access to the spidev/gpiochip
nodes), so run under `sudo` or grant the capabilities.

```sh
sudo ./build/harness/sbc_jitter_test \
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

## Using it from C# (if the C++ side is clean)

If the C++ implementation has no jitter, the same core can back DCS via P/Invoke instead of the
managed SPI adapter. `libduet_sbc.so` exports a small C ABI ([`sbc/include/DuetSbc/CApi.h`](sbc/include/DuetSbc/CApi.h)):

```csharp
[DllImport("duet_sbc")] static extern IntPtr DuetSbc_Create(ref DuetSbcConfig cfg, byte[] err, int len);
[DllImport("duet_sbc")] static extern int    DuetSbc_Connect(IntPtr h, byte[] err, int len);
[DllImport("duet_sbc")] static extern void   DuetSbc_Start(IntPtr h);
[DllImport("duet_sbc")] static extern void   DuetSbc_QueueMessage(IntPtr h, uint flags, byte[] msg, int len);
// ... register callbacks with DuetSbc_SetMessageCallback / DuetSbc_SetCanResponseCallback ...
```

A DCS `ILinkAdapter` implementation would wrap this handle; incoming packets arrive via the
registered callbacks and outgoing data goes through the `Queue*` functions. Because the protocol
structs here match the C# ones byte-for-byte, the two sides stay compatible.

## Sharing with DuetCANMaster

`duet_sbc_protocol` is intended to become the shared definition consumed by **both** this project and
the device side. It is header-only + a CRC `.cpp`, with no firmware/OS dependencies, so it can be
added to the DuetCANMaster build and have `SbcMessageFormats.h` reduced to a thin alias over these
headers. That change touches the DuetCANMaster submodule and its Makefile and is intentionally left
as a follow-up so this project builds independently; until then, keep the two definitions in sync
(the `static_assert`s here guard the layout).

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
- This is a diagnostic tool: the reconnection/error paths mirror `SPI.cs` but the higher-level
  behaviours (code channels, locks, model) are intentionally absent.
