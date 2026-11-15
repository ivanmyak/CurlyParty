FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

RUN apt-get update && apt-get install -y --no-install-recommends \
    ffmpeg \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["CurlyBot.SpeechService/CurlyBot.SpeechService.csproj", "CurlyBot.SpeechService/"]
COPY ["CurlyBot.Contracts/CurlyBot.Contracts.csproj", "CurlyBot.Contracts/"]
COPY ["CurlyBot.Messaging/CurlyBot.Messaging.csproj", "CurlyBot.Messaging/"]
RUN dotnet restore "CurlyBot.SpeechService/CurlyBot.SpeechService.csproj"

COPY . .
WORKDIR "/src/CurlyBot.SpeechService"
RUN dotnet build "CurlyBot.SpeechService.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "CurlyBot.SpeechService.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CurlyBot.SpeechService.dll"]
