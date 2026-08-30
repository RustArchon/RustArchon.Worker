# Build context is this repo's own root (see docker-compose.yml). Unlike RustArchon.Api/.Panel, this
# project has no JumpStart dependency at all.

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["RustArchon.Worker/RustArchon.Worker.csproj", "RustArchon.Worker/"]
COPY ["RustArchon.Messaging/RustArchon.Messaging.csproj", "RustArchon.Messaging/"]
COPY ["RustArchon.Rcon/RustArchon.Rcon.csproj", "RustArchon.Rcon/"]
RUN dotnet restore "RustArchon.Worker/RustArchon.Worker.csproj"

COPY ["RustArchon.Worker/", "RustArchon.Worker/"]
COPY ["RustArchon.Messaging/", "RustArchon.Messaging/"]
COPY ["RustArchon.Rcon/", "RustArchon.Rcon/"]
WORKDIR "/src/RustArchon.Worker"
RUN dotnet build "RustArchon.Worker.csproj" -c $BUILD_CONFIGURATION -o /app/build --no-restore

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "RustArchon.Worker.csproj" -c $BUILD_CONFIGURATION -o /app/publish --no-restore /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RustArchon.Worker.dll"]
