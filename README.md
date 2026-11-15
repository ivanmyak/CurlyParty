# CurlyParty

Решение с Discord- и Telegram-ботами, сервисом идентификации аккаунтов и
сервисом синтеза речи. Проекты лежат в корне решения, без промежуточной папки
`src`.

## Архитектура

```
Discord ──┐
          ├──► DiscordBot ───┐
Telegram ─┘                  │
                             ├──► RabbitMQ (curlybot.events)
                             │        │
                             │        ├──► SpeechService ──► piper-http (TTS)
                             │        │         └──► WAV/OGG на общем volume
                             │        │
                             │        └──► IdentityService ──► PostgreSQL
                             │
                             └── HTTP ──► IdentityService
```

### Проекты

| Проект | Назначение |
| --- | --- |
| `CurlyBot.Contracts` | DTO, интеграционные события, топология RabbitMQ, общие модели |
| `CurlyBot.Messaging` | Тонкая обёртка над `RabbitMQ.Client`: publisher, consumer, DLQ |
| `CurlyBot.Clients` | HTTP-клиент IdentityService и клиент TTS через RabbitMQ |
| `CurlyBot.DiscordBot` | Discord-бот: `/talk`, голосовые каналы, DM, связки аккаунтов |
| `CurlyBot.TelegramBot` | Telegram-бот: `/talk` в ЛС и группах, голосовые сообщения, связки |
| `CurlyBot.IdentityService` | Аккаунты, поиск пользователей, связки, HTTP API |
| `CurlyBot.SpeechService` | Синтез речи через piper-http, кэш WAV/OGG |

`CurlyBot.Domain` больше не используется: его контракты перенесены в
`CurlyBot.Contracts`.

## Быстрый запуск

1. Заполните `env.env`:
   - `DiscordToken` — токен Discord-бота;
   - `TelegramToken` — токен Telegram-бота;
   - при необходимости `Telegram__AdminUserIds__0` — Telegram ID администратора.

2. Положите модель piper в `models/tts-models` (по умолчанию
   `ru_RU-denis-medium.onnx`).

3. Запустите:

```bash
docker compose up --build
```

Сервисы:

| Сервис | Адрес |
| --- | --- |
| RabbitMQ Management | http://localhost:15672 (`curlybot` / `curlybot_dev`) |
| IdentityService | http://localhost:8081/health |
| SpeechService | http://localhost:8082/health |
| PostgreSQL | `localhost:5432` (`curlybot` / `curlybot_dev`) |

## Переменные окружения

Основные настройки задаются через `env.env` или переменные окружения.

| Переменная | Описание |
| --- | --- |
| `DiscordToken` | Токен Discord-бота |
| `TelegramToken` | Токен Telegram-бота |
| `Telegram__AdminUserIds__0`, `...__1` | Telegram ID администраторов |
| `IdentityService__BaseUrl` | Адрес IdentityService (в compose — `http://identity:8080`) |
| `ConnectionStrings__Postgres` | Строка подключения PostgreSQL |
| `RabbitMq__Host`, `RabbitMq__Port` | Подключение к RabbitMQ |
| `RabbitMq__UserName`, `RabbitMq__Password` | Учётные данные RabbitMQ |
| `Speech__EngineUrl` | Адрес piper-http (в compose — `http://tts:5000`) |
| `Speech__OutputPath` | Каталог кэша озвучки (в compose — `/data/tts`) |
| `VoiceSettings__MaxTextLength` | Максимальная длина текста для Discord |
| `Telegram__MaxTextLength` | Максимальная длина текста для Telegram |

## Команды

### Discord

- `/talk <text> [user] [is-personal-message] [is-in-current-service-only]` —
  озвучить текст. Если `is-in-current-service-only=false`, копия уйдёт
  связанному аккаунту в Telegram.
- `/linkme <@username|ссылка|ID>` — запросить связку с аккаунтом другого сервиса.
- `/linkuser <@discord-user> <@username|ссылка|ID>` — связка администратором.
- `/unlinkme`, `/unlinkuser`, `/linkstatus` — управление и просмотр связок.

### Telegram

- `/talk <текст>` — озвучить в текущий чат (в группе — в группу).
- `/talk <@username|ссылка|ID> <текст> [true|false] [true|false]` — озвучить
  другому пользователю. Последние два флага: личное сообщение и «только
  текущий сервис».
- `/linkme <@username|ссылка|ID>`, `/unlinkme`, `/linkstatus` — связки.
- `/linkuser <пользователь> <@username|ссылка|ID>` и `/unlinkuser` — команды
  администратора. Администратор определяется по `Telegram__AdminUserIds`
  или по правам администратора в группе.

## Как работает связка аккаунтов

1. Пользователь вызывает `/linkme` и указывает аккаунт в другом сервисе.
2. IdentityService ищет аккаунт в базе. Если найдено несколько — просит
   уточнить платформу, ссылку или ID. Если аккаунт заблокировал бота —
   сообщает об этом.
3. Целевому пользователю уходит запрос с кнопками «Подтвердить» / «Отклонить».
4. При подтверждении аккаунты объединяются в `link_group`.
5. При отклонении инициатор получает уведомление, повторная попытка
   ограничена 30 минутами (администратор может связать напрямую).

Учитываются только пользователи, которые когда-либо писали боту: их аккаунты
автоматически сохраняются в IdentityService.

## Локальная разработка

```bash
dotnet build CurlyParty.sln
```

Для запуска отдельного сервиса:

```bash
dotnet run --project CurlyBot.IdentityService
dotnet run --project CurlyBot.SpeechService
dotnet run --project CurlyBot.DiscordBot
dotnet run --project CurlyBot.TelegramBot
```

Infrastructure (RabbitMQ, PostgreSQL, piper) удобнее поднять через compose:

```bash
docker compose up rabbitmq postgres tts
```
