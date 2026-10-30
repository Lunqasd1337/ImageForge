# Ручная проверка S2-CLEANUP-UI: очистка хранилища компонентов после обновления

Статус: **Пройдена** (3450e2f1, Hyper-V, 30.10.2026; правки окна по итогам — в следующем коммите PR).
Проверяет очистку образа (план, раздел 11):
- галочки «Очистить хранилище компонентов» и «Вместе с /ResetBase» в окне, строки превью и предупреждение про /ResetBase;
- шаг запуска `cleanup` после всех остальных изменений: `Dism.exe /Cleanup-Image /StartComponentCleanup [/ResetBase]`, в журнале число пакетов и вытесненных до и после;
- насколько меньше становится `install.wim` после обновления KB5134133.

Источник — результат S2-UPDATES-UI `C:\IF\result-upd`: в нём 289 пакетов, из них 89 вытесненных, так что LCU ставить заново не нужно.

## Сборка

- Из одной сборки CI этого PR: `ImageForge.App-<sha>` и `ImageForge.Experiments-<sha>`.
- `C:\IF\app` удалить и распаковать туда новый `ImageForge.App`; `ImageForge.Experiments.exe` положить в `C:\IF`.

## Требования

Сервисная VM в состоянии после S2-UPDATES-UI (`C:\IF\result-upd` на месте; на `fixture-ready` не откатывать), свободно не меньше 40 ГБ.

PowerShell от администратора:
```powershell
$E = "C:\IF\ImageForge.Experiments.exe"; $X = { & $E @args | Out-Host }
Start-Transcript C:\IF\s2-cleanup-ui.txt
& $X version
& $X mounts
Get-PSDrive C | Select-Object @{n='FreeGB';e={[math]::Round($_.Free/1GB,1)}}
```
Ожидается: `mounts` → `none`. Если свободно меньше 40 ГБ, удалить рабочие папки прошлых проверок (результат `result-upd` не трогать):
```powershell
Remove-Item C:\IF\ws-upd, C:\IF\ws-upd2, C:\IF\result-upd2 -Recurse -Force -ErrorAction SilentlyContinue
```

## Шаги

### 1. Чтение образа

Запустить `C:\IF\app\ImageForge.App.exe`, «Подключить движок», в UAC — «Да». Источник `C:\IF\result-upd`, рабочая папка `C:\IF\ws-cl1`, папка результата `C:\IF\result-cl1`. «Прочитать образ».
Ожидается: в журнале `протокол 9`; в «Обновления» — «Пакетов обслуживания: 289. Накопительное обновление: 26100.9725.1.2, стек обслуживания: 26100.9703.1.4.».

### 2. Галочки

Ниже списка обновлений две галочки.
- Пока «Очистить хранилище компонентов» снята, «Вместе с /ResetBase» недоступна.
- Поставить обе, затем снять «Очистить…». Ожидается: «/ResetBase» тоже снялась и снова недоступна.

### 3. Превью без /ResetBase

Поставить только «Очистить хранилище компонентов», других отметок нет. «Подготовить».
Ожидается: статус «План готов… Очистка хранилища компонентов. …», в превью «Очистить хранилище компонентов (StartComponentCleanup) после остальных изменений», предупреждений нет.

### 4. Запуск без /ResetBase

«Запустить», окно не закрывать.
Ожидается в журнале:
- шаг `cleanup...`, `packages: 289, superseded 89`, `cleanup component store`;
- строка `…Dism.exe /English /Image:… /Cleanup-Image /StartComponentCleanup`;
- `cleanup: packages 289 -> …, superseded 89 -> …; rollup 26100.9725.1.2, servicing stack 26100.9703.1.4`;
- «Готово. Результат сохранён.».

Перепишите строку `cleanup: …` и время шага `cleanup` (по отметкам времени).

### 5. Запуск с /ResetBase

