FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY BffMobile/BffMobile.csproj BffMobile/

RUN dotnet restore BffMobile/BffMobile.csproj

COPY BffMobile/. BffMobile/

WORKDIR /src/BffMobile
RUN dotnet publish -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/out ./

EXPOSE 8000
ENV ASPNETCORE_URLS=http://0.0.0.0:8000

ENTRYPOINT ["dotnet", "BffMobile.dll"]
