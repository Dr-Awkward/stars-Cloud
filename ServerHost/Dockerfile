# Galaxies turn-generation worker (M0).
# Multi-stage build to a small Linux container. The engine must already be
# headless (net10.0, no WinForms, no System.Drawing on the turn path) for this to
# build; see Documentation/Cloud/M0.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the projects the host needs and restore. Keeping the copy list tight makes
# the layer cache useful across code-only changes.
COPY Common/ Common/
COPY ServerState/ ServerState/
COPY ServerHost/ ServerHost/
COPY components.xml components.xml
# Compiled into Common and ServerState by link, not by project reference. Without
# it restore succeeds and publish fails with CS2001.
COPY VersionInfo.cs VersionInfo.cs

RUN dotnet restore ServerHost/Nova.Server.Host.csproj
RUN dotnet publish ServerHost/Nova.Server.Host.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Cloud Run provides PORT; ASP.NET Core binds it via ASPNETCORE_URLS.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

# Run as a non-root user.
RUN useradd --uid 10001 --no-create-home --shell /usr/sbin/nologin galaxies
USER 10001

ENTRYPOINT ["dotnet", "Nova.Server.Host.dll"]
