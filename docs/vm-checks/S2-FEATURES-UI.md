# Ручная проверка S2-FEATURES-UI: компоненты Windows и дополнительные компоненты из окна

Статус: **Пройдена** (сборка cce1f5a4, Hyper-V, 29.10.2026).
Проверяет третью часть Features (план, раздел 10). «Прочитать образ» теперь читает не только приложения, но и компоненты Windows с родителями и установленные дополнительные компоненты (Capabilities). В окне отметка у компонента означает «включён после запуска». План добавляет нужных родителей при включении и зависимые компоненты при выключении и показывает их в превью. NetFx3 включается только из `sources\sxs` дистрибутива. Запуск сверяет списки образа с теми, по которым строилось превью, применяет изменения, читает список снова и пишет в журнал всё, что DISM изменил сверх запрошенного. Результат сверяется `feature-list` и `capability-list` по опубликованному образу.

## Сборка

- Из одной сборки CI этого PR: `ImageForge.App-<sha>` и `ImageForge.Experiments-<sha>`.
- `C:\IF\app` удалить и распаковать туда новый `ImageForge.App`; `ImageForge.Experiments.exe` положить в `C:\IF`.

## Требования

Сервисная VM с контрольной точки `fixture-ready` ([Hyper-V](HYPERV.md)), фикстура `C:\IF\fixture\media`, свободно не меньше 30 ГБ, вход под администратором с UAC.

PowerShell от администратора, по одной строке:
```powershell
$E = "C:\IF\ImageForge.Experiments.exe"; $X = { & $E @args | Out-Host }; $W = "C:\IF\ws-feat"; $R = "C:\IF\result-feat"
Start-Transcript C:\IF\s2-features-ui.txt
& $X version
& $X mounts
```
Ожидается: `mounts` → `none`.

## Шаги

### 1. Подключение и папки

Запустить `C:\IF\app\ImageForge.App.exe`, «Подключить движок», в UAC — «Да». Источник `C:\IF\fixture\media`, рабочая папка `C:\IF\ws-feat`, папка результата `C:\IF\result-feat`.
Ожидается: в журнале строка `Движок …, протокол 5, …`.

### 2. Чтение образа

«Прочитать образ». Первое чтение копирует дистрибутив в рабочую папку (несколько минут).
Ожидается:
- В журнале по порядку: `source-check...`, `base-copy...`, `mount read-only...`, `appx-list...`, `appx-manifests...`, `feature-list...`, `feature-parents...`, `capability-list...`, `unmount discard...`.
- Статус «Образ прочитан…».
- Под «Компоненты Windows»: «Компонентов: 136, включено: N…». В «Список компонентов» русские названия, во второй строке имя и состояние. У включённых стоит отметка.
- У `NetFx3` во второй строке «файлы будут взяты из sources\sxs дистрибутива», флажок активен.
- Под «Дополнительные компоненты»: «Установленных дополнительных компонентов: 48…» (сколько было в S2-FEATURES-INVENTORY), в списке есть `MathRecognizer~~~~0.0.1.0`.

### 3. Превью

В «Список компонентов» (имя ищите во второй строке):
- поставить отметку у `IIS-WebSockets`;
- поставить отметку у `NetFx3`;
- снять отметку у `Printing-Foundation-Features`.

В «Дополнительные компоненты» отметить `MathRecognizer~~~~0.0.1.0`. «Подготовить».

Ожидается:
- Статус «План готов… Удаляемых дополнительных компонентов: 1. Включаемых компонентов: …, выключаемых: 2. …».
- В превью:
  - «Удалить дополнительный компонент … (MathRecognizer~~~~0.0.1.0)»;
  - «Выключить компонент …» для `Printing-Foundation-InternetPrinting-Client` с пометкой «(добавлен планом: зависит от выключаемого)» и для `Printing-Foundation-Features`, зависимый идёт первым;
  - «Включить компонент …» для цепочки от `IIS-WebServerRole` до `IIS-WebSockets`, у всех кроме `IIS-WebSockets` пометка «(добавлен планом: нужен включаемому)», и для `NetFx3`;
  - строки «Файлы .NET Framework 3.5 будут взяты из sources\sxs дистрибутива.» и «DISM может сам включить вложенные компоненты…»;
  - строка маркера M1 не нужна: её нет, если есть другие изменения.

Снимок окна с превью.

### 4. Запуск

«Запустить», окно не закрывать. Повторного копирования быть не должно (`base copy reused`).
Ожидается в журнале:
- шаг `capabilities...`, строка `remove capability MathRecognizer~~~~0.0.1.0`;
- шаг `features...`, строки `disable …` и `enable …` в порядке превью;
- строки `feature <имя>: Enabled -> Disabled` (или `Disable Pending`) и `feature <имя>: Disabled -> Enabled` (или `Enable Pending`) для каждого компонента из превью;
- строки `also changed by DISM IIS-…: Disabled -> Enable Pending` для вложенных компонентов IIS, которые DISM включил сам (в прошлый раз их было 13);
- затем `registry...`, «Готово. Результат сохранён.», `Итог: None Published …`.

### 5. Сверка результата

```powershell
New-Item -ItemType Directory C:\IF\mount -Force | Out-Null
& $X mount $R\sources\install.wim 1 C:\IF\mount
& $E feature-list C:\IF\mount | Select-String "IIS-WebServerRole |IIS-WebSockets |NetFx3 |Printing-Foundation"
& $E capability-list C:\IF\mount | Select-String "MathRecognizer"
& $X unmount C:\IF\mount discard
& $X mounts
& $X host-check
Stop-Transcript
```
Ожидается: `IIS-WebServerRole`, `IIS-WebSockets`, `NetFx3` — `Enabled` или `EnablePending`; оба `Printing-Foundation-…` — `Disabled` или `DisablePending`; `MathRecognizer` — `NotPresent`. `mounts` → `none`, `host-check` → `RESULT: OK`.

### 6. Журнал движка

В журнале движка (`engine-*.log` рядом с приложением) есть `inspection revision …: 47 packages, 136 features, 48 installed capabilities, problems: `, строки `plan remove capability …`, `plan disable feature Printing-Foundation-Features`, `plan enable feature IIS-WebSockets`, `plan enable feature NetFx3`.

## Что прислать

`C:\IF\s2-features-ui.txt`, снимки окна для шагов 2 и 3, журнал движка, текст журнала окна после шага 4.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
| cce1f5a4 | Hyper-V | 1–5 | Пройдены. Превью: удаление MathRecognizer; выключение Printing-Foundation-InternetPrinting-Client (добавлен планом, идёт первым) и Printing-Foundation-Features; включение NetFx3 и цепочки IIS-WebServerRole → IIS-WebServer → IIS-ApplicationDevelopment (добавлены планом) → IIS-WebSockets; строки про sources\sxs и вложенные компоненты. Запуск: capabilities, затем features; DISM вызван одной командой с `/Source:…\base\sources\sxs /LimitAccess`; DISM сам включил ещё 13 компонентов IIS, все записаны строками `also changed by DISM`. Сверка опубликованного образа: IIS-WebServerRole `Enabled`, IIS-WebSockets и NetFx3 `Enable Pending`, оба Printing-Foundation `Disabled`, MathRecognizer `Not Present`; `mounts` → `none`, `host-check` → `RESULT: OK`. Журнал движка (шаг 6) не присылали. |
