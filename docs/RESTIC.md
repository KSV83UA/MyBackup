# restic: краткая инструкция

Все копии MyBackup — обычное хранилище restic. Проще всего восстанавливать из веб-интерфейса
(«Снимки» → «Файлы» → «Скачать»); ниже — то же из командной строки.

## Как запускать

**На сервере с MyBackup** — PowerShell от имени администратора. Хранилище и пароль берутся из настроек:

```powershell
function restic { & "C:\Program Files\MyBackup\MyBackup.exe" restic @args }
```

Эта строка действует до закрытия окна; дальше все команды пишутся как `restic …`.

**На любом другом компьютере** — нужен только `restic.exe` (с github.com/restic/restic/releases) и пароль:

```powershell
$env:RESTIC_REPOSITORY = 'D:\backup'   # или rest:https://…, \\nas\backup
function restic { & "C:\путь\к\restic.exe" @args }
```

Пароль restic спросит сам.

## Хранилище

```powershell
restic stats    # сколько снимков, файлов, общий объём
restic check    # проверка целостности хранилища
```

## Снимки

```powershell
restic snapshots                              # все
restic snapshots --tag "name:Бухгалтерия"     # одного источника
```

В списке: ID (8 символов), время, сервер, тег `name:<источник>`, путь. Вместо ID можно писать `latest` — последний снимок
(вместе с `--tag`, чтобы взять последний снимок нужного источника).

## Найти файл

Пути внутри снимка пишутся так: `C:\Users\ivanov\Desktop` → `/C/Users/ivanov/Desktop`.

```powershell
restic find "Отчёт*.xlsx"                                                   # в каких снимках есть такой файл
restic ls latest --tag "name:Профили пользователей" /C/Users/ivanov/Desktop  # что лежит в папке
```

## Восстановить один файл

```powershell
restic dump 3f2a9c1e "/C/Users/ivanov/Desktop/Отчёт.xlsx" --target "D:\Restore\Отчёт.xlsx"
```

Файл появится ровно там, где указано в `--target`.

## Восстановить папку

```powershell
restic restore "3f2a9c1e:/C/Users/ivanov/Desktop" --target "D:\Restore\Desktop"
```

В `D:\Restore\Desktop` окажется содержимое папки. Так же восстанавливается файловая база 1С:
`restic restore "3f2a9c1e:/D/1C/Buh" --target "D:\Restore\Buh"`.

## Важно

- Восстанавливайте в отдельную папку (`D:\Restore`), а не поверх рабочих файлов; нужное потом скопируйте.
- Для одного файла используйте `dump`, а не `restore --include`: `restore` воссоздаёт всю цепочку папок `C\Users\…`
  с правами исходных папок — выводит «Fatal: There were 1 errors» (хотя файл восстановлен), а такие папки трудно удалить.
- Не запускайте `forget` и `prune` вручную — они удаляют копии.
- Без пароля хранилища восстановить ничего нельзя.
