# Build Native AOT cho Linux x64 (Ubuntu Server) ngay tren Windows qua Docker.
# Chay tu thu muc goc repo:  deploy\linux\publish-linux.ps1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY . .
RUN dotnet publish TanTienQR/TanTienQR.csproj -c Release -r linux-x64 -o /out

FROM scratch AS export
COPY --from=build /out /
