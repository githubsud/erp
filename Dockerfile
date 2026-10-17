# API container (the web app is static and deployed separately, e.g. Cloudflare Pages).
# Build:  docker build -t qatar-erp-api .
# Run migrations (owner credentials) once per release, then the API (runtime credentials).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Modules/ src/Modules/
COPY src/Erp.Api/ src/Erp.Api/
RUN dotnet publish src/Erp.Api/Erp.Api.csproj -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# QuestPDF renders with its bundled engine; fontconfig keeps it quiet on slim Linux images.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_GCHeapHardLimitPercent=75
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --retries=3 CMD ["dotnet", "Erp.Api.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "Erp.Api.dll"]
