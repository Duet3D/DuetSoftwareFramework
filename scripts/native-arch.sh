# Architecture helpers shared by the scripts that build native libraries.
#
# Sourced, not run: `source "$(dirname "$0")/native-arch.sh"`.

# elf_machine_for <arch>
# Print the machine name readelf reports for a .NET runtime identifier, or fail for an unknown one.
elf_machine_for() {
    case "$1" in
        linux-arm64) echo "AArch64" ;;
        linux-arm)   echo "ARM" ;;
        linux-x64)   echo "X86-64" ;;
        *)           return 1 ;;
    esac
}

# host_arch_rid
# Print the runtime identifier of the machine running the build.
host_arch_rid() {
    case "$(uname -m)" in
        aarch64)         echo "linux-arm64" ;;
        armv7l|armv6l)   echo "linux-arm" ;;
        x86_64)          echo "linux-x64" ;;
        *)               return 1 ;;
    esac
}

# verify_elf_arch <library> <arch> <build directory>
# Check that a library was built for the architecture that was asked for.
#
# A build directory first configured without a toolchain file keeps the host compiler forever after:
# the toolchain's set(CMAKE_CXX_COMPILER) is a plain set, which does not override a value already in
# the cache. Pointing a preset at a toolchain afterwards therefore changes nothing, the build still
# succeeds, and it quietly produces host binaries. Nothing notices until the target tries to load
# one, and the loader's message for a shared object of the wrong architecture is "cannot open shared
# object file: No such file or directory" - about a file that is plainly there.
verify_elf_arch() {
    local lib="$1" arch="$2" build_dir="$3" expected actual
    expected="$(elf_machine_for "$arch")" || return 0

    actual="$(readelf -h "$lib" 2>/dev/null | sed -n 's/^ *Machine: *//p')"
    if [[ "$actual" != *"$expected"* ]]; then
        echo "Error: $(basename "$lib") was built for '$actual', but $arch needs '$expected'." >&2
        echo "       The CMake cache in $build_dir predates the toolchain file, so it is still" >&2
        echo "       using the compiler it was first configured with. Build again after:" >&2
        echo "           rm -rf $build_dir" >&2
        exit 1
    fi
}
