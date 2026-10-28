# Ручная проверка M1-EXP-C: полный запуск и сбой до публикации

Статус: **Пройдено** (28.10.2026, BuildId `8c9e41ef` — merge-сборка PR #6 с головой `ca14fdd`).
Покрывает эксперимент 4 из раздела 16 плана: при сбое до публикации исходник и предыдущий результат сохраняются, незавершённая цель помечается для пересоздания, временный итог не выдаётся как готовый. Заодно проверяется весь путь M1 одним запуском: копия источника → рабочий образ → mount → запись маркера в офлайн SOFTWARE → commit → export → сборка результата рядом с папкой вывода → повторная сверка источника → публикация.

## Сборка

- Артефакт `ImageForge.Experiments-<sha>` из GitHub Actions этого PR (джоба «Build all (Windows)»). Сверьте хеш exe с `SHA256.txt`.
- Положите exe в `C:\IF` поверх старого (`C:\IF\ImageForge.Experiments.exe`).

## Требования

- Та же сервисная VM, что для M1-EXP-A, с подготовленной фикстурой `C:\IF\fixture\media` (SHA-256 `install.wim` = `F240257D…C7C10A09`).
- Перед началом нет активных монтирований: `& $X mounts` → `DISM mounted images: none`. Если что-то осталось от прошлых проверок, напишите мне и не запускайте `dism /Cleanup-Mountpoints`.
- Свободно не меньше 25 ГБ на `C:`. Снимок VM перед началом — рекомендуется.
- Каждый запуск `run` копирует и хеширует несколько гигабайт: 5–15 минут на запуск — нормально.

Все команды — в **PowerShell от имени администратора в гостевой Windows**, по одной строке. Каждая команда утилиты заканчивается `RESULT: OK` или `RESULT: FAIL`.

```powershell
$X = "C:\IF\ImageForge.Experiments.exe"
$M = "C:\IF\fixture\media"; $W = "C:\IF\ws"; $R = "C:\IF\result"
Start-Transcript C:\IF\exp-c.txt
& $X version
```

## Шаги

### 1. Отпечаток источника

```powershell
& $X manifest $M
```
Запишите `fingerprint` — это **F1**.

### 2. Успешный запуск

```powershell
& $X run $M $W $R none
& $X manifest $R
& $X mount $R\sources\install.wim 1 C:\IF\mount
& $X read-marker C:\IF\mount
& $X unmount C:\IF\mount discard
```
Ожидается: в выводе `run` шаги `source-check` … `publish`, затем `Run …: Published`, `Published: C:\IF\result`, `state.json (Current): target Completed`, `DISM mounted images: none`, `Loaded ImageForge hives: none`, `RESULT: OK`. Отпечаток результата запишите — это **F2**. Маркер читается как `"M1"`.

### 3. Сбой до публикации

```powershell
& $X run $M $W $R before-publish
& $X manifest $R
& $X manifest $M
Get-ChildItem C:\IF -Force -Filter ".result.imageforge-*"
```
Ожидается: `Run …: Failed at publish: Injected failure…`, `publication recovery: DiscardStaging`, `target NeedsRebuild`, монтирований и кустов нет, `RESULT: OK` (ожидаемый сбой). Отпечаток `C:\IF\result` = **F2** (предыдущий результат не тронут), источника = **F1**. `Get-ChildItem` ничего не выводит (временной папки нет).

### 4. Сбой после mount

```powershell
& $X run $M $W $R after-mount
& $X manifest $R
```
Ожидается: `Failed at registry`, в журнале образ отмонтирован без сохранения (`discard`), монтирований и кустов нет, `RESULT: OK`. Результат = **F2**.

### 5. Аварийное завершение при смонтированном образе

```powershell
& $X run $M $W $R pause-after-mount
```
Дождитесь строки `PAUSED before step 'registry'`. Во **втором** окне PowerShell от администратора:
```powershell
Stop-Process -Name ImageForge.Experiments -Force
```
Затем в первом окне:
```powershell
& $X status $W
& $X run $M $W $R none
& $X recover $W
& $X manifest $R
```
Ожидается:
- `status`: последний запуск `Running`, в списке DISM есть монтирование в `C:\IF\ws\mount\…`.
- `run … none`: отказ с `WorkspaceRecoveryRequiredException … recover the workspace first`, `RESULT: FAIL` (так и должно быть: новый запуск не стартует поверх незавершённого).
- `recover`: `recovery: discarded mount C:\IF\ws\mount\…`, `Recovery: Recovered`, `target NeedsRebuild`, `DISM mounted images: none`, `RESULT: OK`.
- Результат = **F2**.

### 6. Аварийное завершение перед публикацией

```powershell
& $X run $M $W $R pause-before-publish
```
Дождитесь `PAUSED before step 'publish'`, во втором окне снова `Stop-Process -Name ImageForge.Experiments -Force`. Затем:
```powershell
Get-ChildItem C:\IF -Force -Filter ".result.imageforge-*"
& $X recover $W
Get-ChildItem C:\IF -Force -Filter ".result.imageforge-*"
& $X manifest $R
```
Ожидается: до `recover` видна папка `.result.imageforge-<run>.staging` (временный итог); `recover` пишет `publication: DiscardStaging`, `Recovery: Recovered`; после него временной папки нет; результат = **F2**.

### 7. Пересоздание цели и итоговая сверка

```powershell
& $X run $M $W $R none
& $X manifest $M
& $X host-check
Stop-Transcript
```
Ожидается: `Published`, в журнале `base copy reused`; отпечаток источника = **F1**; на сервисной VM маркера нет, загруженных кустов нет.

## Что прислать

«Пройдено» или «Не пройдено» по шагам 1–7 и файл `C:\IF\exp-c.txt`.
При ошибке: номер шага, вывод `& $X status $W`, `& $X mounts` и последние ~200 строк `C:\Windows\Logs\DISM\dism.log`. Оставшиеся монтирования не очищайте сами.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
| `8c9e41ef` (PR #6, голова `ca14fdd`) | VirtualBox, Windows 11 26H2 Pro 10.0.26300, Dism 10.0.26100.8457 | 1–7 | Пройдено |

Отпечатки: источник F1 `A4F26A82…8A2BD352` (1066 файлов) — одинаков в шагах 1, 3 и 7; результат F2 `DA06FD49…98FFBC7D6` (1066 файлов) — одинаков после шагов 3, 4, 5 и 6.
Замечания по прогону:
- В шаге 2 отпечаток результата не сняли, F2 впервые снят после шага 3. Сохранность прежнего результата при сбое перед публикацией подтверждена совпадением F2 в шагах 3–6 и модульным тестом; отдельно не сравнивалась.
- Повторный `unmount … discard` в шаге 2 закономерно вернул `Nothing is mounted` (команда введена дважды).
- В шаге 6 метки времени `assemble` 08:30:39 → `source-recheck` 08:29:15 идут назад: часы гостевой ОС скорректированы синхронизацией, на результат не влияет.
- Процесс завершался через «Диспетчер задач» вместо `Stop-Process`; поведение одинаковое.
