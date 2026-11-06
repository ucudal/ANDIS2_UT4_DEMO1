FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY MessagingBridge/MessagingBridge.csproj MessagingBridge/
COPY Common/Common.csproj Common/

RUN dotnet restore MessagingBridge/MessagingBridge.csproj

COPY MessagingBridge/. MessagingBridge/
COPY Common/. Common/

WORKDIR /src/MessagingBridge
RUN dotnet publish -c Release -o /app/out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/out ./

ENTRYPOINT ["dotnet", "MessagingBridge.dll"]
