FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY UsersService/UsersService.csproj UsersService/
COPY Common/Common.csproj Common/

RUN dotnet restore UsersService/UsersService.csproj

COPY UsersService/. UsersService/
COPY Common/. Common/

WORKDIR /src/UsersService
RUN dotnet publish -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app/out ./

EXPOSE 8000
ENV ASPNETCORE_URLS=http://0.0.0.0:8000

ENTRYPOINT ["dotnet", "UsersService.dll"]
