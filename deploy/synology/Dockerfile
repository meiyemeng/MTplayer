FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src

COPY Directory.Build.props global.json ./
COPY src/MTPlayer.Contracts/MTPlayer.Contracts.csproj src/MTPlayer.Contracts/
COPY src/WebHtv.Core/WebHtv.Core.csproj src/WebHtv.Core/
COPY src/WebHtv.Configuration/WebHtv.Configuration.csproj src/WebHtv.Configuration/
COPY src/WebHtv.Catalogue/WebHtv.Catalogue.csproj src/WebHtv.Catalogue/
COPY src/MTPlayer.Server/MTPlayer.Server.csproj src/MTPlayer.Server/
RUN dotnet restore src/MTPlayer.Server/MTPlayer.Server.csproj

COPY src/MTPlayer.Contracts/ src/MTPlayer.Contracts/
COPY src/WebHtv.Core/ src/WebHtv.Core/
COPY src/WebHtv.Configuration/ src/WebHtv.Configuration/
COPY src/WebHtv.Catalogue/ src/WebHtv.Catalogue/
COPY src/MTPlayer.Server/ src/MTPlayer.Server/
RUN dotnet publish src/MTPlayer.Server/MTPlayer.Server.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS final
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish/ ./
RUN mkdir -p /home/app/.aspnet/DataProtection-Keys \
    && chown -R "$APP_UID:$APP_UID" /app /home/app
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "MTPlayer.Server.dll"]
