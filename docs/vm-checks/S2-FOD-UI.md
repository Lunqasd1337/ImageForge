# Ручная проверка S2-FOD-UI: добавление дополнительных компонентов из окна

Статус: **Пройдена** (сборка a70a9b3b, Hyper-V, 29.10.2026; добавлялся только Speech fr-FR).
Проверяет добавление Capability в окне, в плане и в запуске (план, раздел 10). «Прочитать образ» теперь показывает и компоненты, которых нет в образе (`Not Present`), с русскими названиями. Отмеченные попадают в план, только если указана подходящая папка Features on Demand. Перед добавлением запуск проверяет папку ещё раз. Добавляет он каждый компонент отдельной командой DISM с `/Source` и `/LimitAccess`. Затем список читается снова, и в журнал пишется всё, что DISM добавил сам (у языковых компонентов — базовый пакет и другие).

## Сборка

- Из одной сборки CI этого PR: `ImageForge.App-<sha>` и `ImageForge.Experiments-<sha>`.
- `C:\IF\app` удалить и распаковать туда новый `ImageForge.App`; `ImageForge.Experiments.exe` положить в `C:\IF`.

## Требования

Сервисная VM с контрольной точки `fixture-ready` ([Hyper-V](HYPERV.md)), фикстура `C:\IF\fixture\media`, свободно не меньше 30 ГБ, вход под администратором с UAC. ISO «Languages and Optional Features» 26100.1 подключён (см. [S2-FOD-ADD](S2-FOD-ADD.md)); ниже он на `E:`.

PowerShell от администратора:
```powershell
$E = "C:\IF\ImageForge.Experiments.exe"; $X = { & $E @args | Out-Host }; $R = "C:\IF\result-fod"
Start-Transcript C:\IF\s2-fod-ui.txt
& $X version
& $X mounts
```
Ожидается: `mounts` → `none`.

## Шаги

### 1. Подключение и папки

Запустить `C:\IF\app\ImageForge.App.exe`, «Подключить движок», в UAC — «Да». Источник `C:\IF\fixture\media`, рабочая папка `C:\IF\ws-fod`, папка результата `C:\IF\result-fod`.
Ожидается: в журнале `Движок …, протокол 6, …`.

### 2. Чтение образа

«Прочитать образ».
Ожидается:
- в «Дополнительные компоненты» под «Добавить из Features on Demand» строка «Можно добавить: N, показано: N, отмечено: 0» (в S2-FEATURES-INVENTORY было 376);
- в «Список доступных для добавления» русские названия (перепишите, есть ли строки только с английским именем вида `Language.Speech`), во второй строке полное имя;
- фильтр `fr-FR` оставляет только французские компоненты, `OpenSSH` — `OpenSSH.Server…`.

Перепишите время чтения: от `capability-list...` до `unmount discard...` в журнале окна.

### 3. План без папки и с неподходящей папкой

Отметить `OpenSSH.Server~~~~0.0.1.0` (фильтр `OpenSSH`). Поле «Папка FoD» пустое, «Подготовить».
Ожидается: «План не построен…», в превью «Для добавления нужна папка Features on Demand…».

В «Папка FoD» ввести `C:\IF\fixture\media`, «Подготовить».
Ожидается: «Папка Features on Demand не подходит: The folder has no metadata subfolder…».

### 4. Превью

«Папка FoD» — кнопкой «Выбрать...» выбрать `E:\LanguagesAndOptionalFeatures` (или ввести путь). Фильтр `fr-FR`, отметить `Language.Speech~~~fr-FR~0.0.1.0`. «Подготовить».
Ожидается:
- статус «План готов… Добавляемых: 2. …»;
- в превью «Добавить дополнительный компонент … (OpenSSH.Server~~~~0.0.1.0)», такая же строка для `Language.Speech~~~fr-FR~0.0.1.0`, строка «Источник: E:\LanguagesAndOptionalFeatures» и предупреждение «DISM может сам добавить нужные им компоненты…».

Снимок окна с превью.

### 5. Запуск

«Запустить», окно не закрывать.
Ожидается в журнале:
- шаг `capabilities...`, две строки `add capability … from E:\LanguagesAndOptionalFeatures`, у каждой вызов Dism.exe с `/Add-Capability … /Source:E:\LanguagesAndOptionalFeatures /LimitAccess`;
- `capability OpenSSH.Server~~~~0.0.1.0: Not Present -> Installed`, `capability Language.Speech~~~fr-FR~0.0.1.0: Not Present -> Installed`;
- `also changed by DISM Language.Basic~~~fr-FR~0.0.1.0: Not Present -> Installed` и `… Language.TextToSpeech~~~fr-FR …`;
- «Готово. Результат сохранён.», `Итог: None Published …`.

### 6. Сверка результата

```powershell
New-Item -ItemType Directory C:\IF\mount -Force | Out-Null
& $X mount $R\sources\install.wim 1 C:\IF\mount
& $E capability-list C:\IF\mount | Select-String "OpenSSH|fr-FR"
& $X unmount C:\IF\mount discard
& $X mounts
& $X host-check
Stop-Transcript
```
Ожидается: `OpenSSH.Server`, `Language.Speech`, `Language.Basic`, `Language.TextToSpeech` (fr-FR) — `Installed`; `Language.Handwriting` и `Language.OCR` fr-FR — `NotPresent`. `mounts` → `none`, `host-check` → `RESULT: OK`.

### 7. Журнал движка

В `engine-*.log`: `inspection revision …: … installed and N available capabilities`, `plan add capability OpenSSH.Server~~~~0.0.1.0 from E:\LanguagesAndOptionalFeatures` и такая же строка для Speech.

## Что прислать

`C:\IF\s2-fod-ui.txt`, снимки окна для шагов 2 и 4, журнал окна после шага 5, строки журнала движка из шага 7.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
| a70a9b3b | Hyper-V | 1–6 | Пройдены. Протокол 6; «Можно добавить: 376», русские подписи, фильтр работает. Неподходящие папки (`C:\IF\fixture\media`, `Documents`) отклонены с причиной «no metadata subfolder»; с `E:\LanguagesAndOptionalFeatures` — превью с источником и предупреждением. Запуск добавил `Language.Speech~~~fr-FR` одной командой с `/Source:E:\… /LimitAccess`; `Language.Basic` и `Language.TextToSpeech` fr-FR показаны как `also changed by DISM`. В опубликованном образе все три `Installed`, Handwriting и OCR `NotPresent`, OpenSSH.Server не отмечался и остался `NotPresent`. `mounts` → `none`, `host-check` OK. Найдено: чтение дополнительных компонентов с подписями 376 недобавленных — 1 мин 46 с (было около 20 с); ускорить отдельно. |
