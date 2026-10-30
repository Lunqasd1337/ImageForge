# Ручная проверка S2-EDITIONS-UI: выбор редакции install.wim

Статус: **Ожидает проверки**.
Проверяет выбор редакции (план: меняется одна выбранная редакция, остальные сохраняются):
- окно показывает редакции `sources\install.wim` и по умолчанию выбирает Pro;
- смена редакции сбрасывает прочитанный образ и план, чтение и запуск идут по выбранной редакции;
- в результате все 5 редакций в прежнем порядке, маркер M1 есть только в выбранной.

До этой правки запуск менял образ 1 (Education), а в результат попадал только он.

## Сборка

- Из одной сборки CI этого PR: `ImageForge.App-<sha>` и `ImageForge.Experiments-<sha>`.
- `C:\IF\app` удалить и распаковать туда новый `ImageForge.App`; `ImageForge.Experiments.exe` положить в `C:\IF`.

## Требования

Сервисная VM, фикстура `C:\IF\fixture\media` (Win11 26H2, 5 редакций), свободно не меньше 40 ГБ.

PowerShell от администратора:
```powershell
$E = "C:\IF\ImageForge.Experiments.exe"; $X = { & $E @args | Out-Host }
Start-Transcript C:\IF\s2-editions-ui.txt
& $X version
& $X mounts
Get-PSDrive C | Select-Object @{n='FreeGB';e={[math]::Round($_.Free/1GB,1)}}
```
Ожидается: `mounts` → `none`.

## Шаги

### 1. Редакции и выбор по умолчанию

Запустить `C:\IF\app\ImageForge.App.exe`, «Подключить движок», в UAC — «Да». Источник `C:\IF\fixture\media`, рабочая папка `C:\IF\ws-ed`, папка результата `C:\IF\result-ed`.
До чтения список «Редакция в sources\install.wim» пуст, под ним «Редакции появятся после «Прочитать образ»…».

«Прочитать образ». Ожидается:
- в журнале `протокол 10`;
- в списке 5 строк: `1. Windows 11 Education`, `2. Windows 11 Enterprise`, `3. Windows 11 Pro`, `4. Windows 11 Pro Education`, `5. Windows 11 Pro for Workstations`; выбрана `3. Windows 11 Pro`;
- под списком «Меняется «Windows 11 Pro» (образ 3 из 5); остальные редакции попадут в результат без изменений.»;
- приложения, компоненты и драйверы прочитаны. Перепишите число приложений.

### 2. Смена редакции

Выбрать `1. Windows 11 Education`. Ожидается: под списком «Редакция изменилась; нажмите «Прочитать образ».», списки приложений и драйверов очищены, «Запустить» неактивна.

«Прочитать образ». Ожидается «Меняется «Windows 11 Education» (образ 1 из 5)…». Перепишите число приложений (может отличаться от Pro).

Вернуть `3. Windows 11 Pro`, «Прочитать образ». Ожидается снова «образ 3 из 5».

### 3. Запуск

Никаких отметок, «Подготовить» (план — маркер M1), «Запустить», окно не закрывать.
Ожидается в журнале после `unmount commit`:
```
export image 1 of 5
export image 2 of 5
export image 3 of 5 (serviced)
export image 4 of 5
export image 5 of 5
```
и «Готово. Результат сохранён.». Перепишите время шага `export` (по отметкам времени).

### 4. Сверка результата

```powershell
dism /English /Get-WimInfo /WimFile:C:\IF\result-ed\sources\install.wim
Get-Item C:\IF\fixture\media\sources\install.wim, C:\IF\result-ed\sources\install.wim |
  Select-Object FullName, @{n='GB';e={[math]::Round($_.Length/1GB,2)}}
New-Item -ItemType Directory C:\IF\mount-ed -Force | Out-Null
& $X mount C:\IF\result-ed\sources\install.wim 3 C:\IF\mount-ed
& $X read-marker C:\IF\mount-ed
& $X unmount C:\IF\mount-ed discard
& $X mount C:\IF\result-ed\sources\install.wim 1 C:\IF\mount-ed
& $X read-marker C:\IF\mount-ed
& $X unmount C:\IF\mount-ed discard
```
Ожидается:
- `Get-WimInfo`: 5 образов, имена и порядок как в шаге 1;
- размер результата близок к фикстуре (7,22 ГБ);
- образ 3: `Milestone = "M1"`, `RESULT: OK`;
- образ 1: `Milestone = <absent>`, `RESULT: FAIL` (так и должно быть: Education не меняли).

### 5. Хост и журнал движка

```powershell
& $X mounts
& $X host-check
Get-ChildItem C:\ProgramData\ImageForge\logs\engine-*.log | Sort-Object LastWriteTime | Select-Object -Last 1 |
  Get-Content -Encoding UTF8 | Select-String "open workspace|editions:|export image"
Stop-Transcript
```
Ожидается: `mounts` → `none`, `host-check` → `RESULT: OK`; в журнале `open workspace: … image default`, `editions: 5, servicing 3 Windows 11 Pro, others kept unchanged`, после смены — `image 1` и `servicing 1 Windows 11 Education`.

## Что прислать

- файл `C:\IF\s2-editions-ui.txt`;
- снимок окна после шага 1 (список редакций) и журнал окна после шага 3;
- числа приложений из шагов 1 и 2.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
