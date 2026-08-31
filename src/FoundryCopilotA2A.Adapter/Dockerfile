FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/FoundryCopilotA2A.Adapter/FoundryCopilotA2A.Adapter.csproj", "src/FoundryCopilotA2A.Adapter/"]
COPY ["src/FoundryCopilotA2A.ServiceDefaults/FoundryCopilotA2A.ServiceDefaults.csproj", "src/FoundryCopilotA2A.ServiceDefaults/"]
RUN dotnet restore "src/FoundryCopilotA2A.Adapter/FoundryCopilotA2A.Adapter.csproj"

COPY . .
RUN dotnet publish "src/FoundryCopilotA2A.Adapter/FoundryCopilotA2A.Adapter.csproj" \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_ENVIRONMENT=Production

COPY --from=build /app/publish .
USER $APP_UID

ENTRYPOINT ["dotnet", "FoundryCopilotA2A.Adapter.dll"]

