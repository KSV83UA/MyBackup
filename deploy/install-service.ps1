#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Установка (или удаление) службы MyBackup на сервере.

.EXAMPLE
  .\install-service.ps1                  # установить из текущей папки в C:\Program Files\MyBackup
  .\install-service.ps1 -Uninstall       # остановить и удалить службу (файлы и копии не трогаются)
#>
param(
    [string]$SourceDir = (Split-Path -Parent $PSCommandPath),
    [string]$InstallDir = "$env:ProgramFiles\MyBackup",
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$serviceName = 'MyBackup'
$dataDir = Join-Path $env:ProgramData 'MyBackup'

if ($Uninstall) {
    if (Get-Service $serviceName -ErrorAction SilentlyContinue) {
        Stop-Service $serviceName -ErrorAction SilentlyContinue
        sc.exe delete $serviceName | Out-Null
        Write-Host "Служба $serviceName удалена. Настройки и журналы остались в $dataDir."
    } else {
        Write-Host "Служба $serviceName не установлена."
    }
    return
}

foreach ($file in 'MyBackup.exe', 'restic.exe', 'config.example.json') {
    if (-not (Test-Path (Join-Path $SourceDir $file))) { throw "Не найден $file в $SourceDir" }
}

$existing = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -eq 'Running') { Stop-Service $serviceName }

New-Item -ItemType Directory -Force $InstallDir, $dataDir | Out-Null
Copy-Item (Join-Path $SourceDir 'MyBackup.exe'), (Join-Path $SourceDir 'restic.exe'), (Join-Path $SourceDir 'config.example.json') $InstallDir -Force

# Настройки содержат пароли: доступ только у SYSTEM и администраторов.
icacls $dataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null

$config = Join-Path $dataDir 'config.json'
if (-not (Test-Path $config)) {
    Copy-Item (Join-Path $SourceDir 'config.example.json') $config
    Write-Host "Создан $config из примера — заполните настройки в веб-интерфейсе." -ForegroundColor Yellow
}

$exe = Join-Path $InstallDir 'MyBackup.exe'
if (-not $existing) {
    New-Service -Name $serviceName -BinaryPathName "`"$exe`"" -DisplayName 'MyBackup — резервное копирование 1С' `
        -Description 'Резервное копирование баз 1С, SQL и профилей пользователей через restic' -StartupType Automatic | Out-Null
    sc.exe config $serviceName start= delayed-auto | Out-Null
    # Перезапуск при сбое: через 1 мин, 1 мин, 5 мин.
    sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
    Write-Host "Служба $serviceName установлена (LocalSystem, отложенный автозапуск)."
} else {
    Write-Host "Файлы службы $serviceName обновлены."
}

if (-not (Test-Path (Join-Path $dataDir 'web-password'))) {
    Write-Host ""
    Write-Host "Задайте пароль веб-интерфейса (не короче 8 символов):" -ForegroundColor Yellow
    & $exe web-password
}

# Служба запускается и с незаполненными настройками: задачи с ошибками не выполняются,
# а всё остальное можно заполнить в веб-интерфейсе.
Start-Service $serviceName
Write-Host ""
Write-Host "Служба $serviceName запущена."
Write-Host "Откройте на этом сервере http://127.0.0.1:8480 — там хранилище, задачи, расписание, Telegram и журнал."
Write-Host "Проверка из командной строки: & '$exe' check"
