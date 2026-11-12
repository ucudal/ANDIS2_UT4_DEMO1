FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY UsersService/UsersService.csproj UsersService/
COPY Common/Common.csproj Common/

RUN dotnet restore UsersService/UsersService.csproj

COPY UsersService/. UsersService/
COPY Common/. Common/

WORKDIR /src/UsersService
RUN dotnet publish -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/out ./

EXPOSE 8000
ENV ASPNETCORE_URLS=http://0.0.0.0:8000

# Una imagen por versión, para las demos de Kubernetes, de la 32 a la 34:
#   docker build --build-arg RELEASE_VERSION=2 -t users-service:v2 ...
# La versión 3 se construye además con CHAOS_FAILURE_RATE=0.3: es una versión
# con un error, que falla en el 30% de las solicitudes desde que arranca.
# Sin argumentos, es la versión 1 sin fallas, como la usa Docker Compose. Ahí
# users-service-v2 usa esta misma imagen y pisa RELEASE__VERSION.
ARG RELEASE_VERSION=1
ARG CHAOS_FAILURE_RATE=0
ENV RELEASE__VERSION=$RELEASE_VERSION
ENV CHAOS__FAILURERATE=$CHAOS_FAILURE_RATE

ENTRYPOINT ["dotnet", "UsersService.dll"]
