# Ручная проверка S2-DRIVERS-INVENTORY: разбор пакетов драйверов, список драйверов образа, добавление и удаление

Статус: **Ожидает проверки**.
Проверяет первую часть раздела 12 плана:
- разбор INF и проверку пакета до DISM: класс, поставщик, версия, каталог, раздел x64 и файлы рядом с INF;
- список драйверов образа (DismGetDrivers);
- добавление (DismAddDriver, ForceUnsigned выключен, пока его не выбрали явно);
- удаление по имени в образе `oemN.inf` (DismRemoveDriver) с отказом для BootCritical без явного согласия.

Работа идёт с **копией** `install.wim` в `C:\IF\scratch`. Хост и драйверы самой VM не трогаются.

## Сборка и требования

- `ImageForge.Experiments-<sha>` из CI этого PR, положить в `C:\IF`.
- Сервисная VM ([Hyper-V](HYPERV.md)) на чекпоинте `fixture-ready`, свободно не меньше 15 ГБ. `& $X mounts` → `none`.
- ISO драйверов virtio-win (подписанные драйверы Red Hat, удобный тестовый набор): `https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/stable-virtio/virtio-win.iso`. Подключить в VM как DVD. Ниже предполагается буква `E:`; если другая, поправьте `$D`.

PowerShell от администратора, по одной строке:
```powershell
$X = { & "C:\IF\ImageForge.Experiments.exe" @args | Out-Host }; $D = "E:"
Start-Transcript C:\IF\s2-drivers.txt
& $X version
Get-ChildItem $D\NetKVM\w11\amd64, $D\viostor\w11\amd64 | Select-Object Name, Length
New-Item -ItemType Directory C:\IF\scratch, C:\IF\scratch\mount -Force | Out-Null
Copy-Item C:\IF\fixture\media\sources\install.wim C:\IF\scratch\install.wim -Force
Set-ItemProperty C:\IF\scratch\install.wim -Name IsReadOnly -Value $false
& $X mount C:\IF\scratch\install.wim 1 C:\IF\scratch\mount
```
Ожидается: в обеих папках есть `.inf`, `.cat`, `.sys`; монтирование `RESULT: OK`.

## Шаги

### 1. Разбор пакетов без DISM

```powershell
& $X driver-scan $D\NetKVM\w11\amd64
& $X driver-scan $D\viostor\w11\amd64
& $X driver-scan $D\
```
Ожидается:
- для `netkvm.inf` класс `Net`, поставщик `Red Hat, Inc.`, дата и версия, каталог `netkvm.cat`, в архитектурах есть `amd64`, строк `PROBLEM` нет;
- для `viostor.inf` класс `SCSIAdapter`, каталог `viostor.cat`, строк `PROBLEM` нет;
- скан всего ISO проходит без `ERROR`. У папок `x86` и `ARM64` будут `PROBLEM: … нет раздела для x64`, это верно. Строки `SAME INF CONTENT`, если появятся, тоже верны. Перепишите итоговую строку `INF files: …` и любые `PROBLEM` у папок `amd64`: их быть не должно, и каждая такая строка — либо ошибка разбора, либо особенность пакета.

### 2. Список драйверов образа

```powershell
& $X driver-list C:\IF\scratch\mount third-party
& $X driver-list C:\IF\scratch\mount all
```
Ожидается: первая команда даёт `Drivers read … : 0` или несколько `oemN.inf` и `RESULT: OK`. Вторая даёт сотни inbox-драйверов, среди них строки `BOOT-CRITICAL`, и `RESULT: OK`. Перепишите обе итоговые строки `Third-party: …` и время чтения `Drivers read … s`.

### 3. Отказы до DISM

```powershell
New-Item -ItemType Directory C:\IF\scratch\nocat -Force | Out-Null
Copy-Item $D\NetKVM\w11\amd64\* C:\IF\scratch\nocat -Force
Remove-Item C:\IF\scratch\nocat\*.cat
& $X driver-add C:\IF\scratch\mount C:\IF\scratch\nocat\netkvm.inf signed-only
(Get-Content C:\IF\scratch\nocat\netkvm.inf) | Where-Object { $_ -notmatch '^\s*CatalogFile' } | Set-Content C:\IF\scratch\nocat\netkvm.inf
& $X driver-add C:\IF\scratch\mount C:\IF\scratch\nocat\netkvm.inf signed-only
& $X driver-remove C:\IF\scratch\mount oem999.inf normal
```
Ожидается:
- первая `driver-add`: `PROBLEM: Нет файла каталога netkvm.cat`, `REFUSED: the package has problems`;
- вторая: `catalog (none: UNSIGNED)`, `REFUSED: the package has no catalog`;
- `driver-remove`: `REFUSED: 'oem999.inf' is not a third-party driver`.

Все три дают `RESULT: FAIL`, строки `DismAddDriver finished` нет.

### 4. Добавление подписанных драйверов

```powershell
& $X driver-add C:\IF\scratch\mount $D\NetKVM\w11\amd64\netkvm.inf signed-only
& $X driver-add C:\IF\scratch\mount $D\viostor\w11\amd64\viostor.inf signed-only
& $X driver-list C:\IF\scratch\mount third-party
```
Ожидается: в каждой `driver-add` есть `DismAddDriver finished in … s`, `ADDED:` и строка `oemN.inf | netkvm.inf | Net | Red Hat, Inc. | …` (для второй `viostor.inf | SCSIAdapter`, возможно с `BOOT-CRITICAL`), обе `RESULT: OK`. В списке оба пакета. Перепишите имена `oemN.inf` и есть ли `BOOT-CRITICAL` у viostor. Если DISM вернёт ошибку, пришлите её целиком.

### 5. Неподписанный пакет только по явному выбору

```powershell
& $X driver-add C:\IF\scratch\mount C:\IF\scratch\nocat\netkvm.inf force-unsigned
```
Ожидается: `WARNING: ForceUnsigned is on`, затем либо `ADDED:` с `Unsigned`, либо `No new published name appeared` (DISM может считать пакет тем же, что из шага 4). Перепишите, что получилось: это важно для правила о дубликатах.

### 6. Удаление

Подставьте имена из шага 4:
```powershell
& $X driver-remove C:\IF\scratch\mount <oem-netkvm>.inf normal
& $X driver-remove C:\IF\scratch\mount <oem-viostor>.inf normal
& $X driver-remove C:\IF\scratch\mount <oem-viostor>.inf allow-boot-critical
& $X driver-list C:\IF\scratch\mount third-party
```
Ожидается: netkvm `removed`, `RESULT: OK`. Для viostor: если он `BOOT-CRITICAL`, то первая команда даёт `REFUSED: the driver is boot-critical`, а вторая выводит `WARNING` и `removed`. Если не `BOOT-CRITICAL`, первая сразу `removed`, а вторая `REFUSED: … not a third-party driver`. Итоговый список как в шаге 2, плюс пакет из шага 5, если он добавился.

### 7. Уборка

```powershell
& $X unmount C:\IF\scratch\mount discard
& $X mounts
& $X host-check
Stop-Transcript
```
Ожидается: размонтирование `RESULT: OK`, `mounts` → `none`, `host-check` → `RESULT: OK`.

## Что прислать

`C:\IF\s2-drivers.txt` целиком и версию ISO virtio-win (файл `E:\virtio-win-guest-tools.exe` → Свойства → Подробно, или имя ISO).

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
