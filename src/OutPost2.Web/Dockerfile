# Build from the repo root:  docker compose build web
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY OutPost2.slnx ./
COPY src/OutPost2.Core/OutPost2.Core.csproj              src/OutPost2.Core/
COPY src/OutPost2.Infrastructure/OutPost2.Infrastructure.csproj src/OutPost2.Infrastructure/
COPY src/OutPost2.Web/OutPost2.Web.csproj                src/OutPost2.Web/
COPY tests/OutPost2.Tests/OutPost2.Tests.csproj          tests/OutPost2.Tests/
RUN dotnet restore src/OutPost2.Web/OutPost2.Web.csproj

COPY . .
RUN dotnet publish src/OutPost2.Web/OutPost2.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Trust the private CA so the container can reach a LAN Vault over TLS when
# OutPost2__Vault__Address points at the real cluster Vault instead of vault-dev.
# Mount the cert at /usr/local/share/ca-certificates/lab-ca.crt to activate.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

# A started container is not a serving one, and compose has no other way to tell them
# apart. /healthz is liveness-only and deliberately unauthenticated, so it is already
# the right probe; curl is installed above for the CA step and is reused here rather
# than pulled in twice.
HEALTHCHECK --interval=30s --timeout=3s --start-period=15s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/healthz || exit 1

EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

# Drop root before the entrypoint (OP2-144). `app` (uid/gid 1654) ships in the aspnet
# image, so this costs nothing and needs no adduser.
#
# Three things make this safe to do unconditionally here: the app binds 8080, which
# needs no capability; nothing under /app is written at runtime, and leaving it owned
# by root means the process cannot rewrite its own binaries; and the data protection
# keys live in Postgres rather than on disk (OP2-56), so there is no key ring needing
# a writable path. Staging buffers go to the temp directory, which stays writable.
#
# Anything added later that DOES need to write — a cache, a key ring, a socket — needs
# its own directory chowned to $APP_UID, not a return to root.
USER $APP_UID

ENTRYPOINT ["dotnet", "OutPost2.Web.dll"]
