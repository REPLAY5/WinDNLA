# WinDNLA — Windows DLNA Media Server

Unpackaged WinUI 3 (.NET 10) DLNA/UPnP MediaServer для локальной сети.

## Возможности

- Имя сервера и иконка (видны DLNA-клиентам)
- Несколько папок с видео, дерево folder → subfolders
- Сканирование + превью через ffmpeg (кэш в `%LocalAppData%\WinDNLA\thumbs`)
- Авто-рескан каждые 30 секунд и ручной рескан
- Опциональное перекодирование «на лету» (по расширению / кодеку ≠ h264/h265)
- Статистика клиентов: IP, файл, скорость Мбит/с, транскод да/нет
- Tray: закрытие сворачивает в трей; меню Выход / Автозагрузка / Пересканировать
- Автозагрузка через Startup с `--quiet` (тихий старт в tray)
- MSI-установщик (WiX)

## Требования

- Windows 10 1809+ / Windows 11
- .NET 10 SDK (для сборки)
- **ffmpeg.exe** и **ffprobe.exe** — положить в `tools/ffmpeg/` перед запуском/сборкой MSI

### Куда класть ffmpeg

```
tools/ffmpeg/ffmpeg.exe
tools/ffmpeg/ffprobe.exe
```

При сборке они копируются в `ffmpeg\` рядом с `WinDNLA.exe`.  
Скачать: https://www.gyan.dev/ffmpeg/builds/ (release essentials) или BtbN builds.

## Сборка и запуск (dev)

```powershell
dotnet build src\WinDNLA.App\WinDNLA.App.csproj -c Debug -p:Platform=x64
dotnet run --project src\WinDNLA.App\WinDNLA.App.csproj -c Debug -p:Platform=x64 --no-build
```

Тихий старт:

```powershell
.\src\WinDNLA.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\WinDNLA.exe --quiet
```

При первом bind HTTP/SSDP Windows может запросить разрешение брандмауэра — разрешите для частных сетей. HTTP слушает через обычный TCP-сокет (без http.sys / URL ACL), поэтому запуск не требует прав администратора.

## Publish + MSI

```powershell
# 1. Положите ffmpeg в tools\ffmpeg\
# 2. Publish
dotnet publish src\WinDNLA.App\WinDNLA.App.csproj -c Release -r win-x64 --self-contained -p:Platform=x64 -o artifacts\publish

# 3. MSI (нужен WiX v5: dotnet tool install --global wix)
.\installer\build-msi.ps1
```

Артефакт: `artifacts\WinDNLA-<version>-x64.msi`

Установка: `Program Files\WinDNLA\`. Данные пользователя (`settings.json`, `library.db`, thumbs) остаются в `%LocalAppData%\WinDNLA` при uninstall.

## Тесты

```powershell
dotnet test tests\WinDNLA.Tests\WinDNLA.Tests.csproj
```

Покрывают: готовность файлов (lock / `.part`), скан библиотеки, пропуск занятых, Browse/стрим DLNA с транскодом и без, Range, SessionTracker.
