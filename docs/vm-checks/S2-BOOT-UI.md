# Ручная проверка S2-BOOT-UI: обновление boot.wim и файлов установщика

Статус: **Ожидает проверки**.
Проверяет обслуживание `boot.wim` по рецепту из [S2-TARGETS-INVENTORY](S2-TARGETS-INVENTORY.md):
- у найденного обновления третья отметка «В boot.wim», превью;
- шаг запуска `boot`: копия `boot.wim`, в каждом образе (1 — Windows PE, 2 — Windows Setup) LCU, очистка с /ResetBase и commit; `setup.exe` и `setuphost.exe` из образа 2 заменяют файлы в `sources` носителя; экспорт обоих образов с `/Bootable` у образа 2;
- в результате `boot.wim` с двумя образами и `boot index 2`, версия 26100.9725, новые файлы установщика; носитель загружается.

Чтобы запуск был короче, в сам образ install.wim обновления не ставятся.

## Сборка

- Из одной сборки CI этого PR: `ImageForge.App-<sha>` и `ImageForge.Experiments-<sha>`.
- `C:\IF\app` удалить и распаковать туда новый `ImageForge.App`; `ImageForge.Experiments.exe` положить в `C:\IF`.

## Требования

Сервисная VM, фикстура `C:\IF\fixture\media`, `C:\IF\upd\lcu\Windows11.0-KB5134133-x64.msu`. Свободно не меньше 40 ГБ. Для шага 6 — Windows ADK (Deployment Tools) на сервисной VM и VirtualBox на хосте, как в [M1-INSTALL](M1-INSTALL.md).

PowerShell от администратора:
```powershell
$E = "C:\IF\ImageForge.Experiments.exe"; $X = { & $E @args | Out-Host }
Start-Transcript C:\IF\s2-boot-ui.txt
& $X version
& $X mounts
```
Ожидается: `mounts` → `none`.

## Шаги

### 1. Чтение образа

Запустить `C:\IF\app\ImageForge.App.exe`, «Подключить движок», в UAC — «Да». Источник `C:\IF\fixture\media`, рабочая папка `C:\IF\ws-boot`, папка результата `C:\IF\result-boot`. «Прочитать образ».
Ожидается: в журнале `протокол 12`.

### 2. Отметка и превью

Папка обновлений `C:\IF\upd`, «Найти обновления». У каждого годного файла три отметки: «В образ», «В WinRE», «В boot.wim».
У KB5134133 поставить **только** «В boot.wim», «Подготовить».
Ожидается: в превью строка «boot.wim (sources\boot.wim): обновить все образы…» и «Добавить в boot.wim Windows11.0-KB5134133-x64.msu: KB5134133, MSU (WIM)»; статус «…Обновлений в boot.wim: 1.».

### 3. Запуск

«Запустить», окно не закрывать (около 20–25 минут).
Ожидается в журнале после экспорта install.wim, на шаге `boot...`:
- `boot.wim: 2 images, boot index 2, 569 MB` (около того);
- `boot image 1 of 2: Microsoft Windows PE (amd64)`, `boot packages: …`, `boot add update …`, `boot update …: … new …; rollup 26100.9725…`, `boot cleanup: packages … -> …`;
- то же для `boot image 2 of 2: Microsoft Windows Setup (amd64)` и затем
  `setup file setup.exe: media 10.0.26100.1… -> boot.wim 10.0.26100.9549…`,
  `setup file setuphost.exe: media …9445… -> boot.wim …9725…`;
- `export boot image 1 of 2`, `export boot image 2 of 2 (bootable)`, команда с `/Bootable`;
- `boot.wim: … MB -> … MB, boot index 2`;
- «Готово. Результат сохранён.».

Перепишите строки `boot…`, `setup file …` и время шага `boot`.

### 4. Сверка результата

```powershell
& $X wim-header C:\IF\result-boot\sources\boot.wim
& $X info C:\IF\result-boot\sources\boot.wim
foreach ($f in "setup.exe", "setuphost.exe") {
  foreach ($d in "C:\IF\fixture\media\sources", "C:\IF\result-boot\sources") {
    $i = Get-Item "$d\$f"; "{0} {1}" -f $i.FullName, $i.VersionInfo.FileVersion
  }
}
Get-Item C:\IF\fixture\media\sources\boot.wim, C:\IF\result-boot\sources\boot.wim | Select-Object FullName, Length
```
Ожидается:
- `wim-header`: 2 образа, `boot index 2`;
- `info`: оба образа 10.0.26100.9725;
- `setup.exe` и `setuphost.exe` в результате новее, чем в фикстуре (9549 и 9725); в фикстуре прежние.

### 5. Хост и журнал движка

```powershell
& $X mounts
& $X host-check
Get-ChildItem C:\ProgramData\ImageForge\logs\engine-*.log | Sort-Object LastWriteTime | Select-Object -Last 1 |
  Get-Content -Encoding UTF8 | Select-String "boot|Bootable"
Stop-Transcript
```
Ожидается: `mounts` → `none`, `host-check` → `RESULT: OK`; в журнале `plan add boot.wim update …`, команды `/Cleanup-Image … /ResetBase` в `…\mount-boot` и `/Export-Image … /Bootable`.

### 6. Загрузка носителя (VirtualBox)

Собрать ISO из результата, как в шаге 1 [M1-INSTALL](M1-INSTALL.md), с путём `C:\IF\result-boot` и именем `C:\IF\ImageForge-boot.iso`. Перенести на хост, создать машину (EFI, Secure Boot, TPM 2.0), загрузиться с ISO.
Ожидается: программа установки стартует, есть окно выбора языка и кнопка «Установить»; при «Установить» — выбор редакции. Устанавливать до конца не нужно.

## Что прислать

- файл `C:\IF\s2-boot-ui.txt`;
- журнал окна после шага 3;
- снимок окна программы установки из шага 6.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
