# Galaxies public API gateway (M1/M2).
# Multi-stage build to a small Linux container. Depends on the headless engine
# (Common + ServerState), the shared control plane, and the Google SDKs.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the projects the API needs and restore. Keeping the copy list tight makes
# the layer cache useful across code-only changes.
COPY Common/ Common/
COPY ServerState/ ServerState/
COPY ControlPlane/ ControlPlane/
COPY Api/ Api/
COPY components.xml components.xml
# Compiled into Common and ServerState by link, not by project reference. Without
# it restore succeeds and publish fails with CS2001.
COPY VersionInfo.cs VersionInfo.cs

RUN dotnet restore Api/Galaxies.Api.csproj
RUN dotnet publish Api/Galaxies.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Cloud Run provides PORT; ASP.NET Core binds it via ASPNETCORE_URLS.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

# Run as a non-root user.
RUN useradd --uid 10001 --no-create-home --shell /usr/sbin/nologin galaxies
USER 10001

ENTRYPOINT ["dotnet", "Galaxies.Api.dll"]
