# Ручная проверка S2-REG-APPLY: применение .reg к смонтированному образу

Статус: **Пройдена** (2026-10-28, Hyper-V, Win11 26H2 Pro).
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

### 2б. HKCU\Software\Classes — через первый вход пользователя

В `install.wim` у профиля Default нет `UsrClass.dat`, поэтому такие изменения не пишутся в кусты образа. Они кладутся файлом в `ProgramData\ImageForge\FirstLogon`, а в SOFTWARE регистрируется Active Setup, который импортирует файл при первом входе каждого пользователя. Сам импорт при входе проверяется отдельно: [S2-FIRST-LOGON](S2-FIRST-LOGON.md).

```powershell
Test-Path C:\IF\scratch\mount\Users\Default\AppData\Local\Microsoft\Windows\UsrClass.dat
@'
Windows Registry Editor Version 5.00

[HKEY_CURRENT_USER\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32]
@=""

[HKEY_CURRENT_USER\Software\Classes\.imageforgetest]
@="ImageForgeTest.User"
'@ | Set-Content C:\IF\scratch\classes.reg -Encoding Unicode
& $X reg-apply C:\IF\scratch\mount C:\IF\scratch\classes.reg
& $X reg-verify C:\IF\scratch\mount C:\IF\scratch\classes.reg
Get-ChildItem C:\IF\scratch\mount\ProgramData\ImageForge\FirstLogon
```
Ожидается:
- `Test-Path` → `False`.
- Превью `reg-apply`: у ключей `HKCU\Software\Classes\…` строка «При первом входе каждого пользователя (Active Setup, …)», затем четыре записи в `Microsoft\Active Setup\Installed Components\{…}` (по умолчанию, `StubPath`, `Version`, `IsInstalled`). `RESULT: OK`.
- `reg-verify`: строка `file ProgramData\ImageForge\FirstLogon\user-classes-….reg: present OK`, четыре значения Active Setup `OK`, `RESULT: OK`.
- В папке один файл `user-classes-….reg`.

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
| 8d82564a | Hyper-V | 2 | FAIL: удаление значения — «Отказано в доступе» (ключ открывался только на чтение). Исправлено |
| 25bfcb4c | Hyper-V | 2 | FAIL на шаге 26: в образе нет `UsrClass.dat`, кусты до него уже записаны. Добавлена проверка файлов кустов до записи |
| 25bfcb4c | Hyper-V | 2, 3 | OK: 30/30, `reg-verify` OK по всем значениям, ControlSet001, повтор OK |
| eab3cd9f | Hyper-V | 2б, 4 | OK: `steps 0/8`, `HiveFileMissing … UsrClass.dat`, ничего не записано; unmount discard, `mounts` none, `host-check` OK, ключей в реестре VM нет |
