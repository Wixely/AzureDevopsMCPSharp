# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY NuGet.config global.json Directory.Build.props Directory.Packages.props ./
COPY AzureDevopsMCPSharp.csproj ./
RUN dotnet restore AzureDevopsMCPSharp.csproj

COPY . .
RUN dotnet publish AzureDevopsMCPSharp.csproj \
    -c Release \
    --no-restore \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

ENV DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    AZDOMCP_Server__Host=0.0.0.0 \
    AZDOMCP_Server__Port=5700 \
    AZDOMCP_Server__Path=/mcp \
    AZDOMCP_AzureDevOps__ReadOnly=true

RUN mkdir -p /app/logs && chown -R $APP_UID:0 /app
COPY --from=build --chown=$APP_UID:0 /app/publish ./

USER $APP_UID
EXPOSE 5700
VOLUME ["/app/logs"]

ENTRYPOINT ["dotnet", "AzureDevopsMCPSharp.dll"]
