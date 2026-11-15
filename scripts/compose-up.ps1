#requires -Version 5.1
<#
.SYNOPSIS
    Запускает или останавливает оркестрацию CurlyParty.

.DESCRIPTION
    Поддерживает два взаимоисключающих варианта оркестрации:

      * main  — docker-compose.yml, самодостаточный: RabbitMQ, локальный
                PostgreSQL, IdentityService, SpeechService, боты и piper-http;
      * debug — docker-compose.debug.yml, без локального PostgreSQL:
                IdentityService подключается к внешнему `postgresdb`
                (стек docker-compose.database.yml, сеть shared-net),
                piper-http не публикует порт 5000.

    Основной оркестр имеет приоритет: при его запуске работающий дебаговый
    оркестр останавливается автоматически. Дебаговый оркестр не стартует,
    пока работает основной, — сначала остановите основу.

.PARAMETER Mode
    Вариант оркестрации: main (по умолчанию) или debug.

.PARAMETER Down
    Остановить и удалить контейнеры выбранного оркестра (тома сохраняются).

.PARAMETER NoBuild
    Не пересобирать образы при запуске.

.PARAMETER Force
    Не проверять занятость портов 5432 и 5000 перед запуском основного оркестра.

.PARAMETER EnvFile
    Файл с переменными для интерполяции compose. По умолчанию `env.env`,
    если он существует.

.EXAMPLE
    scripts\compose-up.ps1
    Поднять основной оркестр (с пересборкой образов).

.EXAMPLE
    scripts\compose-up.ps1 -Mode debug
    Поднять дебаговый оркестр на внешнем PostgreSQL.

.EXAMPLE
    scripts\compose-up.ps1 -Mode main -Down
    Остановить основной оркестр.
