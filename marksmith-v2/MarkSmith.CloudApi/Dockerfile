# Multi-stage Dockerfile for MarkSmith Cloud Compiler API (Deployable to Render.com)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy Directory.Build props & targets
COPY marksmith-v2/Directory.Build.props ./marksmith-v2/
COPY marksmith-v2/Directory.Build.targets ./marksmith-v2/

# Copy csproj files for layer caching
COPY marksmith-v2/MarkSmith.Core/MarkSmith.Core.csproj ./marksmith-v2/MarkSmith.Core/
COPY marksmith-v2/MarkSmith.CloudApi/MarkSmith.CloudApi.csproj ./marksmith-v2/MarkSmith.CloudApi/

RUN dotnet restore marksmith-v2/MarkSmith.CloudApi/MarkSmith.CloudApi.csproj

# Copy source trees
COPY marksmith-v2/MarkSmith.Core/ ./marksmith-v2/MarkSmith.Core/
COPY marksmith-v2/MarkSmith.CloudApi/ ./marksmith-v2/MarkSmith.CloudApi/

# Publish Release
RUN dotnet publish marksmith-v2/MarkSmith.CloudApi/MarkSmith.CloudApi.csproj -c Release -o /app/publish --no-restore

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Render dynamically injects $PORT (default 5000)
ENV PORT=5000
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 5000

ENTRYPOINT ["dotnet", "MarkSmith.CloudApi.dll"]
