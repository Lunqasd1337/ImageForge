# Ручная проверка S2-APPX-UI: удаление приложений из окна

Статус: **Ожидает проверки**.
Проверяет раздел «Приложения» в окне (план, раздел 8): движок читает список предустановленных приложений с монтирования проверенной копии только для чтения, отмеченные приложения попадают в план по семейству, библиотеки удалить нельзя, Store и App Installer идут с предупреждением, а запуск удаляет ровно то, что было в превью, сверив перед этим список образа со списком превью. Результат сверяется `appx-list` по опубликованному образу.

## Сборка

- Из одной сборки CI этого PR: `ImageForge.App-<sha>` и `ImageForge.Experiments-<sha>`.
- `C:\IF\app` удалить и распаковать туда новый `ImageForge.App`; `ImageForge.Experiments.exe` положить в `C:\IF`.

## Требования

Сервисная VM с контрольной точки `fixture-ready` ([Hyper-V](HYPERV.md)), фикстура `C:\IF\fixture\media`, свободно не меньше 30 ГБ, вход под администратором с UAC.

PowerShell от администратора:
```powershell
$X = { & "C:\IF\ImageForge.Experiments.exe" @args | Out-Host }; $W = "C:\IF\ws-appx"; $R = "C:\IF\result-appx"
Start-Transcript C:\IF\s2-appx-ui.txt
& $X version
& $X mounts
```
Ожидается: `mounts` → `none`.

## Шаги

### 1. Подключение и папки

Запустить `C:\IF\app\ImageForge.App.exe`, «Подключить движок», в UAC — «Да». Источник `C:\IF\fixture\media`, рабочая папка `C:\IF\ws-appx`, папка результата `C:\IF\result-appx`.
Ожидается: в журнале строка `Движок …, протокол 4, …`.

### 2. Удаление до чтения списка невозможно

Раздел «Приложения» пуст, «Подготовить». Ожидается обычный план с маркером M1 (приложения не отмечены, значит их и нет в плане).

### 3. Чтение списка

«Прочитать приложения». Первое чтение копирует дистрибутив в рабочую папку (несколько минут), затем монтирует копию только для чтения.
Ожидается:
- В журнале по порядку: `source-check...`, `base-copy...`, `mount read-only...`, `appx-list...`, `unmount discard...`.
- Статус «Предустановленных приложений: N…».
- У каждой строки имя, версия, архитектура и семейство.
- `Microsoft.WindowsStore` и `Microsoft.DesktopAppInstaller` с оранжевой строкой «Внимание: …».
- `Microsoft.VCLibs…`, `Microsoft.UI.Xaml…` и другие библиотеки (если есть в образе) с пометкой «Библиотека для других приложений…», флажок неактивен.

В PowerShell, пока окно открыто:
```powershell
& $X mounts
Test-Path $W\inspect
```
Ожидается: `mounts` → `none`, `False`.

### 4. Превью

Отметить `Clipchamp.Clipchamp` и ещё одно приложение без пометок (например `Microsoft.BingWeather`), «Подготовить».
Ожидается:
- Статус «План готов, изменений реестра: 1. Удаляемых приложений: 2. …».
- В превью две строки «Удалить приложение … (полное имя пакета)» и строка маркера M1.

Затем отметить ещё `Microsoft.WindowsStore`, «Подготовить»: в превью под ним строка «Внимание: Без Microsoft Store…». Снять отметку со Store и снова «Подготовить».

### 5. Запуск

«Запустить», окно не закрывать (20–40 минут). Повторного копирования быть не должно (в журнале `base copy reused`).
Ожидается: в журнале шаг `appx...`, строки `remove Clipchamp.Clipchamp_…` и `remove …BingWeather…`, затем `registry...`; «Готово. Результат сохранён.», строка `Итог: None Published …`.

### 6. Сверка результата

```powershell
New-Item -ItemType Directory C:\IF\mount -Force | Out-Null
& $X mount $R\sources\install.wim 1 C:\IF\mount
& $X appx-list C:\IF\mount
& $X unmount C:\IF\mount discard
& $X mounts
& $X host-check
Stop-Transcript
```
Ожидается: в `appx-list` нет двух удалённых приложений, `Provisioned packages:` на 2 меньше, чем N из шага 3; Store и остальные на месте. `mounts` → `none`, `host-check` → `RESULT: OK`.

### 7. Журнал движка

В журнале движка (`engine-*.log` рядом с приложением) есть `inspect image`, `inspection revision …: N packages, problems: `, две строки `plan remove appx …` и `… 1 changes, 0 problems`.

## Что прислать

`C:\IF\s2-appx-ui.txt`, снимки окна для шагов 3 и 4, журнал движка.