Источник `C:\IF\result-upd`, рабочая папка `C:\IF\ws-cl2`, результат `C:\IF\result-cl2`. «Прочитать образ». Поставить обе галочки, «Подготовить».
Ожидается: статус «…Очистка хранилища компонентов с /ResetBase.», в превью «Очистить хранилище компонентов (StartComponentCleanup /ResetBase)…» и «Внимание: С /ResetBase вытесненные версии компонентов удаляются насовсем…».

«Запустить». Ожидается: `cleanup component store (ResetBase)`, команда с `/ResetBase`, `cleanup: packages 289 -> …, superseded 89 -> 0` (или около нуля), «Готово. Результат сохранён.». Перепишите строку `cleanup: …` и время шага.

### 6. Размеры и чтение результата

```powershell
Get-Item C:\IF\fixture\media\sources\install.wim, C:\IF\result-upd\sources\install.wim, C:\IF\result-cl1\sources\install.wim, C:\IF\result-cl2\sources\install.wim |
  Select-Object FullName, @{n='GB';e={[math]::Round($_.Length/1GB,2)}}
```
Пришлите таблицу. Затем в окне: источник `C:\IF\result-cl2`, рабочая папка `C:\IF\ws-cl3`, «Прочитать образ». Ожидается: rollup 26100.9725.1.2 и SSU 26100.9703.1.4 на месте, пакетов меньше 289.

### 7. Хост и журнал движка

```powershell
& $X mounts
& $X host-check
Get-ChildItem C:\ProgramData\ImageForge\logs\engine-*.log | Sort-Object LastWriteTime | Select-Object -Last 1 |
  Get-Content -Encoding UTF8 | Select-String "plan cleanup|plan warning|Cleanup-Image"
Stop-Transcript
```
Ожидается: `mounts` → `none`, `host-check` → `RESULT: OK`; в журнале `plan cleanup component store` и `plan cleanup component store (ResetBase)`, `plan warning: С /ResetBase…`, обе команды `/Cleanup-Image`.

## Что прислать

- файл `C:\IF\s2-cleanup-ui.txt`;
- журнал окна после шагов 4 и 5;
- таблицу размеров из шага 6.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
| 3450e2f1 | Hyper-V | 0–3 | Пройдены. Протокол 9; result-upd: 289 пакетов, rollup 26100.9725.1.2. Галочка /ResetBase недоступна без очистки и снимается вместе с ней. Превью без /ResetBase — одна строка без предупреждений. |
| 3450e2f1 | Hyper-V | 4 | Пройден. `cleanup`: 289 → 201 пакет, вытесненных 89 → 0 за 2,5 мин (в офлайн-образе DISM не ждёт 30 дней); rollup и SSU на месте. Коммит 8 мин, весь запуск 19 мин, результат опубликован. |
| 3450e2f1 | Hyper-V | 5 | Пройден. Превью с предупреждением про /ResetBase, команда с `/ResetBase`; 289 → 201, вытесненных 0, 2,75 мин; результат опубликован. |
| 3450e2f1 | Hyper-V | 6 | Пройден. install.wim: фикстура 7,22 ГБ, после LCU 9,27 ГБ, после очистки 7,16 ГБ, после очистки с /ResetBase 7,16 ГБ. Очистка возвращает 2,1 ГБ; /ResetBase в офлайн-образе размера не меняет, только запрещает удалять обновления. Чтение result-cl2: 201 пакет, rollup 9725, SSU 9703. Найдено: после повторного чтения образа в превью оставался прежний план, а галочка /ResetBase — стоять; теперь превью очищается, /ResetBase снимается; подпись галочки уточнена. |
| 3450e2f1 | Hyper-V | 7 | Пройден. `mounts` → none, `host-check` → OK. В журнале движка `plan cleanup component store` и `… (ResetBase)`, `plan warning: С /ResetBase…`, обе команды `/Cleanup-Image /StartComponentCleanup` (вторая с `/ResetBase`), инспекции с 199, 289 и 201 пакетами. |
