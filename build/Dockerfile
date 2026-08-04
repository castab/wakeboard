FROM node:24-alpine AS ui
ARG WAKEBOARD_VERSION=dev
ENV VITE_WAKEBOARD_VERSION=$WAKEBOARD_VERSION
WORKDIR /ui
COPY ui/package.json ui/package-lock.json ./
RUN npm ci
COPY ui/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS test
WORKDIR /source
COPY src/Wakeboard.Server/Wakeboard.Server.csproj src/Wakeboard.Server/
COPY tests/Wakeboard.Tests/Wakeboard.Tests.csproj tests/Wakeboard.Tests/
RUN dotnet restore tests/Wakeboard.Tests/Wakeboard.Tests.csproj
COPY src/ src/
COPY tests/ tests/
COPY --from=ui /ui/dist/ src/Wakeboard.Server/EmbeddedUi/
RUN dotnet test tests/Wakeboard.Tests/Wakeboard.Tests.csproj -c Release --no-restore

FROM test AS build
ARG ASSEMBLY_VERSION=0.0.0
RUN dotnet restore src/Wakeboard.Server/Wakeboard.Server.csproj -r win-x64
RUN dotnet publish src/Wakeboard.Server/Wakeboard.Server.csproj -c Release -r win-x64 --self-contained true -o /publish --no-restore /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=None /p:Version=$ASSEMBLY_VERSION

FROM scratch AS export
COPY --from=build /publish/Wakeboard.exe /
