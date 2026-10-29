# Ручная проверка S2-FEATURES-DEPS: родители компонентов и NetFx3 из sources\sxs

Статус: **Пройдена** (сборки 0d51178e и 4d01f42b, Hyper-V, 29.10.2026).
Проверяет вторую часть Features (план, раздел 10). ImageForge через DISM API (`DismGetFeatureParent`) читает родителей каждого компонента. При включении он добавляет выключенных родителей, при выключении — включённые зависимые компоненты. Список добавленного показывается до изменения, `/All` не используется. NetFx3 включается только из проверенной локальной папки (`sources\sxs` дистрибутива), без Windows Update. Неизвестно заранее, отдаёт ли DISM API родителей в смонтированном офлайн-образе: шаг 1 это и показывает.
Работа идёт с **копией** `install.wim` в `C:\IF\scratch`.

## Сборка и требования

- `ImageForge.Experiments-<sha>` из CI этого PR, положить в `C:\IF`.
- Сервисная VM ([Hyper-V](HYPERV.md)) на чекпоинте `fixture-ready`, свободно не меньше 10 ГБ. `& $X mounts` → `none`.
- В `C:\IF\fixture\media\sources\sxs` есть `microsoft-windows-netfx3-ondemand-package~31bf3856ad364e35~amd64~~.cab` (так в дистрибутиве Windows 11).

PowerShell от администратора, по одной строке:
```powershell
$X = { & "C:\IF\ImageForge.Experiments.exe" @args | Out-Host }
Start-Transcript C:\IF\s2-features-deps.txt
& $X version
New-Item -ItemType Directory C:\IF\scratch, C:\IF\scratch\mount -Force | Out-Null
Copy-Item C:\IF\fixture\media\sources\install.wim C:\IF\scratch\install.wim -Force
Set-ItemProperty C:\IF\scratch\install.wim -Name IsReadOnly -Value $false
& $X mount C:\IF\scratch\install.wim 1 C:\IF\scratch\mount
Get-ChildItem C:\IF\fixture\media\sources\sxs | Select-Object Name
```
Ожидается: `RESULT: OK` у монтирования, в списке `sxs` есть пакет NetFx3 для amd64.

## Шаги

### 1. Родители, ничего не меняется

```powershell
& $X feature-parents C:\IF\scratch\mount IIS-WebSockets
& $X feature-parents C:\IF\scratch\mount Printing-Foundation-Features
```
Ожидается:
- Обе команды: `Parents read: 136 of 136`, ни одной строки `PROBLEM:`, `RESULT: OK`. Время чтения в той же строке — перепишите его.
- Первая: `Direct parents of 'IIS-WebSockets': IIS-ApplicationDevelopment` (или близкое), строка `Enable: ...` перечисляет цепочку до `IIS-WebServerRole`, родители идут раньше `IIS-WebSockets`. `Disable: nothing to change`.
- Вторая: `Enable: nothing to change`. В `Disable along with it:` есть `Printing-Foundation-InternetPrinting-Client`.

### 2. Включение вместе с родителями

```powershell
& $X feature-enable C:\IF\scratch\mount IIS-WebSockets none
```
Ожидается: строка `Enable along with it:` до вызова DISM; в строке Dism.exe несколько `/FeatureName:` в том же порядке, есть `/LimitAccess`, нет `/All`; для каждого компонента `Disabled -> Enabled` (или `Enable Pending`) без `UNEXPECTED`; `RESULT: OK`. Ниже — строки `ALSO CHANGED by DISM:` с компонентами IIS, которые DISM включает по умолчанию сам (`IIS-StaticContent`, `IIS-DefaultDocument` и другие): так и должно быть, их список нужен.

### 3. Выключение вместе с зависимыми

```powershell
& $X feature-disable C:\IF\scratch\mount IIS-WebServerRole
```
Ожидается: строк `PROBLEM:` нет; в `Disable along with it:` есть все компоненты, включённые в шаге 2, и те, что DISM включил сам (кроме самого `IIS-WebServerRole`), `IIS-WebSockets` идёт раньше `IIS-WebServerRole`; для каждого `-> Disabled` (или `Disable Pending`) без `UNEXPECTED`; `RESULT: OK`. Строки `ALSO CHANGED by DISM:` здесь тоже перепишите, если будут.

### 4. NetFx3: отказы без вызова DISM на изменение

```powershell
& $X feature-enable C:\IF\scratch\mount NetFx3 none
& $X feature-enable C:\IF\scratch\mount NetFx3 C:\IF\scratch
```
Ожидается: `Needs a source: NetFx3`, затем `REFUSED: a feature without payload needs a source folder` у первой и `REFUSED: ... has no microsoft-windows-netfx3-ondemand-package...` у второй; обе `RESULT: FAIL`, строки `/Enable-Feature` нет.

### 5. NetFx3 из sources\sxs

```powershell
& $X feature-enable C:\IF\scratch\mount NetFx3 C:\IF\fixture\media\sources\sxs
```
Ожидается: в строке Dism.exe есть `/Source:C:\IF\fixture\media\sources\sxs` и `/LimitAccess`; `Feature 'NetFx3': Disabled with Payload Removed -> Enabled` (или `Enable Pending`); `RESULT: OK`.

### 6. Уборка

```powershell
& $X unmount C:\IF\scratch\mount discard
& $X mounts
& $X host-check
Stop-Transcript
```
Ожидается: размонтирование `RESULT: OK`, `mounts` → `none`, `host-check` → `RESULT: OK`.

## Что прислать

`C:\IF\s2-features-deps.txt` целиком.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
| 0d51178e | Hyper-V | 1, 2, 4, 5 | Родители читаются: 136 из 136 за 8 с, цепочка IIS и зависимые печати верны; включение IIS-WebSockets с родителями — `IIS-WebSockets` остался `Enable Pending`; NetFx3: отказ без источника и с чужой папкой, из `sources\sxs` — `Enable Pending`. Найдено: DISM при включении `IIS-WebServerRole` сам включил ещё 9 вложенных компонентов (`IIS-HttpErrors`, `IIS-StaticContent`, `IIS-DefaultDocument`, `IIS-DirectoryBrowsing`, `IIS-RequestFiltering`, `IIS-HttpLogging`, `IIS-HttpCompressionStatic`, `IIS-ManagementConsole` и др.) в `Enable Pending`, и шаг 3 отказал на них, потому что `Pending` блокировал изменение. Исправлено: `Enable Pending` считается включённым, `Disable Pending` — выключенным; после изменения сравнивается весь список, и всё, что DISM изменил сверх запрошенного, печатается (`ALSO CHANGED by DISM`). |
| 4d01f42b | Hyper-V | 2, 3, 6 | Пройдены: включение IIS-WebSockets с цепочкой, DISM сам включил ещё 13 компонентов IIS — все показаны строками `ALSO CHANGED by DISM`; повторное включение — `nothing to change`; выключение IIS-WebServerRole взяло все 17 включённых зависимых (в том числе добавленные DISM и `Enable Pending`), зависимые раньше родителей, итог `Disabled` / `Disable Pending`, `RESULT: OK`. Проверка пройдена целиком. |
