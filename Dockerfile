FROM node:26-alpine AS frontend
WORKDIR /build/frontend
COPY frontend/package*.json ./
RUN npm ci --no-fund
COPY frontend/ ./
ARG VITE_MAPTILER_KEY
ARG VITE_MAP_STYLE_URL
# Keyless basemap: e.g. /map/netherlands.pmtiles, served from a volume mounted at MapData__Path.
ARG VITE_PMTILES_URL
ARG VITE_MAP_ASSETS_URL
ARG VITE_MAX_DISTANCE_KM=60
ARG VITE_MAX_CYCLING_DISTANCE_KM=100
ENV VITE_MAPTILER_KEY=$VITE_MAPTILER_KEY VITE_MAP_STYLE_URL=$VITE_MAP_STYLE_URL VITE_MAX_DISTANCE_KM=$VITE_MAX_DISTANCE_KM VITE_MAX_CYCLING_DISTANCE_KM=$VITE_MAX_CYCLING_DISTANCE_KM \
    VITE_PMTILES_URL=$VITE_PMTILES_URL VITE_MAP_ASSETS_URL=$VITE_MAP_ASSETS_URL
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /build
COPY global.json Directory.Build.props NuGet.Config ./
COPY src/Meander.Core/Meander.Core.csproj src/Meander.Core/
COPY src/Meander.Infrastructure/Meander.Infrastructure.csproj src/Meander.Infrastructure/
COPY src/Meander.Api/Meander.Api.csproj src/Meander.Api/
RUN dotnet restore src/Meander.Api/Meander.Api.csproj
COPY src/ src/
RUN dotnet publish src/Meander.Api/Meander.Api.csproj -c Release --no-restore -o /publish /p:UseAppHost=false
COPY --from=frontend /build/frontend/dist/ /publish/wwwroot/

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=backend /publish/ ./
RUN mkdir -p /app/data && chown $APP_UID /app/data
ENV ASPNETCORE_HTTP_PORTS=8080 ASPNETCORE_ENVIRONMENT=Production
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Meander.Api.dll"]
