# Ручная проверка S2-FIRST-LOGON: HKCU\Software\Classes при первом входе

Статус: **Пройдена** (2026-10-28, Hyper-V, Win11 26H2 Pro).
Проверяет, что Active Setup импортирует файл ImageForge при первом входе нового пользователя и изменения попадают в его `HKCU\Software\Classes` (UsrClass.dat). Образ не нужен: утилита выгружает ровно те файл и регистрацию, что запуск кладёт в образ, и они ставятся в саму сервисную VM. **Реестр VM меняется**, поэтому начинать с контрольной точки `fixture-ready` и откатиться в конце ([Hyper-V](HYPERV.md)).

## Сборка

`ImageForge.Experiments-<sha>` из CI этого PR, в `C:\IF`.

## Шаги (PowerShell от администратора)

### 1. Выгрузить и установить

```powershell
$X = { & "C:\IF\ImageForge.Experiments.exe" @args | Out-Host }
Start-Transcript C:\IF\s2-first-logon.txt
& $X version
New-Item -ItemType Directory C:\IF\scratch -Force | Out-Null
@'
Windows Registry Editor Version 5.00

[HKEY_CURRENT_USER\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32]
@=""

[HKEY_CURRENT_USER\Software\Classes\.imageforgetest]
@="ImageForgeTest.User"
"Число"=dword:00000007
'@ | Set-Content C:\IF\scratch\classes.reg -Encoding Unicode
& $X first-logon-export C:\IF\scratch\classes.reg C:\IF\scratch\fl
New-Item -ItemType Directory C:\ProgramData\ImageForge\FirstLogon -Force | Out-Null
Copy-Item C:\IF\scratch\fl\user-classes-*.reg C:\ProgramData\ImageForge\FirstLogon\
reg import C:\IF\scratch\fl\register.reg
net user iftest "IfTest-2026!" /add
Stop-Transcript
```
Ожидается: `first-logon-export` → `RESULT: OK` и строка `Active Setup: HKLM\SOFTWARE\Microsoft\Active Setup\Installed Components\{…}`; `reg import` — «Операция успешно завершена»; пользователь создан.

### 2. Первый вход нового пользователя

Выйти из системы (не перезагрузка) и войти как `iftest` с паролем `IfTest-2026!`. В PowerShell **от имени iftest, без администратора**:
```powershell
Start-Transcript C:\Users\Public\s2-first-logon-user.txt
reg query "HKCU\Software\Classes\.imageforgetest"
reg query "HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32"
reg query "HKCU\Software\Microsoft\Active Setup\Installed Components\{GUID}"
Stop-Transcript
```
`{GUID}` — из строки `Active Setup:` в выводе `first-logon-export` (в HKCU под этим GUID хранится только `Version`). Затем правый клик по рабочему столу.

Ожидается:
- `.imageforgetest`: `(по умолчанию) REG_SZ ImageForgeTest.User` и `Число REG_DWORD 0x7`.
- `InprocServer32`: `(по умолчанию) REG_SZ` с пустым значением.
- В `HKCU\…\Active Setup\Installed Components\{…}` есть `Version` `1,0,0,0`: Windows отметила, что импорт для этого пользователя выполнен.
- На рабочем столе сразу старое контекстное меню (без «Показать дополнительные параметры»). Если меню новое — напиши, это тоже результат: значит, Active Setup отработал уже после запуска проводника.
- Было ли окно консоли при входе — напиши, видел ли.

### 3. Повторный вход

Выйти и снова войти как `iftest`. Ничего не должно выполняться повторно. Можно проверить, что значение не возвращается после удаления:
```powershell
reg delete "HKCU\Software\Classes\.imageforgetest" /f
```
выйти, войти, `reg query "HKCU\Software\Classes\.imageforgetest"` → «Не удается найти указанный раздел».

### 4. Откат

Выключить VM и откатиться к `fixture-ready`.

## Что прислать

`C:\IF\s2-first-logon.txt`, `C:\Users\Public\s2-first-logon-user.txt` и ответ про меню и окно консоли.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
| f3b4bf2e | Hyper-V | 1–3 | OK: под новым пользователем `.imageforgetest` (`@`, `Число`=7) и `InprocServer32` есть, `Version 1,0,0,0` под GUID компонента, старое контекстное меню сразу после входа, после удаления ключа и повторного входа импорт не повторился. При входе на мгновение мелькает окно консоли `reg.exe` — оставлено как есть |
