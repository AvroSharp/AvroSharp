# The .NET 10 SDK on Ubuntu 24.04, as on the ubuntu-latest runners, with the .NET 8 and 9 SDKs beside it, as CI's
# setup-dotnet installs them. clang and zlib1g-dev are the Native AOT prerequisites (the AotSmoke publish).
FROM mcr.microsoft.com/devcontainers/dotnet:10.0-noble

RUN apt-get update \
    && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && bash /tmp/dotnet-install.sh --channel 8.0 --install-dir /usr/share/dotnet \
    && bash /tmp/dotnet-install.sh --channel 9.0 --install-dir /usr/share/dotnet \
    && rm /tmp/dotnet-install.sh
