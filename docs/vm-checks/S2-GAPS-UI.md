# Ручная проверка S2-GAPS-UI: строки в ресурсах и закрытые пропуски плана

Статус: **Ожидает проверки**.

Проверяет PR #33 (строки в ресурсах) и #34 (пункты 5–11 сверки с планом) одной сборкой:
- **Строки:** все подписи окна берутся из `Strings/Resources.resw`. Если строки нет, вместо текста видно `[Имя]`.
- **Папки:** рабочая папка не на NTFS отклоняется. Источник или результат в синхронизируемой папке даёт предупреждение.
- **Место на диске:**
  - нехватка места останавливает чтение образа до копирования;
  - запуск пишет в журнал оценку нужного места.
- **Второе окно:** если движок уже работает, второе окно не запускает свой и объясняет почему.
- **Порядок шагов install:**
  - компоненты → обновления → драйверы → удаление приложений → очистка → .NET;
  - превью в том же порядке.
- **`/Remove`:** при выключении компонента его файлы удаляются из образа, только если поставлена отметка.
- **Копии пакетов:** перед монтированием пакеты копируются в рабочую папку (строка `staged …`). DISM читает уже копии.

## Сборка

- Из одной сборки CI PR #34: `ImageForge.App-<sha>` и `ImageForge.Experiments-<sha>`.
- `C:\IF\app` удалить и распаковать туда новый `ImageForge.App`; `ImageForge.Experiments.exe` положить в `C:\IF`.

## Требования

- Сервисная VM, фикстура `C:\IF\fixture\media`.
- `C:\IF\upd\lcu\Windows11.0-KB5134133-x64.msu`.
- Свободно не меньше 50 ГБ на `C:`.
- По желанию для шага 5: накопительное обновление .NET Framework 3.5 и 4.8.1 для той же версии Windows из Microsoft Update Catalog. Его файл с `ndp481` в имени положить в `C:\IF\upd\net`.

PowerShell от администратора:
```powershell
$E = "C:\IF\ImageForge.Experiments.exe"; $X = { & $E @args | Out-Host }
Start-Transcript C:\IF\s2-gaps-ui.txt
& $X version
& $X mounts
```
Ожидается: `mounts` → `none`.

## Шаги

### 1. Окно и строки

Запустить `C:\IF\app\ImageForge.App.exe`, «Подключить движок», в UAC — «Да».

Ожидается:
- все подписи, кнопки, заголовки и подсказки на русском, как раньше, нигде нет `[…]`;
- в списке «Кодировка» четыре пункта: «Авто (по BOM)», UTF-16, UTF-8, ANSI;
- в журнале `протокол 14`;
- под списком компонентов новая отметка «Удалять файлы выключаемых компонентов из образа (/Remove; …)».

Пришлите снимок окна целиком (можно двумя-тремя снимками с прокруткой).

### 2. Второе окно

Не закрывая первое окно, запустить второй `C:\IF\app\ImageForge.App.exe`, «Подключить движок», в UAC — «Да».
Ожидается в статусе второго окна: «Движок уже запущен другим окном ImageForge. …». Второе окно закрыть.

### 3. Папки: файловая система, место, синхронизация

Создать два маленьких виртуальных диска внутри гостевой Windows (команды для той же консоли администратора):
```powershell
@"
create vdisk file=C:\IF\exfat.vhdx maximum=1024 type=expandable
attach vdisk
create partition primary
format fs=exfat quick label=IFEXFAT
assign letter=X
create vdisk file=C:\IF\small.vhdx maximum=3072 type=expandable
attach vdisk
create partition primary
format fs=ntfs quick label=IFSMALL
assign letter=Y
"@ | Set-Content C:\IF\vdisk.txt -Encoding ASCII
diskpart /s C:\IF\vdisk.txt
Get-Volume -DriveLetter X, Y | Select-Object DriveLetter, FileSystem, SizeRemaining
```
Ожидается: X — exFAT, Y — NTFS, около 3 ГБ.

В первом окне источник `C:\IF\fixture\media`, папка результата `C:\IF\result-gaps`, затем по очереди:

| Рабочая папка | Действие | Ожидается |
|---|---|---|
| `X:\ws` | «Прочитать образ» | Статус «Папки не подходят: рабочая папка: файловая система exFAT, а для монтирования образа нужен NTFS» |
| `Y:\ws` | «Прочитать образ» | Под «Приложения»: «Образ не прочитан: Not enough space on Y:\: the run needs … GB, … GB is free.»; папки `Y:\ws\base` нет |
| `C:\IF\ws-gaps`, результат `C:\IF\Dropbox\result-gaps` | «Прочитать образ» | Под папками жёлтая строка «Внимание: папка результата: синхронизируемая папка (OneDrive, Dropbox и т. п.). …»; чтение идёт обычно |

