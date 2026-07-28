# Reference sources

Code kept for reference only. Nothing here is compiled, on any include path, or matched by the
source globs in [../src/CMakeLists.txt](../src/CMakeLists.txt).

## `Kinematics/`

The RepRapFirmware kinematics implementations, imported with the rest of `Movement/` in `ff9967cb`.

They stay here because kinematics is moving to **DuetControlServer**, not to this project. In the
split being built, DCS performs `DDA::InitStandardMove` steps 1-6 — which is where every kinematics
call lives (`CartesianToMotorSteps`, `MotorStepsToCartesian`, `GetTiltCorrection`,
`LimitSpeedAndAcceleration`, `IsContinuousRotationAxis`, `GetControllingDrives`) — and ships the
resulting endpoints and direction vector down to this library, which owns the `DDARing` from step 7
onwards. So none of this C++ has a caller here, but all of it is the source of truth for the C# port.

Delete it once `src/DuetControlServer/Motion/Kinematics/` is complete.

## `rrf-*.cpp`

Excerpts kept from files that were deleted, because they are the starting point for code this
project still needs:

| File | Source | Becomes |
|---|---|---|
| `rrf-Move-AddLinearSegments.cpp` | `Movement/Move.cpp:1702-2247` | `src/Motion/SegmentBuilder.cpp` — builds the `MoveSegment` chains that position tracking walks. Port drops the S-curve branches and the shaped path (`AxisShaper` is gone). |
| `rrf-DriveMovement-position.cpp` | `Movement/DriveMovement.{h,cpp}` | `src/Motion/DriveTracker.cpp` — the position-only part of `DriveMovement`. In RRF the segment advance is driven by the step ISR; with no local drivers the motion thread has to drive it instead. |

Delete each once its replacement exists.
