# Этот этап используется при запуске из VS в быстром режиме (по умолчанию для конфигурации отладки)
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg \
    libopus-dev \
    libsodium-dev \
    && rm -rf /var/lib/apt/lists/*

# Этот этап используется для сборки проекта службы
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["CurlyBot.DiscordBot/CurlyBot.DiscordBot.csproj", "CurlyBot.DiscordBot/"]
COPY ["CurlyBot.Contracts/CurlyBot.Contracts.csproj", "CurlyBot.Contracts/"]
COPY ["CurlyBot.Messaging/CurlyBot.Messaging.csproj", "CurlyBot.Messaging/"]
COPY ["CurlyBot.Clients/CurlyBot.Clients.csproj", "CurlyBot.Clients/"]
RUN dotnet restore "CurlyBot.DiscordBot/CurlyBot.DiscordBot.csproj"

COPY . .
WORKDIR "/src/CurlyBot.DiscordBot"
RUN dotnet build "CurlyBot.DiscordBot.csproj" -c $BUILD_CONFIGURATION -o /app/build

# Этот этап используется для публикации проекта службы, который будет скопирован на последний этап
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "CurlyBot.DiscordBot.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Этот этап используется в рабочей среде или при запуске из VS в обычном режиме (по умолчанию, когда конфигурация отладки не используется)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Копируем libdave.so ПРЯМО в корень папки с ботом
COPY ./CurlyBot.DiscordBot/libs/libdave.so /app/libdave.so
RUN chmod +x /app/libdave.so

ENTRYPOINT ["dotnet", "CurlyBot.DiscordBot.dll"]
