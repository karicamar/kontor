# Multi-stage build: the SDK image builds and tests, the small runtime image only runs.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet test --no-restore -c Release
RUN dotnet publish src/Kontor.Api -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Never run as root inside the container
RUN mkdir /data && chown $APP_UID /data
USER $APP_UID

ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Default="Data Source=/data/kontor.db" \
    Kontor__AuditJsonlPath=/data/kontor-audit.jsonl
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "Kontor.Api.dll"]
