FROM mcr.microsoft.com/devcontainers/dotnet:10.0
# FROM mcr.microsoft.com/devcontainers/cpp:2-ubuntu24.04

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
        clang-format-18 \
        clang-tidy-18 \
        lldb \
        llvm \
        gdb \
        crossbuild-essential-arm64 \
    && add-apt-repository -y ppa:ubuntu-toolchain-r/test \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        gcc-15 \
        g++-15 \
    && update-alternatives --install /usr/bin/clang-format clang-format /usr/bin/clang-format-18 100 \
    && update-alternatives --install /usr/bin/clang-tidy clang-tidy /usr/bin/clang-tidy-18 100 \
    && rm -rf /var/lib/apt/lists/*
