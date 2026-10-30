# Ручная проверка S2-REG-PARSE: разбор настоящих экспортов regedit

Статус: **Пройдена** (40f69b16, Hyper-V, 30.10.2026).
Проверяет загрузчик и парсер `.reg` (план, раздел 9) на файлах, которые сделал сам Windows, в формате Windows Registry Editor Version 5.00 (UTF-16LE), и что REGEDIT4 отклоняется (решение владельца от 28.10.2026). Каждое значение из файла сравнивается с живым реестром VM побайтно. Утилита реестр только читает.
Тестовый ключ создаётся в `HKCU` **сервисной VM** и в конце удаляется. На хосте ничего не выполняется.

## Сборка

`ImageForge.Experiments-<sha>` из CI этого PR, положить в `C:\IF` поверх старого.

## Шаги

PowerShell от администратора, по одной строке:
```powershell
$X = { & "C:\IF\ImageForge.Experiments.exe" @args | Out-Host }
Start-Transcript C:\IF\s2-reg-parse.txt
& $X version
```

### 1. Тестовый ключ со всеми типами

```powershell
$K = "HKCU\Software\ImageForgeRegFileTest"
reg add "$K" /v "Строка" /t REG_SZ /d "Привет; это не комментарий \ а обратная черта" /f
reg add "$K" /ve /t REG_SZ /d "по умолчанию" /f
reg add "$K" /v "Путь" /t REG_EXPAND_SZ /d "%SystemRoot%\Тест" /f
reg add "$K" /v "Список" /t REG_MULTI_SZ /d "один\0два\0три" /f
reg add "$K" /v "Число" /t REG_DWORD /d 0x12345678 /f
reg add "$K" /v "Большое" /t REG_QWORD /d 0x1122334455667788 /f
reg add "$K" /v "Двоичное" /t REG_BINARY /d 00FF10AB /f
reg add "$K" /v "Пустое" /t REG_NONE /f
reg add "$K\Вложенный ключ" /v "Длинное" /t REG_BINARY /d (("0123456789ABCDEF" * 16)) /f
```
Ожидается: каждая команда — `Операция успешно завершена`.

### 2. Экспорт в формате v5

```powershell
reg export "$K" C:\IF\regtest-v5.reg /y
& $X reg-check C:\IF\regtest-v5.reg auto 1251 Software\ImageForgeRegFileTest
```
Ожидается: `decoded as Utf16`, у каждого значения `live matches`, `9 values compared`, `RESULT: OK`.

### 3. REGEDIT4

Не проверяется в VM: формат REGEDIT4 не поддерживается по решению владельца, его отказ покрыт тестами `RegFileLoaderTests`.

### 4. Уборка

```powershell
reg delete "$K" /f
Stop-Transcript
```

## Что прислать

`C:\IF\s2-reg-parse.txt` и файл `C:\IF\regtest-v5.reg`: он станет эталонной фикстурой в тестах.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
| 40f69b16 | Hyper-V | 1–2 | Пройдены. `reg export` записал UTF-16LE с BOM; `reg-check`: `decoded as Utf16`, 11 операций, все 9 значений `live matches` (кириллица в именах и данных, `;` и `\` в строке, EXPAND_SZ, MULTI_SZ, DWORD, QWORD, BINARY, NONE `00,00`, длинный BINARY с переносами), `RESULT: OK`. Файл добавлен эталоном: `tests/ImageForge.Core.Tests/Registry/Fixtures/regtest-v5.reg`, тест `RegFileFixtureTests`. Шаг 3 не выполнялся (см. выше). |
