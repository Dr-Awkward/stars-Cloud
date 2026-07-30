# Galaxies AI participant runner, galaxies-ai (M3).
#
# Multi-stage build to a small Linux container. The build context is the
# repository root (see .dockerignore), and the copy list is deliberately tight so
# the restore layer survives code-only changes.
#
# Note what is NOT copied: ServerState. galaxies-ai sits on the client side of the
# engine boundary and must never be able to link the turn engine. Keeping the
# turn engine out of the image is a cheaper guarantee than keeping it out by
# convention.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Common/ Common/
COPY AiContract/ AiContract/
COPY ControlPlane/ ControlPlane/
COPY AiService/ AiService/
COPY components.xml components.xml
# Compiled into Common by link, not by project reference. Without it restore
# succeeds and publish fails with CS2001.
COPY VersionInfo.cs VersionInfo.cs

RUN dotnet restore AiService/Galaxies.AiService.csproj
RUN dotnet publish AiService/Galaxies.AiService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Cloud Run provides PORT; ASP.NET Core binds it via ASPNETCORE_URLS. 8082 is the
# galaxies-ai port (spec header).
ENV ASPNETCORE_URLS=http://0.0.0.0:8082
EXPOSE 8082

# Run as a non-root user.
RUN useradd --uid 10001 --no-create-home --shell /usr/sbin/nologin galaxies
USER 10001

ENTRYPOINT ["dotnet", "Galaxies.AiService.dll"]