После третьей строки вернуть папку результата `C:\IF\result-gaps` и снова «Прочитать образ» (быстро, копия уже есть). Предупреждение должно исчезнуть.

Диски отключить:
```powershell
@"
select vdisk file=C:\IF\exfat.vhdx
detach vdisk
select vdisk file=C:\IF\small.vhdx
detach vdisk
"@ | Set-Content C:\IF\vdisk-off.txt -Encoding ASCII
diskpart /s C:\IF\vdisk-off.txt
Remove-Item C:\IF\exfat.vhdx, C:\IF\small.vhdx, C:\IF\vdisk.txt, C:\IF\vdisk-off.txt
```

### 4. Выбор и превью

В том же окне (рабочая папка `C:\IF\ws-gaps`, результат `C:\IF\result-gaps`):
- «Приложения»: отметить Clipchamp.
- «Компоненты Windows» → «Список компонентов»: снять отметку у `Printing-Foundation-InternetPrinting-Client`.
- Поставить «Удалять файлы выключаемых компонентов из образа».
- Папка обновлений `C:\IF\upd`, «Найти обновления». У KB5134133 только «В образ». Если есть файл .NET из `C:\IF\upd\net`, у него тоже «В образ».
- Поставить «Очистить хранилище компонентов…», без /ResetBase.
- «Подготовить».

Ожидается в превью, сверху вниз:
1. `Выключить компонент … Printing-Foundation-InternetPrinting-Client`, под ним строка «С /Remove: файлы выключаемых компонентов удаляются из образа; …».
2. `Добавить обновление Windows11.0-KB5134133-x64.msu: …` и строки про время и источник.
3. `Удалить приложение Clipchamp …`.
4. `Очистить хранилище компонентов (StartComponentCleanup) после остальных изменений`.
5. Только с .NET: «Обновления .NET Framework: после очистки хранилища компонентов, отдельным шагом» и под ней `Добавить обновление …ndp481…`. Предупреждения «Выбрано несколько MSU» нет.

Статус: «План готов, … Удаляемых приложений: 1. Включаемых компонентов: 0, выключаемых: 1. … Добавляемых обновлений: 1 (или 2). Очистка хранилища компонентов. …».

Пришлите снимок превью.

### 5. Запуск

«Запустить», окно не закрывать (около 50–60 минут).

Ожидается в журнале по порядку:
- `space: work folder volume needs … GB, output volume … GB`;
- `staged 1 update package(s), … GB` (с .NET — `2 update package(s)`);
- `features...`, `disable Printing-Foundation-InternetPrinting-Client with /Remove`;
- `updates...`, `add update Windows11.0-KB5134133-x64.msu …`;
- `appx...`, удаление Clipchamp;
- `cleanup...`, `cleanup component store`, `cleanup: packages … -> …`;
- только с .NET: `dotnet...`, `add update …ndp481…`, `update …ndp481…: … new …`;
- «Готово. Результат сохранён.».

Перепишите строки `space:`, `staged`, `disable …`, `cleanup:` и, если был .NET, `update …ndp481…`.

### 6. Сверка результата

```powershell
$R = "C:\IF\result-gaps"
New-Item -ItemType Directory C:\IF\mount -Force | Out-Null
& $X mount $R\sources\install.wim 3 C:\IF\mount
& $E feature-list C:\IF\mount | Select-String "Printing-Foundation-InternetPrinting-Client "
& $E appx-list C:\IF\mount | Select-String "Clipchamp"
& $E package-list C:\IF\mount | Select-String "RollupFix|NDP"
& $X unmount C:\IF\mount discard
Get-ChildItem C:\IF\ws-gaps -Force | Select-Object Name
& $X mounts
& $X host-check
Stop-Transcript
```
Номер образа в `mount` — тот, что выбран в «Редакции» (Pro, в фикстуре 3).

Ожидается:
- компонент в состоянии `DisabledWithPayloadRemoved`;
- Clipchamp в списке нет;
- RollupFix 26100.9725 `Installed` (с .NET — и пакет NDP);
- в `C:\IF\ws-gaps` есть `workspace.lock`, папки запуска с `inputs` после успешного запуска нет;
- `mounts` → `none`, `host-check` → `RESULT: OK`.

## Что прислать

- `C:\IF\s2-gaps-ui.txt`;
- снимки окна из шагов 1, 2, 3 (три строки таблицы) и 4;
- строки журнала окна из шага 5;
- при ошибке: текст статуса, журнал окна целиком и последний `C:\ProgramData\ImageForge\logs\engine-*.log`.

## Результаты

| Сборка | VM | Шаги | Итог |
|---|---|---|---|
