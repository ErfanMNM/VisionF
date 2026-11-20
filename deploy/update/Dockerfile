# Snapshot đầu vào được build-release.ps1 xuất từ Git tag sạch.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY . .
ARG RELEASE_VERSION
ARG UPDATE_PUBLIC_KEY
RUN test -n "$RELEASE_VERSION" && test -n "$UPDATE_PUBLIC_KEY" \
    && dotnet publish TanTienQR/TanTienQR.csproj -c Release -r linux-x64 \
       "-p:Version=$RELEASE_VERSION" "-p:UpdatePublicKey=$UPDATE_PUBLIC_KEY" -o /out/app \
    && dotnet publish tools/TanTienLauncher/TanTienLauncher.csproj -c Release -r linux-x64 \
       "-p:Version=$RELEASE_VERSION" "-p:UpdatePublicKey=$UPDATE_PUBLIC_KEY" -o /out/launcher

FROM scratch AS export
COPY --from=build /out/ /
