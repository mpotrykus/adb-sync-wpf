# AdbSync server. Build on the target or with buildx, for example:
#   docker build --platform linux/arm64 -t adbsync-server .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0-noble AS build
ARG TARGETARCH
WORKDIR /src
COPY AdbSync.Core/AdbSync.Core.csproj AdbSync.Core/
COPY AdbSync.Engine/AdbSync.Engine.csproj AdbSync.Engine/
COPY AdbSync.Server/AdbSync.Server.csproj AdbSync.Server/
RUN dotnet restore AdbSync.Server/AdbSync.Server.csproj -a $TARGETARCH
COPY AdbSync.Core/ AdbSync.Core/
COPY AdbSync.Engine/ AdbSync.Engine/
COPY AdbSync.Server/ AdbSync.Server/
RUN dotnet publish AdbSync.Server/AdbSync.Server.csproj -c Release -a $TARGETARCH --self-contained false --no-restore -o /app

# Ubuntu's adb package is built for arm64 as well as amd64 (Google's platform-tools zip is x86-64 only), and
# 24.04 ships adb 34, which supports wireless pairing.
FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble
RUN apt-get update \
    && apt-get install -y --no-install-recommends adb tzdata \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV PATH="/app:${PATH}" \
    ADBSYNC_DATA_DIR=/data \
    ADBSYNC_PROJECTS_DIR=/projects \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
VOLUME ["/data"]
HEALTHCHECK --interval=30s --timeout=10s --start-period=20s --retries=3 CMD ["adbsync-server", "healthcheck"]
ENTRYPOINT ["adbsync-server"]
