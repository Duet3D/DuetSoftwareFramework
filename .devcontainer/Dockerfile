FROM mcr.microsoft.com/dotnet/sdk:10.0

ARG DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        git \
        jq \
        libxml2-utils \
        make \
        npm \
        rsync \
        unzip \
        zip \
    && rm -rf /var/lib/apt/lists/*
