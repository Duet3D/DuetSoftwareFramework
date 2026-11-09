# CMake toolchain file for cross-compiling to 32-bit Raspberry Pi OS (armhf Linux).
#
# Mirrors aarch64-linux-gnu.cmake; see that file for why the devcontainer's glibc makes the .so
# loadable on Raspberry Pi OS Bookworm.
#
# The toolchain itself comes from crossbuild-essential-armhf (see .devcontainer/Dockerfile).
set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_PROCESSOR arm)

set(CMAKE_C_COMPILER arm-linux-gnueabihf-gcc)
set(CMAKE_CXX_COMPILER arm-linux-gnueabihf-g++)

# 32-bit ARM has no half-precision float type unless one is asked for, and CANlib's messages contain
# one: ShortMinCurMax and the pressure advance parameters are float16_t on the wire, so without this
# the type does not exist and nothing that includes a CAN message header compiles. IEEE is the format
# the Duet boards use; aarch64 has it by default, which is why only this toolchain needs to say so.
add_compile_options(-mfp16-format=ieee)

# Look for programs on the host, but libraries/headers/packages for the target only.
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)
