# Сервисная VM в Hyper-V

Как подготовить сервисную VM для ручных проверок и откатывать её к чистому состоянию перед каждой проверкой. Команды PowerShell выполняются **на хосте** от администратора, если не сказано «в VM».

## 1. Включить Hyper-V

- Нужна Windows 11 Pro, Enterprise или Education и включённая виртуализация в BIOS/UEFI (Intel VT-x или AMD-V).
- «Включение или отключение компонентов Windows» → отметить **Hyper-V** целиком (платформа и средства управления) → перезагрузка.
- После этого VirtualBox работает поверх Hyper-V заметно медленнее. Старую VM лучше не запускать.

## 2. Создать VM

«Диспетчер Hyper-V» → «Действие» → «Создать» → «Виртуальная машина»:

| Шаг мастера | Значение |
|---|---|
| Имя | `ImageForge-Service` |
| Поколение | **2** (без него Windows 11 не встанет) |
| Память | 8192 МБ |
| Сеть | Default Switch |
| Диск | новый VHDX на 128 ГБ, на SSD |
| Установка | из ISO Windows 11 Pro 26H2 |

Не запускай VM сразу, сначала открой «Параметры»:
- **Безопасность**: включить безопасную загрузку (шаблон «Microsoft Windows») и **доверенный платформенный модуль** (TPM нужен Windows 11).
- **Процессор**: 4 виртуальных процессора.
- **Службы интеграции**: отметить «Гостевые службы» (нужно для `Copy-VMFile`).
- **Контрольные точки**: снять «Использовать автоматические контрольные точки».

То же в PowerShell:
```powershell
New-VM -Name ImageForge-Service -Generation 2 -MemoryStartupBytes 8GB -SwitchName "Default Switch" -NewVHDPath "D:\Hyper-V\ImageForge-Service.vhdx" -NewVHDSizeBytes 128GB
Add-VMDvdDrive -VMName ImageForge-Service -Path "D:\ISO\Win11_26H2.iso"
Set-VMFirmware -VMName ImageForge-Service -FirstBootDevice (Get-VMDvdDrive -VMName ImageForge-Service) -SecureBootTemplate MicrosoftWindows
Set-VMKeyProtector -VMName ImageForge-Service -NewLocalKeyProtector
Enable-VMTPM -VMName ImageForge-Service
Set-VMProcessor -VMName ImageForge-Service -Count 4
Enable-VMIntegrationService -VMName ImageForge-Service -Name "Guest Service Interface"
Set-VM -Name ImageForge-Service -AutomaticCheckpointsEnabled $false
```
Пути `D:\Hyper-V\…` и `D:\ISO\…` замени на свои.

## 3. Установить Windows

- «Подключить» → «Пуск». Если появилось «Press any key to boot from CD or DVD», нажми любую клавишу.
- Установка обычная, редакция **Pro**.
- Локальная учётная запись без интернета: на экране выбора сети `Shift+F10` → `start ms-cxh:localonly`. Если в 26H2 это уже не работает, подойдёт и учётная запись Microsoft.
- После входа подключайся в **расширенном сеансе** (кнопка «Расширенный сеанс» на панели). Тогда работают буфер обмена и копирование файлов перетаскиванием.

## 4. Положить фикстуру и утилиту

Фикстура — содержимое ISO. Проще всего взять её с того же виртуального DVD **в VM**:
```powershell
New-Item -ItemType Directory C:\IF\fixture\media -Force | Out-Null
Copy-Item D:\* C:\IF\fixture\media -Recurse
```
(`D:` — буква DVD в VM, проверь в проводнике.)

Утилиту из CI (zip-артефакт распаковать на хосте) копируй **с хоста**:
```powershell
Get-ChildItem "C:\Downloads\ImageForge.Experiments-<sha>" -File -Recurse | ForEach-Object {
  Copy-VMFile -Name ImageForge-Service -SourcePath $_.FullName -DestinationPath ("C:\IF\" + $_.Name) -CreateFullPath -FileSource Host -Force
}
```
Если в артефакте есть подпапки, проще перетащить папку в окно расширенного сеанса.

Проверка **в VM**:
```powershell
$X = { & "C:\IF\ImageForge.Experiments.exe" @args | Out-Host }
& $X version
& $X mounts
& $X host-check
```
Ожидается: `mounts` → `none`, `host-check` → `RESULT: OK`.

## 5. Контрольная точка «чистая VM»

Выключи VM (в VM: «Завершение работы»), затем на хосте:
```powershell
Checkpoint-VM -Name ImageForge-Service -SnapshotName fixture-ready
```
Точку делаем на выключенной VM, тогда в неё не попадает ничего лишнего из памяти.

## 6. Перед каждой проверкой

```powershell
Restore-VMCheckpoint -VMName ImageForge-Service -Name fixture-ready -Confirm:$false
Start-VM ImageForge-Service
```
Затем скопируй новую сборку утилиты (шаг 4) и выполняй шаги из документа проверки.

## 7. Забрать результат

`Copy-VMFile` копирует только с хоста в VM. Transcript (`C:\IF\*.txt`) забирай из расширенного сеанса: скопировать файл в проводнике VM и вставить на хосте.

## Место на диске

- Каждая контрольная точка — отдельный `.avhdx`, он растёт по мере работы. Лишние точки удаляй: `Remove-VMCheckpoint -VMName ImageForge-Service -Name <имя>`.
- Монтирование и копия `install.wim` берут в VM около 15 ГБ. Держи в VM не меньше 30 ГБ свободного места.
