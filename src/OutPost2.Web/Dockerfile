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
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
ENTRYPOINT ["dotnet", "OutPost2.Web.dll"]
