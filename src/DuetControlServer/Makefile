
TARGET=DuetControlServer

include ../../Makefile.components

# ---------------------------------------------------------------------------
# Native SPI interface (libduet_realtime_core.so)
#
# The SPI transfer loop lives in C++ (../DuetRealtimeCore) so it can run on a pinned real-time
# thread; DuetControlServer P/Invokes into it via Link/Native. The .so therefore has to be built for
# the same architecture as the managed output and shipped alongside it.
#
# NOTE ON GLIBC: a shared library must link glibc dynamically, so a cross-built .so must not require
# a newer glibc than the target has. The devcontainer is Debian Bookworm and its cross toolchain
# targets the same glibc 2.36 as Raspberry Pi OS Bookworm, so a cross-built .so loads on the Pi as-is
# and no sysroot is involved. To target an older release, pass one:
#
#     make DUET_REALTIME_CORE_SYSROOT=/path/to/pi-sysroot   (see src/DuetRealtimeCore/scripts/fetch-pi-sysroot.sh)
# ---------------------------------------------------------------------------

REALTIME_CORE_SRC_DIR := ../DuetRealtimeCore
REALTIME_CORE_BUILD_DIR := $(REALTIME_CORE_SRC_DIR)/build-$(ARCH)
REALTIME_CORE_LIB := $(REALTIME_CORE_BUILD_DIR)/src/libduet_realtime_core.so

# Map the package architecture onto a CMake toolchain file. An empty value means "build natively".
ifeq ($(ARCH),$(filter $(ARCH),armhf armhfp armv7h armv7hl))
    REALTIME_CORE_TOOLCHAIN := $(REALTIME_CORE_SRC_DIR)/cmake/arm-linux-gnueabihf.cmake
else ifeq ($(ARCH),$(filter $(ARCH),arm64 aarch64))
    REALTIME_CORE_TOOLCHAIN := $(REALTIME_CORE_SRC_DIR)/cmake/aarch64-linux-gnu.cmake
else
    REALTIME_CORE_TOOLCHAIN :=
endif

REALTIME_CORE_CMAKE_ARGS := -DCMAKE_BUILD_TYPE=Release
ifneq ($(REALTIME_CORE_TOOLCHAIN),)
    REALTIME_CORE_CMAKE_ARGS += -DCMAKE_TOOLCHAIN_FILE=$(abspath $(REALTIME_CORE_TOOLCHAIN))
endif
ifneq ($(DUET_REALTIME_CORE_SYSROOT),)
    REALTIME_CORE_CMAKE_ARGS += -DDUET_REALTIME_CORE_SYSROOT=$(abspath $(DUET_REALTIME_CORE_SYSROOT))
endif

.PHONY: native-lib

$(REALTIME_CORE_LIB):
	$(ECHO_PREFIX)$(TARGET_TITLE) cmake libduet_realtime_core "" Release $(ARCH)
	$(CMD_PREFIX) cmake -S $(REALTIME_CORE_SRC_DIR) -B $(REALTIME_CORE_BUILD_DIR) $(REALTIME_CORE_CMAKE_ARGS)
	$(CMD_PREFIX) cmake --build $(REALTIME_CORE_BUILD_DIR) --target duet_realtime_core_shared -j

native-lib: $(REALTIME_CORE_LIB)

# Both the build and publish outputs need the .so next to the assemblies so the default P/Invoke
# probing (application directory) resolves it without a custom DllImportResolver.
build: native-lib
	$(CMD_PREFIX) cp $(REALTIME_CORE_LIB) bin/$(CONFIG)/netcoreapp$(RUNTIME)/$(DOTNET_ARCH)/

publish: native-lib
	$(CMD_PREFIX) cp $(REALTIME_CORE_LIB) $(DESTDIR)

clean: clean-native-lib

.PHONY: clean-native-lib
clean-native-lib:
	$(CMD_PREFIX) rm -rf $(REALTIME_CORE_BUILD_DIR)
