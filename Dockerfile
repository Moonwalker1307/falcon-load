# The web UI and JSON API. Build from the repository root:
#   docker build -t falcon-load .
#   docker run --rm -p 127.0.0.1:5299:8080 -v falcon-load-data:/data falcon-load
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore on its own layer, so a code change doesn't download the packages again.
COPY global.json ./
COPY src/FalconLoad.Core/FalconLoad.Core.csproj src/FalconLoad.Core/
COPY src/FalconLoad.Web/FalconLoad.Web.csproj src/FalconLoad.Web/
RUN dotnet restore src/FalconLoad.Web/FalconLoad.Web.csproj

COPY src/FalconLoad.Core/ src/FalconLoad.Core/
COPY src/FalconLoad.Web/ src/FalconLoad.Web/
RUN dotnet publish src/FalconLoad.Web/FalconLoad.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Saved tests live in /data, so mount a volume there to keep them when the container is replaced.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    FalconLoad__DataDirectory=/data
RUN mkdir /data && chown "$APP_UID" /data
VOLUME /data

COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "FalconLoad.Web.dll"]
