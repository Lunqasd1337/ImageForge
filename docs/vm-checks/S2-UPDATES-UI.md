# Ручная проверка S2-UPDATES-UI: обновления в окне, в плане и в запуске

Статус: **Ожидает проверки**.
Проверяет шаг обновлений (план, раздел 11) для локальных пакетов:
- «Прочитать образ» показывает число пакетов обслуживания, версию накопительного обновления (RollupFix) и стека обслуживания (SSU).
- «Найти обновления» читает .msu и .cab из папки и считает их SHA-256; DISM при этом не вызывается.
- В плане файлы с одинаковым содержимым ставятся один раз, пакет с проблемой не принимается, несколько MSU дают предупреждение.
- Запуск сверяет хеш каждого файла с превью, ставит пакеты через DismAddPackage до драйверов и пишет в журнал новые RollupFix и SSU.

Рецепт взят из [S2-UPDATES-INVENTORY](S2-UPDATES-INVENTORY.md): LCU в формате WIM ставится одним DismAddPackage вместе со SSU, около 10 минут. Цель пока одна: образ 1 в `install.wim`.

## Сборка

- Из одной сборки CI этого PR: `ImageForge.App-<sha>` и `ImageForge.Experiments-<sha>`.
- `C:\IF\app` удалить и распаковать туда новый `ImageForge.App`; `ImageForge.Experiments.exe` положить в `C:\IF`.

## Требования

Сервисная VM с контрольной точки `fixture-ready` ([Hyper-V](HYPERV.md)), фикстура `C:\IF\fixture\media`, свободно не меньше 60 ГБ, вход под администратором с UAC. `C:\IF\upd\lcu\Windows11.0-KB5134133-x64.msu` из S2-UPDATES-INVENTORY на месте (если контрольная точка его не содержит, скопировать снова).

PowerShell от администратора. Команды готовят тестовую папку `C:\IF\upd\ui` без копирования 4,9 ГБ (жёсткие ссылки на тот же файл):
- `lcu` и `copy`: один и тот же LCU под двумя путями;
- `bad\notcab.msu`: не cabinet и не WIM.
```powershell
$E = "C:\IF\ImageForge.Experiments.exe"; $X = { & $E @args | Out-Host }
$L = "C:\IF\upd\lcu\Windows11.0-KB5134133-x64.msu"; $U = "C:\IF\upd\ui"
Start-Transcript C:\IF\s2-updates-ui.txt
& $X version
& $X mounts
Remove-Item $U -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $U\lcu, $U\copy, $U\bad -Force | Out-Null
New-Item -ItemType HardLink -Path $U\lcu\Windows11.0-KB5134133-x64.msu -Target $L | Out-Null
New-Item -ItemType HardLink -Path $U\copy\Windows11.0-KB5134133-x64.msu -Target $L | Out-Null
Set-Content $U\bad\notcab.msu "not a cabinet"
Set-Content $U\readme.txt "not scanned"
& $X update-scan $U
```
Ожидается: `mounts` → `none`; `update-scan` находит 3 файла, у обоих KB5134133 — `Wim`, у `bad\notcab.msu` строка `PROBLEM`, итог `with problems: 1`.

## Шаги

### 1. Подключение и чтение образа

Запустить `C:\IF\app\ImageForge.App.exe`, «Подключить движок», в UAC — «Да». Источник `C:\IF\fixture\media`, рабочая папка `C:\IF\ws-upd`, папка результата `C:\IF\result-upd`. «Прочитать образ».
Ожидается: в журнале `протокол 8`; в разделе «Обновления» — «Пакетов обслуживания: 199. Накопительное обновление: 26100.9457.1.0, стек обслуживания: 26100.9441.…».

### 2. Поиск обновлений

«Папка обновлений» — кнопкой «Выбрать...» выбрать `C:\IF\upd\ui` (или ввести путь), «Найти обновления». Чтение идёт до минуты: оба файла хешируются целиком.
Ожидается:
- «Найдено файлов: 3, можно добавить: 2, отмечено: 0»;
- в «Найденные обновления» у `bad\notcab.msu` отметку поставить нельзя, предупреждение о подписи файла;
- у `copy\…` и `lcu\…` вторая строка `Msu · KB5134133 · x64 · WIM · SHA-1 в имени нет` и предупреждение «Такое же содержимое ещё у 1 файлов…».

Снимок окна со списком.

### 3. Превью

Отметить `copy\Windows11.0-KB5134133-x64.msu` и `lcu\Windows11.0-KB5134133-x64.msu`, «Подготовить».
Ожидается:
- статус «План готов… Добавляемых обновлений: 1. …»;
- в превью «Добавить обновление copy\Windows11.0-KB5134133-x64.msu: KB5134133, MSU (WIM)», «То же содержимое, ставится один раз: lcu\Windows11.0-KB5134133-x64.msu», строка про 10–15 минут, «Источник: C:\IF\upd\ui»;
- предупреждения «Выбрано несколько MSU» нет (это один KB).

Снимок окна с превью.

### 4. Файл пропал после превью

