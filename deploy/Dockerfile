# The site's image (J1, D-127): the frontend built with Node, the backend published with the .NET SDK, both served by
# one ASP.NET Core process on the runtime image. Debian (glibc) on purpose: NetVips ships its native libvips for
# linux-x64 on glibc (D-108). Build from the repository root: docker build -f deploy/Dockerfile .

# ---- Frontend ----
# Base images pinned by digest (Dependabot keeps them fresh): a rebuild gives the same image
FROM node:22-bookworm-slim@sha256:43ac6c60b8f89723f746e8a92ce91abd5017e627ce1ddfe4238355d3a30b772c AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

# ---- Backend ----
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
WORKDIR /src
COPY global.json nuget.config Directory.Build.props Directory.Packages.props .editorconfig ./
# The engine carries the default rules inside it (docs/RULESET.md)
COPY docs/ruleset.default.json docs/
COPY src/GameEvent.Engine/GameEvent.Engine.csproj src/GameEvent.Engine/
COPY src/GameEvent.Infrastructure/GameEvent.Infrastructure.csproj src/GameEvent.Infrastructure/
COPY src/GameEvent.Web/GameEvent.Web.csproj src/GameEvent.Web/
RUN dotnet restore src/GameEvent.Web/GameEvent.Web.csproj
COPY src/GameEvent.Engine/ src/GameEvent.Engine/
COPY src/GameEvent.Infrastructure/ src/GameEvent.Infrastructure/
COPY src/GameEvent.Web/ src/GameEvent.Web/
RUN dotnet publish src/GameEvent.Web/GameEvent.Web.csproj -c Release --no-restore -o /app

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f AS runtime
# The release tag (J4, D-201): the site reports it, a page that saw another one offers «Что нового»
ARG VERSION=dev
WORKDIR /app
COPY --from=build /app ./
COPY --from=web /src/web/dist ./frontend

# Production unless the compose file says Staging: test endpoints and local cookies never switch on by accident (D-120)
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    Frontend__DistPath=/app/frontend \
    ConnectionStrings__Main="Data Source=/data/game-event.db" \
    Files__Path=/data/files \
    Logging__File__Path=/data/logs/game-event-.log \
    DataProtection__KeysPath=/data/keys \
    Site__LockFile=/data/site.lock

# Everything the site keeps lives in /data: the database, its maintenance flag, uploaded files, logs and the keys that
# sign sessions (a new container keeps everyone signed in)
RUN mkdir -p /data && chown app:app /data
USER app
VOLUME /data
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 CMD ["dotnet", "GameEvent.Web.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "GameEvent.Web.dll"]
