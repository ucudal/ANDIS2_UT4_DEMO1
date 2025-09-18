FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ProductsService/ProductsService.csproj ProductsService/
COPY Common/Common.csproj Common/

RUN dotnet restore ProductsService/ProductsService.csproj

COPY ProductsService/. ProductsService/
COPY Common/. Common/

WORKDIR /src/ProductsService
RUN dotnet publish -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app/out ./

EXPOSE 8000
ENV ASPNETCORE_URLS=http://0.0.0.0:8000

ENTRYPOINT ["dotnet", "ProductsService.dll"]
