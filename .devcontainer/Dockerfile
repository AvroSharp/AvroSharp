# The .NET 10 SDK on Ubuntu 24.04, as on the ubuntu-latest runners; the .NET 8 and 9 SDKs come from the dotnet
# feature in devcontainer.json. clang and zlib1g-dev are the Native AOT prerequisites (the AotSmoke publish).
FROM mcr.microsoft.com/devcontainers/dotnet:1-10.0-noble

RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
