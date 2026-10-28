# Ручная проверка S2-REG-PARSE: разбор настоящих экспортов regedit

Статус: **Ожидает ручной проверки**.
Проверяет загрузчик и парсер `.reg` (план, раздел 9) на файлах, которые сделал сам Windows, в обоих форматах: Windows Registry Editor Version 5.00 (UTF-16LE) и REGEDIT4 (ANSI). Каждое значение из файла сравнивается с живым реестром VM побайтно. Утилита реестр только читает.
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
Ожидается: `decoded as Utf16`, `format Version5`, у каждого значения `live matches`, `9 values compared`, `RESULT: OK`.

### 3. Экспорт в формате REGEDIT4

1. Откройте `regedit`, перейдите к `HKEY_CURRENT_USER\Software\ImageForgeRegFileTest`.
2. «Файл» → «Экспорт…», тип файла **«Файлы реестра Win9x/NT4 (*.reg)»**, имя `C:\IF\regtest-v4.reg`, «Выбранная ветвь».

```powershell
& $X reg-check C:\IF\regtest-v4.reg auto 1251 Software\ImageForgeRegFileTest
```
Ожидается: `decoded as Ansi (code page 1251)`, `format Regedit4`. В REGEDIT4 нет Юникода, поэтому кириллица должна совпасть, только если кодовая страница системы — 1251. Пришлите вывод как есть, даже если будут `DIFFERS`: это и есть результат проверки ANSI.

### 4. Уборка

```powershell
reg delete "$K" /f
Stop-Transcript
```

## Что прислать

`C:\IF\s2-reg-parse.txt`, а также оба файла `C:\IF\regtest-v5.reg` и `C:\IF\regtest-v4.reg`: они станут эталонными фикстурами в тестах.

## Результаты

| BuildId | VM | Шаги | Итог |
|---|---|---|---|
| — | — | — | Ожидает ручной проверки |
