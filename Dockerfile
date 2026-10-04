# Aurora DB Manager: the Web host (UI + REST API + background workers) in one image.
#
#   build     compiles and publishes the application, and builds the migration bundle
#   runtime   the ASP.NET runtime plus the database client programs backups and restores run

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restored first, from the project files alone, so that this layer is reused until a dependency changes.
COPY src/AuroraDbManager.Api/AuroraDbManager.Api.csproj src/AuroraDbManager.Api/
COPY src/AuroraDbManager.Web/AuroraDbManager.Web.csproj src/AuroraDbManager.Web/
RUN dotnet restore src/AuroraDbManager.Web/AuroraDbManager.Web.csproj

COPY src/ src/
RUN dotnet publish src/AuroraDbManager.Web/AuroraDbManager.Web.csproj -c Release -o /out/app --no-restore

# The schema migrations as one program: `efbundle --connection "..."` brings a database up to date.
# Aurora never migrates by itself at startup; the "migrate" service of the compose file runs this.
RUN dotnet tool install dotnet-ef --tool-path /tools --version "10.0.*" \
 && /tools/dotnet-ef migrations bundle \
      --project src/AuroraDbManager.Api/AuroraDbManager.Api.csproj \
      --configuration Release \
      --output /out/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# pg_dump/pg_restore must be at least as new as the newest PostgreSQL an instance can run (17),
# which the distribution's own package is not: they come from the PostgreSQL project's repository.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl gnupg \
 && install -d /usr/share/postgresql-common/pgdg \
 && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
 && . /etc/os-release \
 && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt ${VERSION_CODENAME}-pgdg main" \
      > /etc/apt/sources.list.d/pgdg.list \
 && apt-get update \
 && apt-get install -y --no-install-recommends postgresql-client-17 mysql-client \
 && apt-get purge -y gnupg \
 && apt-get autoremove -y \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /out/app ./
COPY --from=build /out/efbundle /usr/local/bin/efbundle

# Backups, and the keys that encrypt instance passwords and session cookies: both must outlive the container.
VOLUME ["/var/lib/aurora/backups", "/var/lib/aurora/keys"]

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    Backups__Local__RootPath=/var/lib/aurora/backups

EXPOSE 8080

# Runs as root: it talks to the Docker daemon through the mounted socket, which is root's.
# See docs/security.md on what that access means.
ENTRYPOINT ["dotnet", "AuroraDbManager.Web.dll"]
