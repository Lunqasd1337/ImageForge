# Ручная проверка S2-REG-APPLY: применение .reg к смонтированному образу

Статус: **Ожидает ручной проверки**.
Проверяет, что изменения из `.reg` файла попадают в нужные офлайн-кусты образа (план, раздел 9): все типы значений, удаление значений и ключей, SOFTWARE, SYSTEM с CurrentControlSet через `Select\Current`, NTUSER.DAT и UsrClass.dat профиля Default, DEFAULT, HKCR. На хосте и в реестре сервисной VM ничего не меняется.
Работа идёт с **копией** `install.wim` в `C:\IF\scratch`, фикстура не монтируется.

## Сборка и требования

- `ImageForge.Experiments-<sha>` из CI этого PR, положить в `C:\IF`.
- Сервисная VM ([как подготовить в Hyper-V](HYPERV.md)), фикстура `C:\IF\fixture\media`, свободно не меньше 10 ГБ. `& $X mounts` → `none`.

PowerShell от администратора, по одной строке:
```powershell
$X = { & "C:\IF\ImageForge.Experiments.exe" @args | Out-Host }
Start-Transcript C:\IF\s2-reg-apply.txt
& $X version
New-Item -ItemType Directory C:\IF\scratch, C:\IF\scratch\mount -Force | Out-Null
Copy-Item C:\IF\fixture\media\sources\install.wim C:\IF\scratch\install.wim
Set-ItemProperty C:\IF\scratch\install.wim -Name IsReadOnly -Value $false
& $X mount C:\IF\scratch\install.wim 1 C:\IF\scratch\mount
```
Ожидается: `RESULT: OK` у монтирования.

## Шаги

### 1. Файл с изменениями

```powershell
@'
Windows Registry Editor Version 5.00

; Все типы значений в SOFTWARE
[HKEY_LOCAL_MACHINE\SOFTWARE\ImageForgeTest]
@="по умолчанию"
"Строка"="Привет; не комментарий \\ и \"кавычки\""
"Путь"=hex(2):25,00,53,00,79,00,73,00,74,00,65,00,6d,00,52,00,6f,00,6f,00,74,00,25,00,00,00
"Список"=hex(7):41,00,00,00,42,00,00,00,00,00
"Число"=dword:12345678
"Большое"=hex(b):88,77,66,55,44,33,22,11
"Двоичное"=hex:00,ff,10,ab,\
  01,02
"Пустое"=hex(0):
"Удалить"="будет удалено"
"Удалить"=-

[HKEY_LOCAL_MACHINE\SOFTWARE\ImageForgeTest\Ветка\Глубже]
"x"=dword:00000001

[-HKEY_LOCAL_MACHINE\SOFTWARE\ImageForgeTest\Ветка]

; SYSTEM через CurrentControlSet
[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\ImageForgeTest]
"Start"=dword:00000004

; Профиль Default, системный профиль и HKCR
[HKEY_CURRENT_USER\Control Panel\ImageForgeTest]
"Цвет"="синий"

[HKEY_USERS\.DEFAULT\Control Panel\ImageForgeTest]
"Цвет"="зелёный"

[HKEY_CLASSES_ROOT\.imageforgetest]
@="ImageForgeTest.Machine"
'@ | Set-Content C:\IF\scratch\apply.reg -Encoding Unicode
```

### 2. Применение и проверка

```powershell
& $X reg-apply C:\IF\scratch\mount C:\IF\scratch\apply.reg
& $X reg-verify C:\IF\scratch\mount C:\IF\scratch\apply.reg
```
Ожидается:
- В превью SYSTEM-ключ указан как `Services\ImageForgeTest в ControlSet, который указан в SYSTEM\Select\Current`, HKCR — как `SOFTWARE\Classes\.imageforgetest (HKCR по политике ImageForge …)`.
- `reg-apply`: `Execution: Completed`, `RESULT: OK`.
- `reg-verify`: у каждой строки `OK`. Ключ `ImageForgeTest\Ветка` — `absent`, значение `Удалить` — `absent`, у SYSTEM путь начинается с `ControlSet001` (или того номера, что указан в образе). `RESULT: OK`.

### 2б. Нет UsrClass.dat — отказ без записи

В `install.wim` фикстуры у профиля Default нет `UsrClass.dat` (так показал первый прогон S2-REG-APPLY; `Test-Path` ниже это подтверждает). По плану такой куст не создаётся и не подменяется другим, поэтому файл с `HKCU\Software\Classes` должен быть отклонён до любой записи.

```powershell
Test-Path C:\IF\scratch\mount\Users\Default\AppData\Local\Microsoft\Windows\UsrClass.dat
@'
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SOFTWARE\ImageForgeTest]
"НеДолжноПоявиться"="1"

[HKEY_CURRENT_USER\Software\Classes\.imageforgetest]
@="ImageForgeTest.User"
'@ | Set-Content C:\IF\scratch\classes.reg -Encoding Unicode
& $X reg-apply C:\IF\scratch\mount C:\IF\scratch\classes.reg
& $X reg-verify C:\IF\scratch\mount C:\IF\scratch\apply.reg
```
Ожидается: `Test-Path` → `False`. `reg-apply`: `Execution: Failed, steps 0/…`, `HiveFileMissing: the image has no Users\Default\AppData\Local\Microsoft\Windows\UsrClass.dat`, `RESULT: FAIL`, строки `RECOVERY REQUIRED` нет. Следующий `reg-verify` по `apply.reg` — снова `RESULT: OK` (значения `НеДолжноПоявиться` он не проверяет; оно не записалось, потому что SOFTWARE даже не загружался).

### 3. Повторное применение

```powershell
& $X reg-apply C:\IF\scratch\mount C:\IF\scratch\apply.reg
& $X reg-verify C:\IF\scratch\mount C:\IF\scratch\apply.reg
```
Ожидается: снова `RESULT: OK` у обеих команд. Повтор не ломает результат.

### 4. Уборка и чистота хоста

```powershell
& $X unmount C:\IF\scratch\mount discard
& $X mounts
& $X host-check
reg query "HKLM\SOFTWARE\ImageForgeTest"
reg query "HKCU\Control Panel\ImageForgeTest"
Remove-Item C:\IF\scratch -Recurse -Force
Stop-Transcript
```
Ожидается: `mounts` → `none`, `host-check` → `RESULT: OK`. Обе команды `reg query` выдают «Не удается найти указанный раздел или параметр»: в реестре сервисной VM ничего не появилось.

## Что прислать

`C:\IF\s2-reg-apply.txt`.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
| — | — | — | Ожидает ручной проверки |