Не меняя отметок, в PowerShell:
```powershell
Rename-Item $U\copy\Windows11.0-KB5134133-x64.msu Windows11.0-KB5134133-x64.msu.bak
```
В окне «Запустить».
Ожидается: запуск останавливается на шаге `updates` с ошибкой `Update packages changed or went missing since the preview: copy\Windows11.0-KB5134133-x64.msu.`, DismAddPackage не вызывается, образ размонтируется без сохранения, результат не публикуется.

Вернуть файл:
```powershell
Rename-Item $U\copy\Windows11.0-KB5134133-x64.msu.bak Windows11.0-KB5134133-x64.msu
& $X mounts
```
Ожидается: `mounts` → `none`.

### 5. Запуск

В окне «Подготовить» (отметки те же), затем «Запустить». Установка идёт 10–15 минут, окно не закрывать.
Ожидается в журнале:
- шаг `updates...`, строка `packages: 199; rollup 26100.9457.1.0, servicing stack 26100.9441.…`;
- `add update copy\Windows11.0-KB5134133-x64.msu: KB5134133, MSU (WIM)`, `DismAddPackage C:\IF\upd\ui\copy\…`, проценты `add-package` до 100;
- `update copy\Windows11.0-KB5134133-x64.msu: N new, 89 superseded; rollup 26100.9725.1.2, servicing stack 26100.9703.1.4` и строки `  new Package_for_…`;
- «Готово. Результат сохранён.», `Итог: None Published …`.

Перепишите время шага `updates` (по отметкам времени журнала) и строку `update … new … superseded`.

### 6. Чтение результата

Источник `C:\IF\result-upd`, рабочая папка `C:\IF\ws-upd2`, результат `C:\IF\result-upd2`. «Прочитать образ».
Ожидается: «Пакетов обслуживания: 289. Накопительное обновление: 26100.9725.1.2, стек обслуживания: 26100.9703.1.4.».

### 7. То же обновление ещё раз (эксперимент)

Отметить только `lcu\Windows11.0-KB5134133-x64.msu`, «Подготовить», «Запустить».
Здесь ответ неизвестен заранее, нужен сам результат. Возможны два исхода:
- в журнале `update lcu\…: no change in the image (DISM already holds this package)` и опубликованный результат;
- ошибка шага `updates` со строкой DISM (код `0x…`), образ размонтирован без сохранения.

Перепишите строку журнала и время шага.

### 8. Хост и журнал движка

```powershell
& $X mounts
& $X host-check
Get-ChildItem C:\ProgramData\ImageForge\logs\engine-*.log | Sort-Object LastWriteTime | Select-Object -Last 1 |
  Get-Content -Encoding UTF8 | Select-String "servicing packages|plan add update|plan warning|DismAddPackage"
Stop-Transcript
```
Ожидается:
- `mounts` → `none`, `host-check` → `RESULT: OK`;
- в журнале движка `inspection revision …: … 199 servicing packages (rollup 26100.9457.1.0, servicing stack …)` и такая же строка с 289, `plan add update copy\Windows11.0-KB5134133-x64.msu from C:\IF\upd\ui`, `DismAddPackage …`.

## Что прислать

- файл `C:\IF\s2-updates-ui.txt`;
- снимки окна для шагов 2 и 3;
- журнал окна после шагов 4, 5 и 7.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
| 40f69b16 | Hyper-V | 0–4 | Пройдены. Поиск: 3 файла, 2 можно добавить; `bad\notcab.msu` — «нет подписи MSCF», обе жёсткие ссылки KB5134133 — `WIM` с пометкой «такое же содержимое». Превью: одно добавление `copy\…`, `lcu\…` как то же содержимое, источник `C:\IF\upd\ui`. Файл переименован после превью: запуск остановлен на `updates` до DismAddPackage, образ размонтирован без сохранения, результат не опубликован. Найдено: «ещё у 1 файлов» — склонение исправлено. |
| 40f69b16 | Hyper-V | 5 | Пройден. Перед установкой 199 пакетов, rollup 26100.9457.1.0, SSU 26100.9441.1.11. Шаг `updates` 10,7 мин: 90 новых, 89 вытеснено, rollup 26100.9725.1.2, SSU 26100.9703.1.4. Коммит образа 27 мин (без обновления — около 3 мин), экспорт 2 мин, весь запуск 46 мин; результат опубликован. Найдено: после `add-package 100%` журнал молчит 7 мин (вторая фаза DISM, Current > Total) — теперь в журнал пишется отдельная строка; в превью добавлено, что сохранение образа после LCU занимает до получаса. |
| 40f69b16 | Hyper-V | 6 | Пройден. Чтение result-upd: 289 пакетов, rollup 26100.9725.1.2, SSU 26100.9703.1.4. |
| 40f69b16 | Hyper-V | 7 | Пройден. Тот же LCU на обновлённый образ: DismAddPackage завершился без ошибки за 60 с, список пакетов не изменился (289), в журнале `no change in the image`; коммит 3,5 мин, экспорт 2,5 мин, результат опубликован. Повторное обновление безопасно и стоит только времени. |
