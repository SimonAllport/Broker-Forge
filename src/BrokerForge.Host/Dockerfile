# Build from the repository root: docker build -f src/BrokerForge.Host/Dockerfile .
# Every source is a connection design read from Dataverse (HLD D-34); the image carries no descriptors or table schemas.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/ src/
COPY tools/SwaggerForge/ tools/SwaggerForge/
RUN dotnet publish src/BrokerForge.Host/BrokerForge.Host.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "BrokerForge.Host.dll"]
