# Ручная проверка S2-WINRE-UI: обновление WinRE в запуске

Статус: **Пройдена** (96115ff8, Hyper-V, 30.10.2026).
Проверяет обслуживание среды восстановления по рецепту из [S2-TARGETS-INVENTORY](S2-TARGETS-INVENTORY.md):
- у каждого найденного обновления две отметки — «В образ» и «В WinRE»; превью и предупреждение для WinRE;
- шаг запуска `winre`: `Winre.wim` извлекается из смонтированной редакции, в него ставятся накопительное обновление (даёт только стек обслуживания) и Safe OS Dynamic Update, затем очистка с /ResetBase, commit, export и возврат в образ;
- в результате WinRE выбранной редакции новой версии, у остальных редакций прежний.

Чтобы запуск был коротким, в сам образ обновления не ставятся — только в WinRE.

## Сборка

- Из одной сборки CI этого PR: `ImageForge.App-<sha>` и `ImageForge.Experiments-<sha>`.
- `C:\IF\app` удалить и распаковать туда новый `ImageForge.App`; `ImageForge.Experiments.exe` положить в `C:\IF`.

## Требования

Сервисная VM, фикстура `C:\IF\fixture\media`; на месте `C:\IF\upd\lcu\Windows11.0-KB5134133-x64.msu` и Safe OS DU KB5130881 в `C:\IF\upd\safeos` (из S2-TARGETS-INVENTORY). Свободно не меньше 40 ГБ.

PowerShell от администратора:
```powershell
$E = "C:\IF\ImageForge.Experiments.exe"; $X = { & $E @args | Out-Host }
Start-Transcript C:\IF\s2-winre-ui.txt
& $X version
& $X mounts
Get-ChildItem C:\IF\upd -Recurse -File | Select-Object FullName, Length
Get-PSDrive C | Select-Object @{n='FreeGB';e={[math]::Round($_.Free/1GB,1)}}
```
Ожидается: `mounts` → `none`; в списке файлов LCU и Safe OS DU.

## Шаги

### 1. Чтение образа

Запустить `C:\IF\app\ImageForge.App.exe`, «Подключить движок», в UAC — «Да». Источник `C:\IF\fixture\media`, рабочая папка `C:\IF\ws-re`, папка результата `C:\IF\result-re`. Редакция — `3. Windows 11 Pro` (по умолчанию). «Прочитать образ».
Ожидается: в журнале `протокол 11`, «Меняется «Windows 11 Pro» (образ 3 из 5)…».

### 2. Обновления и отметки

Папка обновлений `C:\IF\upd`, «Найти обновления», раскрыть «Найденные обновления».
Ожидается: у каждого файла две отметки «В образ» и «В WinRE»; у Setup DU (KB5128977, если он в `C:\IF\upd\setup`) обе недоступны — в нём нет `update.mum`.

Отметить у KB5134133 **только** «В WinRE», «Подготовить».
Ожидается: в превью строка «WinRE (Windows\System32\Recovery\Winre.wim): извлечь, обновить…», под ней «Добавить в WinRE … KB5134133 …» и «Внимание: В WinRE накопительное обновление ставит только стек обслуживания…».

Отметить ещё у KB5130881 «В WinRE», «Подготовить».
Ожидается: в превью сначала KB5134133, потом KB5130881, предупреждения нет; статус «…Обновлений в WinRE: 2.». Строк «Добавить обновление» (в образ) нет.

### 3. Запуск

«Запустить», окно не закрывать (около 15–20 минут).
Ожидается в журнале после `winre...`:
- `winre: Windows\System32\Recovery\Winre.wim, 766 MB` (около того);
- `winre packages: 56; servicing stack 26100.9441…`;
- `winre add update … KB5134133 …` и `winre update …: … new …; servicing stack 26100.9703…` — либо строка `0x8007007E, expected for a cumulative update in WinRE; continuing`;
- `winre add update … KB5130881 …` и `  new Package_for_SafeOSDU…9817…`;
- `winre cleanup component store (ResetBase)`, `winre cleanup: packages … -> …`;
- `winre: 766 MB -> commit … MB -> export … MB, put back into the image`;
- затем `registry`, `unmount-commit`, экспорт пяти редакций и «Готово. Результат сохранён.».

Перепишите строки `winre…` и время шага `winre` (по отметкам времени).

### 4. WinRE в результате

```powershell
$S = "C:\IF\scratch"; New-Item -ItemType Directory $S\mount -Force | Out-Null
foreach ($i in 3, 1) {
  & $X mount C:\IF\result-re\sources\install.wim $i $S\mount
  Get-Item -Force $S\mount\Windows\System32\Recovery\Winre.wim | Select-Object Length, Attributes
  Copy-Item -Force $S\mount\Windows\System32\Recovery\Winre.wim "$S\winre-$i.wim"
  & $X unmount $S\mount discard
  Set-ItemProperty "$S\winre-$i.wim" -Name Attributes -Value Normal
  & $X info "$S\winre-$i.wim"
}
```
Ожидается:
- образ 3 (Pro): `Winre.wim` около 800 МБ, версия **10.0.26100.9817**;
- образ 1 (Education): прежний WinRE, 803 МБ, версия 10.0.26100.9444.

### 5. Хост и журнал движка

```powershell
& $X mounts
& $X host-check
Get-ChildItem C:\ProgramData\ImageForge\logs\engine-*.log | Sort-Object LastWriteTime | Select-Object -Last 1 |
  Get-Content -Encoding UTF8 | Select-String "WinRE|winre|plan warning"
Remove-Item $S\winre-*.wim -Force
Stop-Transcript
```
Ожидается: `mounts` → `none`, `host-check` → `RESULT: OK`; в журнале `plan add WinRE update …` для обоих файлов, `plan warning: В WinRE накопительное…` от первой подготовки, `DismAddPackage …` в `…\mount-winre`, `/Cleanup-Image … /ResetBase` и `/Export-Image … winre.wim`.

## Что прислать

- файл `C:\IF\s2-winre-ui.txt`;
- снимок превью после шага 2 и журнал окна после шага 3.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
| 96115ff8 | Hyper-V | 0–2 | Пройдены (со второй попытки: сначала было запущено старое окно). Папка `C:\IF\upd`: 3 файла, Setup DU недоступен. Только KB5134133 «В WinRE» — блок WinRE в превью и предупреждение про Safe OS DU; с KB5130881 — порядок KB5134133, KB5130881, без предупреждения. |
| 96115ff8 | Hyper-V | 3 | Пройден. WinRE 766 МБ, 56 пакетов, SSU 9441. KB5134133: 57 с, только `ServicingStack_9703`, без 0x8007007E. KB5130881: 51 с, `SafeOSDU 26100.9817.1.9`. Cleanup /ResetBase 4 с, 57 → 56. 766 → commit 1094 → export 802 МБ, файл возвращён в образ. Шаг `winre` 3 мин 43 с, весь запуск 17 мин, результат опубликован. |
| 96115ff8 | Hyper-V | 4 | Пройден. Образ 3 (Pro): Winre.wim 841 219 180 байт, 10.0.26100.9817. Образ 1 (Education): 803 275 696 байт, 10.0.26100.9444 — не тронут. |
| 96115ff8 | Hyper-V | 5 | Пройден. `mounts` → none, `host-check` → OK. В журнале движка `plan add WinRE update` для обоих файлов, `plan warning` про Safe OS DU, команды `/Cleanup-Image … /ResetBase` в `mount-winre` и `/Export-Image … winre-export.wim`. |
