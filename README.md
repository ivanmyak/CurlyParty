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

1. Скопируйте шаблон и заполните токены:

   ```powershell
   Copy-Item env.example env.env
   ```

   - `DiscordToken` — токен Discord-бота;
   - `TelegramToken` — токен Telegram-бота;
   - при необходимости `Telegram__AdminUserIds__0` — Telegram ID администратора.

2. Положите модель piper в `models/tts-models` (по умолчанию
   `ru_RU-denis-medium.onnx`).

3. Запустите:

```powershell
scripts\compose-up.ps1            # основной оркестр
```

или напрямую:

```bash
docker compose up --build --force-recreate
```

Сервисы:

| Сервис | Адрес |
| --- | --- |
| RabbitMQ Management | http://localhost:15672 (`curlyparty` / `curlyparty_dev`) |
| IdentityService | http://localhost:8081/health |
| SpeechService | http://localhost:8082/health |
| PostgreSQL | `localhost:5432` (`curlyparty` / `curlyparty_dev`) |

## Оркестрация: основной и дебаговый варианты

В репозитории два взаимоисключающих compose-файла:

| Файл | Проект | Назначение |
| --- | --- | --- |
| `docker-compose.yml` | `curlyparty` | Самодостаточный вариант для клонирования: RabbitMQ, локальный PostgreSQL, `identity`, `speech`, боты и `piper-http`. |
| `docker-compose.debug.yml` | `curlyparty-debug` | Локальный дебаг: без локального PostgreSQL — `identity` ходит во внешнюю БД `postgresdb` через сеть `shared-net`; `piper-http` не публикует порт 5000. |

Правила:

- основной оркестр главнее: его запуск автоматически останавливает дебаговый;
- если поднят основной, дебаговый не стартует и сообщает:
  «Поднят основной оркестр. Для поднятия дебага — остановите основу»;
- у оркестров одинаковые `container_name`, поэтому одновременно они работать
  не могут.

Скрипт `scripts/compose-up.ps1` реализует эти правила и проверяет занятость
портов 5432 и 5000 перед запуском основного варианта:

```powershell
scripts\compose-up.ps1                     # поднять основной оркестр
scripts\compose-up.ps1 -Mode debug         # поднять дебаговый оркестр
scripts\compose-up.ps1 -Mode main -Down    # остановить основной оркестр
scripts\compose-up.ps1 -Mode debug -Down   # остановить дебаговый оркестр

scripts\compose-up.ps1 -NoBuild            # без пересборки образов
scripts\compose-up.ps1 -Force              # без проверки портов
```

Скрипт всегда запускает `up -d --build --force-recreate --remove-orphans`:
образы пересобираются по актуальным исходникам, а контейнеры пересоздаются
принудительно, даже если compose-файл и теги образов не менялись.

Файл переменных выбирается по режиму: для основного — `env.env`, для
дебагового — `debug.env`, а если его нет, то `env.env`. Можно указать явно:
`scripts\compose-up.ps1 -Mode debug -EnvFile .\debug.env`.

Эквивалентные команды без скрипта:

```bash
docker compose -f docker-compose.yml up -d --build --force-recreate --remove-orphans
docker compose --env-file debug.env -f docker-compose.debug.yml up -d --build --force-recreate --remove-orphans
docker compose -f docker-compose.debug.yml down --remove-orphans
```

В дебаговом оркестре данные Discord-бота и кэш озвучки лежат не в именованных
томах, а в локальных каталогах — их видно прямо в репозитории:

| Каталог на хосте | Куда монтируется | Что содержит |
| --- | --- | --- |
| `CurlyBot.DiscordBot/Data` | `/app/Data` | `activity_mapping.json`, `tracking.json` |
| `CurlyBot.DiscordBot/Data/tts-records` | `/data/tts` | WAV/OGG, сгенерированные SpeechService |

Оба каталога в `.gitignore`, поэтому записи озвучки не попадут в коммиты.

## Подготовка внешнего PostgreSQL (дебаговый режим)

Дебаговый оркестр использует `postgresdb` из стека `docker-compose.database.yml`
(файл лежит вне репозитория и не изменяется). В нём нужно один раз создать роль,
базу и выдать привилегии — скрипты лежат в `scripts/sql`:

| Скрипт | Где выполнять | Что делает |
| --- | --- | --- |
| `scripts/sql/01-create-role.sql` | база `postgres` | создаёт роль `curlyparty` с паролем `curlyparty_dev` |
| `scripts/sql/02-create-database.sql` | база `postgres` | создаёт базу `curlypartydb` с владельцем `curlyparty` |
| `scripts/sql/03-grant-privileges.sql` | база `curlypartydb` | выдаёт права на схему `public` |

Через Adminer (`http://localhost:9090`):

1. Войдите администратором PostgreSQL (`SuperAdmiral`).
2. Выберите базу `postgres` и выполните по очереди `01-create-role.sql`
   и `02-create-database.sql`.
3. Переключитесь на базу `curlypartydb` и выполните `03-grant-privileges.sql`.

Через `psql` в контейнере:

