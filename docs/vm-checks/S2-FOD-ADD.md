# Ручная проверка S2-FOD-ADD: добавление дополнительного компонента из папки FoD

Статус: **Пройдена** (сборка 52943a58, Hyper-V, 29.10.2026).
Проверяет добавление Capability (план, раздел 10) из локальной папки Features on Demand: папка `LanguagesAndOptionalFeatures` ISO «Languages and Optional Features» той же сборки, что и образ. ImageForge проверяет, что в папке есть пакеты `.cab` и `metadata\DesktopTargetCompDB_FOD_Neutral.xml.cab`. Затем добавляет только компонент, который образ показывает как `Not Present`, через DISM с `/Source` и `/LimitAccess`, без Windows Update. После добавления список читается снова и печатается всё, что DISM изменил сам. Подходит ли ISO к сборке образа, знает только DISM: шаг 3 это и показывает.
Работа идёт с **копией** `install.wim` в `C:\IF\scratch`.

## Сборка и требования

- `ImageForge.Experiments-<sha>` из CI этого PR, положить в `C:\IF`.
- Сервисная VM ([Hyper-V](HYPERV.md)) на чекпоинте `fixture-ready`, свободно не меньше 15 ГБ. `& $X mounts` → `none`.
- ISO «Languages and Optional Features» для Windows 11 24H2/25H2 (`26100.1.240331-1435.ge_release_amd64fre_CLIENT_LOF_PACKAGES_OEM.iso`, ссылка на [странице Microsoft](https://learn.microsoft.com/ru-ru/azure/virtual-desktop/windows-11-language-packs); отдельного ISO для 26H2 нет, 26H2 стоит на базе 26100) подключён в VM (в Hyper-V: DVD-дисковод → файл образа). Ниже предполагается буква `D:`; если другая, поправьте `$F`.

PowerShell от администратора, по одной строке:
```powershell
$X = { & "C:\IF\ImageForge.Experiments.exe" @args | Out-Host }; $F = "D:\LanguagesAndOptionalFeatures"
Start-Transcript C:\IF\s2-fod-add.txt
& $X version
Get-ChildItem $F\metadata | Select-Object Name
(Get-ChildItem $F -Filter *.cab).Count
Get-ChildItem $F -Filter *OpenSSH* | Select-Object Name
New-Item -ItemType Directory C:\IF\scratch, C:\IF\scratch\mount -Force | Out-Null
Copy-Item C:\IF\fixture\media\sources\install.wim C:\IF\scratch\install.wim -Force
Set-ItemProperty C:\IF\scratch\install.wim -Name IsReadOnly -Value $false
& $X mount C:\IF\scratch\install.wim 1 C:\IF\scratch\mount
& $X capability-list C:\IF\scratch\mount
```
Ожидается: в `metadata` есть `DesktopTargetCompDB_FOD_Neutral.xml.cab`, файлов `.cab` в корне несколько тысяч (в ISO 26100.1 — 4045), есть пакеты OpenSSH для amd64. Монтирование `RESULT: OK`. В `capability-list` строка `OpenSSH.Server~~~~0.0.1.0 NotPresent` — перепишите итоговую строку `Capabilities: N (…)`.

## Шаги

### 1. Отказы без вызова DISM на изменение

```powershell
& $X capability-add C:\IF\scratch\mount OpenSSH.Server~~~~0.0.1.0 C:\IF\fixture\media
& $X capability-add C:\IF\scratch\mount OpenSSH.Client~~~~0.0.1.0 $F
```
Ожидается: первая — `REFUSED: … has no metadata subfolder …`; вторая — `REFUSED: capability 'OpenSSH.Client~~~~0.0.1.0' is not listed as Not Present …` (клиент OpenSSH в образе уже установлен; если у вас он `NotPresent`, напишите). Обе `RESULT: FAIL`, строки `/Add-Capability` нет.

### 2. Добавление OpenSSH.Server

```powershell
& $X capability-add C:\IF\scratch\mount OpenSSH.Server~~~~0.0.1.0 $F
```
Ожидается: `Source …: N package files, metadata present`; в строке Dism.exe есть `/Add-Capability /CapabilityName:OpenSSH.Server~~~~0.0.1.0 /Source:D:\LanguagesAndOptionalFeatures /LimitAccess`; `Capability 'OpenSSH.Server~~~~0.0.1.0': Not Present -> Installed` (или `Install Pending`); `RESULT: OK`. Строки `ALSO CHANGED by DISM:` перепишите, если будут.

Если DISM завершится ошибкой (например `0x800f081f` — источник не найден, или `0x800f0950`), пришлите вывод целиком и номер сборки ISO: значит, ISO не той сборки.

### 3. Компонент с языковыми зависимостями

```powershell
& $X capability-add C:\IF\scratch\mount Language.Speech~~~ru-RU~0.0.1.0 $F
```
Это проверяет, тянет ли DISM за собой другие компоненты. Ожидается `RESULT: OK` и, возможно, строки `ALSO CHANGED by DISM:` (например `Language.Basic~~~ru-RU~0.0.1.0`) — перепишите их. Если `Language.Speech` уже установлен (`REFUSED: … not listed as Not Present`), возьмите любой `Language.Speech~~~<язык>~0.0.1.0` из `capability-list` шага 0, у которого `NotPresent`, например `fr-FR`.

### 4. Уборка

```powershell
& $X unmount C:\IF\scratch\mount discard
& $X mounts
& $X host-check
Stop-Transcript
```
Ожидается: размонтирование `RESULT: OK`, `mounts` → `none`, `host-check` → `RESULT: OK`.

## Что прислать

`C:\IF\s2-fod-add.txt` целиком и номер сборки ISO (из имени файла).

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
| 5025ae83 | Hyper-V | 1 | На `D:` был установочный ISO, не FoD; дистрибутив без `metadata` отклонён верно. |
| 5025ae83 | Hyper-V | 0–1 | ISO 26100.1 LOF: 4045 `.cab`, в `metadata` нет `FoDMetadata_Client.cab` (моё предположение было неверным), есть `DesktopTargetCompDB_FOD_Neutral.xml.cab` и compDB по языкам; проверка отклонила настоящую папку. Исправлено: требуется `DesktopTargetCompDB_FOD_Neutral.xml.cab`. |
| 52943a58 | Hyper-V | 1, 2, 4 | Пройдены. ISO 24H2/25H2 (26100.1) подходит к образу 26H2 (`Image Version: 10.0.26300.9457`, DISM 10.0.26100.8972). Папка принята (4045 пакетов); OpenSSH.Client отклонён как уже установленный без вызова DISM; `OpenSSH.Server~~~~0.0.1.0: Not Present -> Installed`, других изменений DISM не сделал. Шаг 3 не проверен: в русском образе `Language.Speech~~~ru-RU` уже установлен. Уборка чистая. |
| 52943a58 | Hyper-V | 3 (fr-FR) | Пройден. `Language.Speech~~~fr-FR: Not Present -> Installed`, DISM сам добавил `Language.Basic~~~fr-FR` и `Language.TextToSpeech~~~fr-FR` (оба `Installed`), обе показаны строками `ALSO CHANGED by DISM`; Handwriting и OCR не тронуты. Вывод: зависимости языковых компонентов DISM ставит сам и заранее их не сообщает; окно должно предупреждать об этом, а запуск — показывать их после изменения, как у компонентов Windows. |