#>
[CmdletBinding()]
param(
    [ValidateSet('main', 'debug')]
    [string]$Mode = 'main',

    [switch]$Down,

    [switch]$NoBuild,

    [switch]$Force,

    [string]$EnvFile = 'env.env'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 по умолчанию пишет в консоль в кодировке системы,
# из-за чего кириллица превращается в «кракозябры». Принудительно UTF-8.
try {
    [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $OutputEncoding = [System.Text.UTF8Encoding]::new($false)
}
catch {
    # Смена кодировки консоли непринципиальна — продолжаем работу.
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$orchestrations = @{
    main  = @{
        File    = 'docker-compose.yml'
        Project = 'curlyparty'
        Title   = 'основной оркестр'
    }
    debug = @{
        File    = 'docker-compose.debug.yml'
        Project = 'curlyparty-debug'
        Title   = 'дебаговый оркестр'
    }
}

$current = $orchestrations[$Mode]
$other = $orchestrations[$(if ($Mode -eq 'main') { 'debug' } else { 'main' })]

function Test-ComposeProjectRunning {
    <#
    .SYNOPSIS
        Проверяет, есть ли запущенные контейнеры указанного compose-проекта.
    #>
    param(
        [Parameter(Mandatory)][string]$Project
    )

    $ids = & docker ps --filter "label=com.docker.compose.project=$Project" --quiet
    if ($LASTEXITCODE -ne 0) {
        throw 'Не удалось получить список контейнеров Docker. Проверьте, что Docker Desktop запущен.'
    }

    return [bool]($ids | Where-Object { $_ })
}

function Get-ContainerUsingPort {
    <#
    .SYNOPSIS
        Возвращает имя контейнера, опубликовавшего указанный порт.
    #>
    param(
        [Parameter(Mandatory)][int]$Port
    )

    $lines = & docker ps --format '{{.Names}}|{{.Ports}}'
    if ($LASTEXITCODE -ne 0) {
        throw 'Не удалось получить список контейнеров Docker.'
    }

    foreach ($line in $lines) {
        $parts = $line -split '\|', 2
        if ($parts.Length -lt 2) { continue }
        if ($parts[1] -match ":$Port->") { return $parts[0] }
    }

    return $null
}

function Invoke-Compose {
    <#
    .SYNOPSIS
        Выполняет команду docker compose и возвращает её код выхода.
    #>
    param(
        [Parameter(Mandatory)][string[]]$ComposeArguments
    )

    & docker @ComposeArguments
    return $LASTEXITCODE
}

$composeBase = @('compose')
if ($EnvFile -and (Test-Path $EnvFile)) {
    $composeBase += @('--env-file', $EnvFile)
}

# --- Остановка выбранного оркестра -------------------------------------------
if ($Down) {
    Write-Host "Останавливаю $($current.Title) ($($current.Project))..." -ForegroundColor Cyan
    $exitCode = Invoke-Compose -ComposeArguments @(
        $composeBase + @('-f', $current.File, '-p', $current.Project, 'down', '--remove-orphans')
    )
    exit $exitCode
}

# --- Приоритет основного оркестра --------------------------------------------
if (Test-ComposeProjectRunning -Project $other.Project) {
    if ($Mode -eq 'debug') {
        Write-Host 'Поднят основной оркестр. Для поднятия дебага — остановите основу:' -ForegroundColor Yellow
        Write-Host '    scripts\compose-up.ps1 -Mode main -Down' -ForegroundColor Yellow
        exit 1
    }

    Write-Host "Найден работающий $($other.Title) — останавливаю его автоматически..." -ForegroundColor Yellow
    $exitCode = Invoke-Compose -ComposeArguments @(
        $composeBase + @('-f', $other.File, '-p', $other.Project, 'down', '--remove-orphans')
    )
    if ($exitCode -ne 0) {
        Write-Host "Не удалось остановить $($other.Title)." -ForegroundColor Red
        exit $exitCode
    }
}

# --- Проверка свободных портов для основного оркестра -------------------------
if ($Mode -eq 'main' -and -not $Force) {
    $conflicts = @()
    foreach ($port in 5432, 5000) {
        $owner = Get-ContainerUsingPort -Port $port
        if ($owner -and $owner -notlike 'curlyparty-*') {
            $conflicts += "  порт $port занят контейнером '$owner'"
        }
    }

    if ($conflicts.Count -gt 0) {
        Write-Host 'Основной оркестр поднимает собственные PostgreSQL (5432) и piper-http (5000),' -ForegroundColor Red
        Write-Host 'но нужные порты уже заняты:' -ForegroundColor Red
        $conflicts | ForEach-Object { Write-Host $_ -ForegroundColor Red }
        Write-Host ''
        Write-Host 'Варианты: используйте дебаговый оркестр на внешней БД:' -ForegroundColor Yellow
        Write-Host '    scripts\compose-up.ps1 -Mode debug' -ForegroundColor Yellow
        Write-Host 'или освободите порты (проверку можно отключить ключом -Force).' -ForegroundColor Yellow
        exit 1
    }
}

# --- Запуск ------------------------------------------------------------------
$composeArguments = $composeBase + @('-f', $current.File, '-p', $current.Project, 'up', '-d')
if (-not $NoBuild) {
    $composeArguments += '--build'
}

Write-Host "Поднимаю $($current.Title) ($($current.Project))..." -ForegroundColor Cyan
$exitCode = Invoke-Compose -ComposeArguments $composeArguments

if ($exitCode -ne 0) {
    Write-Host "Не удалось запустить $($current.Title)." -ForegroundColor Red
    exit $exitCode
}

Write-Host ''
Write-Host 'Готово:' -ForegroundColor Green
Write-Host '  RabbitMQ Management : http://localhost:15672 (curlyparty / curlyparty_dev)'
Write-Host '  IdentityService     : http://localhost:8081/health'
Write-Host '  SpeechService       : http://localhost:8082/health'
if ($Mode -eq 'main') {
    Write-Host '  PostgreSQL          : localhost:5432 (curlyparty / curlyparty_dev)'
}
else {
    $dbName = if ($env:CURLYPARTY_DB_NAME) { $env:CURLYPARTY_DB_NAME } else { 'curlypartydb' }
    $dbHost = if ($env:CURLYPARTY_DB_HOST) { $env:CURLYPARTY_DB_HOST } else { 'postgresdb' }
    Write-Host "  PostgreSQL          : $dbHost / $dbName (внешний, сеть shared-net)"
}

exit 0
