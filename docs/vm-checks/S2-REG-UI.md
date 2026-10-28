# Ручная проверка S2-REG-UI: .reg из окна приложения

Статус: **Ожидает ручной проверки**.
Проверяет раздел «Реестр» в окне: выбор файла, кодировку, ошибки файла с номерами строк, ошибки плана, превью из движка и полный запуск с изменениями из файла. Результат сверяется `reg-verify` по смонтированному опубликованному образу.

## Сборка

- Из одной сборки CI этого PR: `ImageForge.App-<sha>` и `ImageForge.Experiments-<sha>`.
- `C:\IF\app` удалить и распаковать туда новый `ImageForge.App`; `ImageForge.Experiments.exe` положить в `C:\IF`.

## Требования

Сервисная VM с контрольной точки `fixture-ready` ([Hyper-V](HYPERV.md)), фикстура `C:\IF\fixture\media`, свободно не меньше 30 ГБ, вход под администратором с UAC.

PowerShell от администратора:
```powershell
$X = { & "C:\IF\ImageForge.Experiments.exe" @args | Out-Host }; $M = "C:\IF\fixture\media"; $W = "C:\IF\ws"; $R = "C:\IF\result-reg"
Start-Transcript C:\IF\s2-reg-ui.txt
& $X version
& $X mounts
New-Item -ItemType Directory C:\IF\reg -Force | Out-Null
```

### Файлы для проверки

```powershell
@'
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SOFTWARE\ImageForgeUi]
"Строка"="из окна"
"Число"=dword:0000002a

[HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\ImageForgeUi]
"Start"=dword:00000004

[HKEY_CURRENT_USER\Control Panel\ImageForgeUi]
"Цвет"="синий"

[HKEY_CURRENT_USER\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32]
@=""
'@ | Set-Content C:\IF\reg\good.reg -Encoding Unicode
[IO.File]::WriteAllText("C:\IF\reg\utf8.reg", "Windows Registry Editor Version 5.00`r`n`r`n[HKEY_LOCAL_MACHINE\SOFTWARE\ImageForgeUi]`r`n`"Строка`"=`"без BOM`"`r`n", (New-Object Text.UTF8Encoding $false))
@'
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SOFTWARE\ImageForgeUi]
"Плохое"=dword:zz
'@ | Set-Content C:\IF\reg\syntax.reg -Encoding Unicode
@'
Windows Registry Editor Version 5.00

[HKEY_LOCAL_MACHINE\SAM\ImageForgeUi]
"x"=dword:00000001
'@ | Set-Content C:\IF\reg\forbidden.reg -Encoding Unicode
Get-ChildItem C:\IF\reg
```
Ожидается: четыре файла.

## Шаги

### 1. Подключение и папки

Запустить `C:\IF\app\ImageForge.App.exe`, «Подключить движок», в UAC — «Да». Источник `C:\IF\fixture\media`, рабочая папка `C:\IF\ws`, папка результата `C:\IF\result-reg`.
Ожидается: в журнале строка `Движок …, протокол 3, …`.

### 2. Без файла — маркер M1

Поле «Файл .reg» пустое, «Подготовить». Ожидается превью с `ImageForge\Validation`, `Milestone = REG_SZ: M1`, статус «План готов, изменений: 1…».

### 3. Ошибки файла

Для каждого файла: «Выбрать...» (окно выбора открывается, фильтр `.reg`), выбрать файл, «Подготовить».

| Файл | Кодировка | Ожидается |
|---|---|---|
| `utf8.reg` | Авто | Статус «Файл .reg не принят…», в превью «Кодировку нельзя определить: в файле нет BOM…», «Запустить» неактивна |
| `utf8.reg` | UTF-8 | «План готов, изменений: 2. Кодировка файла: UTF-8.», в превью `Строка = REG_SZ: без BOM` |
| `syntax.reg` | Авто | «Файл .reg не принят… Кодировка файла: UTF-16.», в превью `строка 4: Неверная запись значения (…)` |
| `forbidden.reg` | Авто | «План не построен, ничего не будет применено.», в превью `изменение 1: …SAM…` |

Также: при выбранной кодировке «ANSI» поле кодовой страницы активно, при других — нет. Кнопка «Убрать» очищает путь, «Запустить» становится неактивной.

### 4. Превью рабочего файла

Выбрать `good.reg`, кодировка «Авто», «Подготовить». Ожидается статус «План готов, изменений: … Кодировка файла: UTF-16.» и превью:
- `SOFTWARE\ImageForgeUi` — `Строка` и `Число = REG_DWORD: 0x0000002a (42)`;
- `Services\ImageForgeUi в ControlSet, который указан в SYSTEM\Select\Current`;
- `DefaultUser (Users\Default\NTUSER.DAT)` — `Цвет`;
- `HKCU\Software\Classes\CLSID\{86ca1aa0-…}\InprocServer32` с пометкой «При первом входе каждого пользователя (Active Setup…)»;
- четыре записи `Microsoft\Active Setup\Installed Components\{…}`.

### 5. Запуск

«Запустить», окно не закрывать (20–40 минут). Ожидается: «Готово. Результат сохранён.», строка `Итог: None Published …`.

### 6. Сверка результата

```powershell
New-Item -ItemType Directory C:\IF\mount -Force | Out-Null
& $X mount $R\sources\install.wim 1 C:\IF\mount
& $X reg-verify C:\IF\mount C:\IF\reg\good.reg
& $X unmount C:\IF\mount discard
& $X mounts
& $X host-check
reg query "HKLM\SOFTWARE\ImageForgeUi"
Stop-Transcript
```
Ожидается: `reg-verify` — все строки `OK`, включая `file ProgramData\ImageForge\FirstLogon\user-classes-….reg: present OK`, `RESULT: OK`; `mounts` → `none`; `host-check` → `RESULT: OK`; `reg query` — «Не удается найти указанный раздел»: реестр самой VM не тронут.

### 7. Журнал движка

В журнале движка (`engine-*.log` рядом с приложением, как в M1-RUN) есть строки `plan .reg good.reg (… bytes, Auto, code page 1251)` и `… changes, 0 problems`.

## Что прислать

`C:\IF\s2-reg-ui.txt`, снимки окна для шагов 3 и 4 (или текст превью), журнал движка.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
| — | — | — | Ожидает ручной проверки |
