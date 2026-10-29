# Ручная проверка S2-APPX-DEPS: зависимости приложений из манифестов

Статус: **Ожидает проверки**.
Проверяет, что ImageForge читает манифесты provisioned-пакетов (`Program Files\WindowsApps`) и встроенных приложений (`Windows\SystemApps`) из смонтированного образа и по ним решает, что можно удалить (план, раздел 8). Приложение, которое нужно оставшемуся приложению или встроенному приложению Windows, удалить нельзя; если удаляются оба, зависимое удаляется первым. Неизвестно заранее, удастся ли прочитать манифесты при правах образа: шаг 1 это и показывает.

## Сборка и требования

- Из одной сборки CI этого PR: `ImageForge.App-<sha>` и `ImageForge.Experiments-<sha>`. `C:\IF\app` удалить и распаковать туда новый `ImageForge.App`; `ImageForge.Experiments.exe` положить в `C:\IF`.
- Сервисная VM на чекпоинте `fixture-ready` ([Hyper-V](HYPERV.md)), свободно не меньше 30 ГБ.

PowerShell от администратора, по одной строке:
```powershell
$X = { & "C:\IF\ImageForge.Experiments.exe" @args | Out-Host }
Start-Transcript C:\IF\s2-appx-deps.txt
& $X version
New-Item -ItemType Directory C:\IF\scratch, C:\IF\scratch\mount -Force | Out-Null
Copy-Item C:\IF\fixture\media\sources\install.wim C:\IF\scratch\install.wim
Set-ItemProperty C:\IF\scratch\install.wim -Name IsReadOnly -Value $false
& $X mount C:\IF\scratch\install.wim 1 C:\IF\scratch\mount
```

## Шаги

### 1. Зависимости в утилите

```powershell
& $X appx-list C:\IF\scratch\mount
```
Ожидается:
- Под каждым пакетом строка `needs: …` (или `needs: -`), для библиотек с `framework;`.
- В конце `Inbox apps need: …` и `Provisioned packages: 47, dependencies unknown: 0`.

Если есть строки `DEPENDENCIES UNKNOWN` или `Inbox apps: UNKNOWN`, это не провал проверки, а то, что надо узнать: пришлите вывод и для одного такого пакета ещё
```powershell
Get-ChildItem "C:\IF\scratch\mount\Program Files\WindowsApps" -Filter "MSTeams_*" | Select-Object Name, Attributes, LinkType
Get-ChildItem "C:\IF\scratch\mount\Program Files\WindowsApps\MSTeams_*" | Select-Object Name, Attributes, LinkType
```

### 2. Уборка

```powershell
& $X unmount C:\IF\scratch\mount discard
& $X mounts
```
Ожидается `RESULT: OK`, `mounts` → `none`.

### 3. Окно

Запустить `C:\IF\app\ImageForge.App.exe`, «Подключить движок». Источник `C:\IF\fixture\media`, рабочая папка `C:\IF\ws-appx`, результат `C:\IF\result-appx`. «Прочитать приложения».
Ожидается: в журнале `appx-manifests...`; у библиотек в строке деталей «библиотека»; у пакетов, которые нужны другим, оранжевая строка «Нужна приложениям: …». Флажки неактивны только у пакетов с «Зависимости не прочитаны» или «Нужна встроенным приложениям Windows».

### 4. Блокировка и порядок

Найти пакет с пометкой «Нужна приложениям: A» (если в шаге 1 у какого-то пакета в `needs:` есть другой provisioned-пакет).
- Отметить только его, «Подготовить». Ожидается «План не построен…», в превью «… нужна приложениям, которые остаются: A. Отметьте и их…».
- Отметить и A, «Подготовить». Ожидается план, в превью сначала «Удалить приложение A…», потом сам пакет.

Если ни один provisioned-пакет не нужен другому (все `needs:` указывают только на библиотеки, которых нет в списке), шаг 4 пропускается, напишите об этом.

### 5. Запуск (по желанию, 20–40 минут)

С планом из шага 4 «Запустить». Ожидается в журнале порядок строк `remove …` как в превью, затем «Готово». Затем:
```powershell
New-Item -ItemType Directory C:\IF\mount -Force | Out-Null
& $X mount C:\IF\result-appx\sources\install.wim 1 C:\IF\mount
& $X appx-list C:\IF\mount
& $X unmount C:\IF\mount discard
& $X mounts
& $X host-check
Stop-Transcript
```
Ожидается: удалённых пакетов нет в списке, `dependencies unknown: 0`, `mounts` → `none`, `host-check` → `RESULT: OK`.

## Что прислать

`C:\IF\s2-appx-deps.txt` целиком (главное — вывод шага 1), снимки окна для шагов 3 и 4.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
| c1dc75f8 | Hyper-V | 1 | Не прочитан ни один манифест (47 из 47, SystemApps тоже): файлы смонтированного WIM помечены атрибутом ReparsePoint (тег WIM, ещё не извлечённые данные), а проверка отказывала на любом reparse point. Исправлено: отказ только для символических ссылок и junction. |
