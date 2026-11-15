FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["CurlyBot.IdentityService/CurlyBot.IdentityService.csproj", "CurlyBot.IdentityService/"]
COPY ["CurlyBot.Contracts/CurlyBot.Contracts.csproj", "CurlyBot.Contracts/"]
COPY ["CurlyBot.Messaging/CurlyBot.Messaging.csproj", "CurlyBot.Messaging/"]
RUN dotnet restore "CurlyBot.IdentityService/CurlyBot.IdentityService.csproj"

COPY . .
WORKDIR "/src/CurlyBot.IdentityService"
RUN dotnet build "CurlyBot.IdentityService.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "CurlyBot.IdentityService.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CurlyBot.IdentityService.dll"]
