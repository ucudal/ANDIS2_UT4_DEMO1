FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY LegacyErp/LegacyErp.csproj LegacyErp/

RUN dotnet restore LegacyErp/LegacyErp.csproj

COPY LegacyErp/. LegacyErp/

WORKDIR /src/LegacyErp
RUN dotnet publish -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/out ./

ENTRYPOINT ["dotnet", "LegacyErp.dll"]
