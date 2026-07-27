ARG VARIANT=bookworm
FROM mcr.microsoft.com/devcontainers/base:${VARIANT}

ENV ARM_GNU_TOOLCHAIN_VERSION="15.2.rel1"
ENV ARM_GNU_TOOLCHAIN_DIR="/opt/arm-gnu-toolchain-${ARM_GNU_TOOLCHAIN_VERSION}"
ENV PATH="${ARM_GNU_TOOLCHAIN_DIR}/bin:${PATH}"

# ARG DEBIAN_FRONTEND=noninteractive

COPY scripts/install-arm-gcc.sh /tmp/

RUN echo "Installing ARM GNU Toolchain version ${ARM_GNU_TOOLCHAIN_VERSION}..." \
    && sed -i 's/\r$//' /tmp/install-arm-gcc.sh \
    && chmod +x /tmp/install-arm-gcc.sh \
    && /tmp/install-arm-gcc.sh \
    && rm -f /tmp/install-arm-gcc.sh

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        git \
        gnupg \
        jq \
        libxml2-utils \
        make \
        npm \
        rsync \
        unzip \
        zip \
        openssh-client \
        software-properties-common \
        build-essential \
        cmake \
        cppcheck \
        valgrind \
        clang \
        clang-format-19 \
        clang-tidy-19 \
        lldb \
        llvm \
        gdb \
        gdb-multiarch \
        crossbuild-essential-arm64 \
        crossbuild-essential-armhf
    # && rm -rf /var/lib/apt/lists/*

# The AOT targets ask clang for the armv7-linux-gnueabihf triple, but Debian names its cross tools
# arm-linux-gnueabihf-*. Without matching names clang silently falls back to the host x86 linker
RUN ln -s arm-linux-gnueabihf-ld /usr/bin/armv7-linux-gnueabihf-ld \
	&& ln -s arm-linux-gnueabihf-ld.bfd /usr/bin/armv7-linux-gnueabihf-ld.bfd \
	&& ln -s arm-linux-gnueabihf-objcopy /usr/bin/armv7-linux-gnueabihf-objcopy

# Debian only ships versioned names for these. DuetSbcInterface's CMakeLists looks for unsuffixed
# `clang-tidy` (find_program NAMES clang-tidy), so without these the default presets fail to build.
RUN ln -s clang-tidy-19 /usr/bin/clang-tidy \
	&& ln -s clang-format-19 /usr/bin/clang-format

RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
	&& bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
	&& ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet \
	&& rm /tmp/dotnet-install.sh

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
	DOTNET_NOLOGO=1 \
	DOTNET_CLI_HOME=/tmp \
	DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true
