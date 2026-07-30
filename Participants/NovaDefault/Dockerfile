# Galaxies built-in AI participant, "galaxies.default-ai" (M3).
#
# Multi-stage build to a small Linux container. This image is a PARTICIPANT: it
# answers POST /v1/act for one seat and holds no storage, no Firestore client,
# and no credentials. Its manifest declares network "none", and nothing in the
# image needs egress, so that declaration is enforceable rather than aspirational.
#
# CONCURRENCY 1. Nova.Common.Components.AllComponents and GameSettings.Data are
# process-wide statics in the ported engine. AllComponents is written once at
# startup and read thereafter, but the AI's full call graph has not been proven
# free of lazy static mutation on a request thread, so the safe setting is one
# request at a time per instance:
#
#   gcloud run deploy ai-nova-default --concurrency=1 ...
#
# The process serializes /v1/act internally as well, so a misconfigured
# concurrency does not become a data race; it just queues. The manifest's
# max_concurrency of 20 is a fan-out budget across instances, not per instance.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Keep the copy list tight so the layer cache survives code-only changes. The AI
# sources live under Nova/Ai and are LINKED into Ai/Nova.Ai.csproj rather than
# copied, so that one subdirectory has to come along even though no Nova project
# is built here. Only Nova/Ai: the rest of Nova/ is the WinForms desktop client
# and is excluded in .dockerignore, which has a matching exception for this path.
COPY Common/ Common/
COPY Nova/Ai/ Nova/Ai/
COPY Ai/ Ai/
COPY AiContract/ AiContract/
COPY Participants/NovaDefault/ Participants/NovaDefault/
COPY components.xml components.xml
# Compiled into Common by link, not by project reference. Without it restore
# succeeds and publish fails with CS2001.
COPY VersionInfo.cs VersionInfo.cs

RUN dotnet restore Participants/NovaDefault/Galaxies.Participant.NovaDefault.csproj
RUN dotnet publish Participants/NovaDefault/Galaxies.Participant.NovaDefault.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# The component definitions are a runtime dependency, not sample data: without
# them the AI cannot cost a factory, price a hull, or validate a design, and
# /readyz reports 503. The publish step already drops a copy next to the
# assembly; naming it explicitly means the container never depends on that.
COPY --from=build /src/components.xml /app/components.xml
ENV GALAXIES_COMPONENT_FILE=/app/components.xml

# 8080 api, 8081 turngen, 8082 galaxies-ai, 8083 notifier, 8084 this participant.
# Cloud Run injects PORT and overrides ASPNETCORE_URLS anyway.
ENV ASPNETCORE_URLS=http://0.0.0.0:8084
EXPOSE 8084

# Ships dark. The service is inert until somebody sets this to true, and the
# build arg only decides the image DEFAULT: it stays an ordinary environment
# variable, so a deploy can flip it without a rebuild.
ARG PARTICIPANT_ENABLED=false
ENV PARTICIPANT_ENABLED=${PARTICIPANT_ENABLED}

# Run as a non-root user.
RUN useradd --uid 10001 --no-create-home --shell /usr/sbin/nologin galaxies
USER 10001

ENTRYPOINT ["dotnet", "Galaxies.Participant.NovaDefault.dll"]
