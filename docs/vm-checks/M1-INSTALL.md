# Ручная проверка M1-INSTALL: установка результата и вход в WinRE

Статус: **Ожидает ручной проверки** (после M1-RUN).
Покрывает проверки M1 4–6: чистая установка результата в VM с UEFI и Secure Boot, первый вход и перезагрузка; маркер `HKLM\SOFTWARE\ImageForge\Validation\Milestone = M1` в установленной системе и его отсутствие в реестре хоста; регистрация Windows RE и реальный вход в неё с возвратом в Windows.
По решению владельца от 28.10.2026 вместо Hyper-V используется VirtualBox 7.

Экспорт ISO не входит в продукт: ISO собирается вручную стандартной утилитой `oscdimg` из Windows ADK.

## Что нужно

- Результат шага 4 пакета M1-RUN: `C:\IF\result-ui` на сервисной VM (в нём маркер уже проверен).
- **Windows ADK**, компонент **Deployment Tools** — на сервисной VM (не на хосте). Установщик `adksetup.exe` с сайта Microsoft (Windows ADK для Windows 11), при установке отметьте только «Deployment Tools».
- Способ перенести ISO (~6 ГБ) из сервисной VM на хост: общая папка VirtualBox (нужны Guest Additions) или сетевая папка.
- **VirtualBox 7.0 или новее** на хосте, свободно ~70 ГБ.

Обозначения на сервисной VM (PowerShell от администратора, по одной строке):
```powershell
$R = "C:\IF\result-ui"
$X = "C:\IF\ImageForge.Experiments.exe"
Start-Transcript C:\IF\m1-install.txt
```

## Шаги

### 1. Сборка ISO (сервисная VM)

```powershell
$O = "C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe"
Test-Path $R\boot\etfsboot.com, $R\efi\microsoft\boot\efisys.bin
& $O -m -o -u2 -udfver102 "-bootdata:2#p0,e,b$R\boot\etfsboot.com#pEF,e,b$R\efi\microsoft\boot\efisys.bin" $R C:\IF\ImageForge-M1.iso
& $X hash C:\IF\ImageForge-M1.iso
& $X manifest $R
```
Ожидается: оба `Test-Path` — `True`; `oscdimg` завершается `Done.`; запишите SHA-256 ISO и отпечаток результата.
Перенесите `C:\IF\ImageForge-M1.iso` на хост и сверьте хеш там: `Get-FileHash <путь>\ImageForge-M1.iso -Algorithm SHA256`.

### 2. Новая VM для установки (хост, VirtualBox)

Создать машину → **Тип Windows, версия Windows 11 (64-bit)**, ISO — `ImageForge-M1.iso`, **«Пропустить автоматическую установку»** (Skip Unattended Installation) — отметить. Далее:
- память 4096 МБ или больше, 2 процессора и больше;
- диск 64 ГБ (динамический);
- в настройках машины: Система → **Включить EFI**, **Secure Boot** включён, **TPM 2.0**;
- Сеть: на время установки можно оставить NAT.

Снимок машины перед первым запуском — по желанию.

### 3. Чистая установка, первый вход, перезагрузка (гостевая система)

1. Запустите машину; при надписи `Press any key to boot from CD or DVD...` нажмите любую клавишу.
2. Установка Windows: язык → «Установить» → **«У меня нет ключа продукта»** → редакция в списке должна быть одна (Windows 11 Pro) → выборочная установка на пустой диск.
3. Первый запуск (OOBE). Если требуется вход в учётную запись Microsoft, а локальная учётная запись удобнее: отключите сеть машины (Устройства → Сеть → снять «Подключить кабель»), нажмите Shift+F10 и выполните `start ms-cxh:localonly`. Если этот способ в 26H2 не сработает — используйте учётную запись Microsoft, это тестовая машина; напишите, какой путь сработал.
4. Войдите в систему, затем **Пуск → Перезагрузка**, войдите снова.

Ожидается: установка без ошибок, рабочий стол после первого входа и после перезагрузки.

### 4. Маркер в установленной системе (гостевая система)

В PowerShell **от имени администратора** внутри установленной машины:
```powershell
reg query HKLM\SOFTWARE\ImageForge\Validation /v Milestone
Confirm-SecureBootUEFI
(Get-CimInstance Win32_OperatingSystem) | Select-Object Caption, Version, BuildNumber
```
Ожидается: `Milestone    REG_SZ    M1`; `True` (Secure Boot включён); Windows 11 Pro, сборка 26300.

### 5. Маркера нет на хосте

На **хосте** (вашей основной Windows) в обычном PowerShell:
```powershell
reg query HKLM\SOFTWARE\ImageForge\Validation /v Milestone
```
Ожидается: `ОШИБКА: не удается найти указанный раздел или параметр реестра` (маркера нет).
И на сервисной VM: `& $X host-check` → `<absent>`.

### 6. Windows RE: регистрация и реальный вход (гостевая система)

1. PowerShell от администратора:
   ```powershell
   reagentc /info
   ```
   Ожидается: `Windows RE status: Enabled` (`Состояние среды Windows RE: Enabled`) и путь `\\?\GLOBALROOT\device\harddisk0\partition…\Recovery\WindowsRE`.
2. Вход по запросу из гостевой системы:
   ```powershell
   reagentc /boottore
   shutdown /r /t 0
   ```
   Ожидается: после перезагрузки синий экран «Выбор действия» (Choose an option). Сделайте снимок экрана.
3. Выберите **«Продолжить»** (Continue: выйти и перейти к Windows 11). Ожидается: обычная загрузка, вход в систему.
4. Ещё раз `reagentc /info` — по-прежнему `Enabled`.

### 7. Итог на сервисной VM

```powershell
& $X mounts
Stop-Transcript
```
Ожидается: монтирований нет.

## Что прислать

«Пройдено» / «Не пройдено» по шагам 1–7, `C:\IF\m1-install.txt`, вывод команд шагов 4–6 из установленной машины (текстом), снимок экрана WinRE, версию VirtualBox и какой путь входа в OOBE сработал.
При ошибке — номер шага, снимок экрана и точный текст сообщения.

## Результаты

| Сборка результата | ISO (SHA-256) | VM | Шаги | Итог |
|---|---|---|---|---|
| — | — | — | — | Ожидает ручной проверки |
