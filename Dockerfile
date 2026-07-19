FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine3.23 AS base
WORKDIR /app
# Only HTTP is exposed here. TLS must be terminated by a reverse proxy (nginx, Traefik, Caddy)
# placed in front of this container. Do not expose port 80 or run without a TLS proxy in production.
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
RUN apk add --no-cache icu-libs

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine3.23 AS publish
WORKDIR /src
COPY ["Directory.Packages.props", "."]
COPY ["Directory.Build.props", "."]
# Build-isolation config for the vendored SparkplugNet submodule (see
# external/Directory.Build.props): keeps the fork on its own settings, off central
# package management, and off GitVersion (no .git in the Docker build context).
COPY ["external/Directory.Packages.props", "external/"]
COPY ["external/Directory.Build.props", "external/"]
COPY ["external/Directory.Build.targets", "external/"]
# Project files first so `restore` is cached independently of source changes.
# Paths mirror the repo layout so the Web -> Shared -> SparkplugNet ProjectReferences resolve.
COPY ["src/MqttProbe.Web/MqttProbe.Web.csproj", "src/MqttProbe.Web/"]
COPY ["src/MqttProbe.Shared/MqttProbe.Shared.csproj", "src/MqttProbe.Shared/"]
COPY ["external/SparkplugNet/src/SparkplugNet/SparkplugNet.csproj", "external/SparkplugNet/src/SparkplugNet/"]
RUN dotnet restore "src/MqttProbe.Web/MqttProbe.Web.csproj"
COPY src/MqttProbe.Web/ src/MqttProbe.Web/
COPY src/MqttProbe.Shared/ src/MqttProbe.Shared/
COPY external/SparkplugNet/ external/SparkplugNet/
WORKDIR "/src/src/MqttProbe.Web"
# DisableGitVersionTask: the SparkplugNet submodule uses GitVersion.MsBuild, but the
# Docker build context has no .git (.dockerignore excludes it). A global property is
# guaranteed to win regardless of import ordering, so the submodule build never touches git.
RUN dotnet publish "MqttProbe.Web.csproj" -c Release -o /app/publish \
    /p:UseAppHost=false \
    /p:DebugType=none \
    /p:DebugSymbols=false \
    /p:DisableGitVersionTask=true

FROM base AS final
RUN addgroup -S appgroup && adduser -S -D -H -u 1000 -G appgroup appuser \
    && mkdir -p /app/config /app/Plugins \
    && chown -R appuser:appgroup /app/config /app/Plugins \
    && chmod -R 755 /app/config /app/Plugins
WORKDIR /app
COPY --from=publish /app/publish .
RUN mkdir -p /app/config /app/Plugins \
    && chown -R appuser:appgroup /app/config /app/Plugins
USER appuser
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 CMD wget -q -O - http://127.0.0.1:8080/health >/dev/null || exit 1
ENTRYPOINT ["dotnet", "MqttProbe.Web.dll"]