```powershell
docker exec -i postgresdb-container psql -U SuperAdmiral -d postgres < scripts/sql/01-create-role.sql
docker exec -i postgresdb-container psql -U SuperAdmiral -d postgres < scripts/sql/02-create-database.sql
docker exec -i postgresdb-container psql -U SuperAdmiral -d curlypartydb < scripts/sql/03-grant-privileges.sql
```

Параметры подключения задаются переменными `CURLYPARTY_DB_*` (см.
`env.example`). Значения по умолчанию в `docker-compose.debug.yml`:

```
Host=postgresdb;Port=5432;Database=curlypartydb;Username=curlyparty;Password=curlyparty_dev
```

Таблицы создаёт сам `IdentityService` при старте (`CREATE TABLE IF NOT EXISTS`).

## Переменные окружения

Основные настройки задаются через `env.env` (шаблон — `env.example`) или
переменные окружения.

| Переменная | Описание |
| --- | --- |
| `DiscordToken` | Токен Discord-бота |
| `TelegramToken` | Токен Telegram-бота |
| `Telegram__AdminUserIds__0`, `...__1` | Telegram ID администраторов |
| `IdentityService__BaseUrl` | Адрес IdentityService (в compose — `http://identity:8080`) |
| `ConnectionStrings__Postgres` | Строка подключения PostgreSQL |
| `CURLYPARTY_DB_HOST`, `CURLYPARTY_DB_PORT` | Внешняя БД для дебагового оркестра (по умолчанию `postgresdb:5432`) |
| `CURLYPARTY_DB_NAME`, `CURLYPARTY_DB_USER`, `CURLYPARTY_DB_PASSWORD` | База, роль и пароль внешней БД (по умолчанию `curlypartydb`, `curlyparty`, `curlyparty_dev`) |
| `RabbitMq__Host`, `RabbitMq__Port` | Подключение к RabbitMQ |
| `RabbitMq__UserName`, `RabbitMq__Password` | Учётные данные RabbitMQ |
| `Speech__EngineUrl` | Адрес piper-http (в compose — `http://tts:5000`) |
| `Speech__OutputPath` | Каталог кэша озвучки (в compose — `/data/tts`) |
| `VoiceSettings__MaxTextLength` | Максимальная длина текста для Discord |
| `Telegram__MaxTextLength` | Максимальная длина текста для Telegram |

## Команды

### Discord

- `/talk <text> [user] [is-personal-message] [is-in-current-service-only]` —
  озвучить текст. Параметр `user` поддерживает автодополнение: сверху идут
  участники текущего сервера, ниже — сохранённые аккаунты из базы связок
  (Discord и Telegram) с отображаемыми именами. Если
  `is-in-current-service-only=false`, копия уйдёт связанному аккаунту в Telegram.
- `/linkme <@username|ссылка|ID>` — запросить связку с аккаунтом другого сервиса.
- `/linkuser <@discord-user> <@username|ссылка|ID>` — связка администратором.
- `/unlinkme`, `/unlinkuser`, `/linkstatus` — управление и просмотр связок.

### Telegram

- `/talk <текст>` — озвучить в текущий чат (в группе — в группу).
- `/talk` без аргументов (в том числе выбор команды в меню команд Telegram)
  открывает мастер параметров: бот присылает сообщение с inline-кнопками
  «Текст», «Кому», «Личное», «Только здесь», «Озвучить» и «Отмена». Текст
  вводится обычным сообщением, флаги переключаются кнопками. Команда
  отправляется только после того, как задан обязательный текст, а готовая
  озвучка дублируется отправителю в личные сообщения Telegram.
- «Кому» показывает список зарегистрированных в базе людей, отсортированный
  по недавним контактам. По мере ввода ника список фильтруется (живой поиск),
  человека можно выбрать кнопкой или отправить `@username`, ID либо ссылку.
  Связанные аккаунты Discord и Telegram показываются как один человек; выбор
  «Кому» задаёт основного получателя, а копию на вторую платформу можно
  включить, сняв флаг «Только здесь».
- `/talk <@username|ссылка|ID> <текст> [true|false] [true|false]` — озвучить
  другому пользователю. Последние два флага: личное сообщение и «только
  текущий сервис». Поддерживаются и именованные флаги `--pm`, `--only`,
  `--cross`. Готовая озвучка всегда дублируется отправителю в личные сообщения
  Telegram, как и в Discord: получатель слышит её у себя, автор — у себя.
- `/cancel` — закрыть незавершённый мастер. Любая другая команда тоже
  прерывает сценарий. Черновик живёт 10 минут и хранится только в памяти бота.
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
автоматически сохраняются в IdentityService. Telegram не позволяет ботам
инициировать переписку, поэтому для связки пользователь Telegram должен сам
открыть бота и отправить `/start` — только после этого его `@username` попадёт
в базу и станет доступен для поиска.

Отображаемое имя хранится для каждой платформы отдельно в
`platform_accounts.display_name`: для Discord — имя в профиле или на сервере,
для Telegram — имя и фамилия из профиля. Оба имени видны в `/linkstatus`, в
подсказках `/talk` и в уведомлениях о связке.

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

Для запуска на внешней БД без локального PostgreSQL используйте дебаговый
оркестр (см. раздел «Оркестрация»).
