FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY AzureDevopsMCPSharp.csproj ./
RUN dotnet restore AzureDevopsMCPSharp.csproj

COPY . .
RUN dotnet publish AzureDevopsMCPSharp.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:5089
ENV AZDOMCP_Server__Host=0.0.0.0
ENV AZDOMCP_Server__Port=5089
ENV AZDOMCP_Server__Path=/mcp
ENV AZDOMCP_AzureDevOps__ReadOnly=true

COPY --from=build /app/publish .

EXPOSE 5089

ENTRYPOINT ["dotnet", "AzureDevopsMCPSharp.dll"]
