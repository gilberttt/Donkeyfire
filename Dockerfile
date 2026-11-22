# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore only after the full source is copied: a restore from the .csproj alone
# produced a publish with no wwwroot/_framework/blazor.web.js, so the live buttons did nothing.
COPY src/ src/
RUN dotnet restore src/DonkeyFire.Web/DonkeyFire.Web.csproj
RUN dotnet publish src/DonkeyFire.Web/DonkeyFire.Web.csproj -c Release -o /app --no-restore
# Fail the build if Blazor's browser script is missing from the publish output
RUN test -f /app/wwwroot/_framework/blazor.web.js

# Run
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# The database, uploads and backups live on a mounted volume at /data (see fly.toml).
# Runs as root because a freshly mounted volume is root-owned; the container is the only thing on its VM.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__Default="Data Source=/data/donkeyfire.db" \
    Storage__Root=/data/uploads \
    Hosting__BehindProxy=true
EXPOSE 8080
ENTRYPOINT ["dotnet", "DonkeyFire.Web.dll"]
