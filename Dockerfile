FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY src/CraneoBarbero.Api/CraneoBarbero.Api.csproj src/CraneoBarbero.Api/
RUN dotnet restore src/CraneoBarbero.Api/CraneoBarbero.Api.csproj

COPY src/ src/
RUN dotnet publish src/CraneoBarbero.Api/CraneoBarbero.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    PORT=8080

COPY --from=build /app .

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "CraneoBarbero.Api.dll"]
