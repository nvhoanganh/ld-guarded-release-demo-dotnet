# Guarded Release demo (.NET 10) — deployable image for Railway.
#
# Railway auto-detects this Dockerfile and builds it. The app binds the PORT
# Railway injects (entrypoint sets ASPNETCORE_URLS from $PORT).
#
# SDK key: set LaunchDarkly__SdkKey in the Railway service variables.
# Deployed SHA for /api/status comes from Railway's RAILWAY_GIT_COMMIT_SHA.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY GuardedReleaseDemo.csproj ./
RUN dotnet restore GuardedReleaseDemo.csproj
COPY . .
RUN dotnet publish GuardedReleaseDemo.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
# Railway sets PORT; ASP.NET must listen on it. Default 8080 for local runs.
ENV PORT=8080
ENTRYPOINT ["/bin/sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT} dotnet GuardedReleaseDemo.dll"]
