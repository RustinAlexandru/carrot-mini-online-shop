FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS source
WORKDIR /workspace
COPY global.json Directory.Build.props Shop.slnx ./
COPY .config/ .config/
COPY src/ src/
COPY tests/ tests/
RUN dotnet restore Shop.slnx

FROM source AS tests
CMD ["dotnet", "test", "Shop.slnx", "--configuration", "Release", "--no-restore", "--logger", "trx", "--results-directory", "/results"]

FROM source AS publish
RUN dotnet publish src/Shop.Api/Shop.Api.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
WORKDIR /app
COPY --from=publish /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Shop.Api.dll"]
