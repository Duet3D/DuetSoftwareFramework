
TARGET=DuetControlServer

include ../../Makefile.components

# ---------------------------------------------------------------------------
# Native SPI interface (libduet_sbc.so)
#
# The SPI transfer loop lives in C++ (../DuetSbcInterface) so it can run on a pinned real-time
# thread; DuetControlServer P/Invokes into it via Link/Native. The .so therefore has to be built for
# the same architecture as the managed output and shipped alongside it.
#
# NOTE ON GLIBC: a shared library must link glibc dynamically, so a cross-built .so requires the
# target's glibc at runtime. The devcontainer toolchain targets a newer glibc than Raspberry Pi OS
# Bookworm, so a cross-built .so will fail to load on the Pi with "GLIBC_2.3x not found". For release
# packages, either build on the target or pass a matching sysroot:
#
#     make DUET_SBC_SYSROOT=/path/to/pi-sysroot   (see src/DuetSbcInterface/scripts/fetch-pi-sysroot.sh)
#
# Building natively (ARCH matching the host) needs no sysroot and is what CI/dev use.
# ---------------------------------------------------------------------------

SBC_SRC_DIR := ../DuetSbcInterface
SBC_BUILD_DIR := $(SBC_SRC_DIR)/build-$(ARCH)
SBC_LIB := $(SBC_BUILD_DIR)/src/libduet_sbc.so

# Map the package architecture onto a CMake toolchain file. An empty value means "build natively".
ifeq ($(ARCH),$(filter $(ARCH),armhf armhfp armv7h armv7hl))
    SBC_TOOLCHAIN := $(SBC_SRC_DIR)/cmake/arm-linux-gnueabihf.cmake
else ifeq ($(ARCH),$(filter $(ARCH),arm64 aarch64))
    SBC_TOOLCHAIN := $(SBC_SRC_DIR)/cmake/aarch64-linux-gnu.cmake
else
    SBC_TOOLCHAIN :=
endif

SBC_CMAKE_ARGS := -DCMAKE_BUILD_TYPE=Release
ifneq ($(SBC_TOOLCHAIN),)
    SBC_CMAKE_ARGS += -DCMAKE_TOOLCHAIN_FILE=$(abspath $(SBC_TOOLCHAIN))
endif
ifneq ($(DUET_SBC_SYSROOT),)
    SBC_CMAKE_ARGS += -DDUET_SBC_SYSROOT=$(abspath $(DUET_SBC_SYSROOT))
endif

.PHONY: native-lib

$(SBC_LIB):
	$(ECHO_PREFIX)$(TARGET_TITLE) cmake libduet_sbc "" Release $(ARCH)
	$(CMD_PREFIX) cmake -S $(SBC_SRC_DIR) -B $(SBC_BUILD_DIR) $(SBC_CMAKE_ARGS)
	$(CMD_PREFIX) cmake --build $(SBC_BUILD_DIR) --target duet_sbc_shared -j

native-lib: $(SBC_LIB)

# Both the build and publish outputs need the .so next to the assemblies so the default P/Invoke
# probing (application directory) resolves it without a custom DllImportResolver.
build: native-lib
	$(CMD_PREFIX) cp $(SBC_LIB) bin/$(CONFIG)/netcoreapp$(RUNTIME)/$(DOTNET_ARCH)/

publish: native-lib
	$(CMD_PREFIX) cp $(SBC_LIB) $(DESTDIR)

clean: clean-native-lib

.PHONY: clean-native-lib
clean-native-lib:
	$(CMD_PREFIX) rm -rf $(SBC_BUILD_DIR)
