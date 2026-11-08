FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY PurchasesService/PurchasesService.csproj PurchasesService/

RUN dotnet restore PurchasesService/PurchasesService.csproj

COPY PurchasesService/. PurchasesService/

WORKDIR /src/PurchasesService
RUN dotnet publish -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/out ./

EXPOSE 8000
ENV ASPNETCORE_URLS=http://0.0.0.0:8000

ENTRYPOINT ["dotnet", "PurchasesService.dll"]
