# The server of the village, as Coolify builds and runs it. Only the server and what it needs: no tests.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /source
# Coolify passes the commit when "Include Source Commit in Build" is on
ARG SOURCE_COMMIT=dev
COPY Directory.Build.props ./
COPY src/ src/
COPY data/ data/
RUN dotnet publish src/Codery.Village.Server -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
# the calendar runs on Amsterdam time; busybox wget is there for the health check
RUN apk add --no-cache tzdata
WORKDIR /app
COPY --from=build /app ./
ARG SOURCE_COMMIT=dev
ENV VILLAGE_COMMIT=$SOURCE_COMMIT
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s CMD wget -qO- http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "Codery.Village.Server.dll"]
