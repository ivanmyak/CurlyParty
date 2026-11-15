FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["CurlyBot.TelegramBot/CurlyBot.TelegramBot.csproj", "CurlyBot.TelegramBot/"]
COPY ["CurlyBot.Contracts/CurlyBot.Contracts.csproj", "CurlyBot.Contracts/"]
COPY ["CurlyBot.Messaging/CurlyBot.Messaging.csproj", "CurlyBot.Messaging/"]
COPY ["CurlyBot.Clients/CurlyBot.Clients.csproj", "CurlyBot.Clients/"]
RUN dotnet restore "CurlyBot.TelegramBot/CurlyBot.TelegramBot.csproj"

COPY . .
WORKDIR "/src/CurlyBot.TelegramBot"
RUN dotnet build "CurlyBot.TelegramBot.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "CurlyBot.TelegramBot.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

ENTRYPOINT ["dotnet", "CurlyBot.TelegramBot.dll"]
