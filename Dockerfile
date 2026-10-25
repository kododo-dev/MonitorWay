FROM node:22-alpine AS spa
WORKDIR /spa
COPY src/MonitorWay.UI/SPA/package*.json ./
RUN npm ci
COPY src/MonitorWay.UI/SPA/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ ./
COPY --from=spa /spa/dist ./MonitorWay.UI/SPA/dist
RUN dotnet publish MonitorWay.Demo.Web/MonitorWay.Demo.Web.csproj \
    --configuration Release \
    --framework net10.0 \
    --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# The aspnet image already listens on 8080 (ASPNETCORE_HTTP_PORTS); setting ASPNETCORE_URLS as well only adds a warning.
EXPOSE 8080
ENTRYPOINT ["dotnet", "Kododo.MonitorWay.Demo.Web.dll"]
