# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ValleySteward.ShareServer.csproj ./
RUN dotnet restore ValleySteward.ShareServer.csproj

COPY . ./
RUN dotnet publish ValleySteward.ShareServer.csproj \
    -c Release \
    --no-restore \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:5088
ENV VALLEY_STEWARD_SHARE_STORE=/data/shares.json

EXPOSE 5088
VOLUME ["/data"]

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ValleySteward.ShareServer.dll"]
