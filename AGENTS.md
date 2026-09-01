# Инструкции для агентов

## Текущее состояние

Проект реализован как WPF-приложение на .NET 10. Решение находится в `AVRDUDEPROG2/AVRDUDEPROG2.slnx`, основной проект — `AVRDUDEPROG2/AVRDUDEPROG2`, самотесты без внешних NuGet-пакетов — `AVRDUDEPROG2/AVRDUDEPROG2.Tests`.

Интерфейс компактный, светлый, в стиле Windows 10 с единым синим акцентом. Реализованы Flash, EEPROM, chip erase, calibration, fuse/lock, Drag & Drop, сохранение выбора и профили автоматического программирования. В выходную папку из `references/avrdudeprog33` копируются AVRDUDE 6.1, конфигурация и драйвер USBasp.

## Структура проекта

- `AVRDUDEPROG2/AVRDUDEPROG2.slnx` — решение; `AVRDUDEPROG2/AVRDUDEPROG2` — WPF-приложение, `AVRDUDEPROG2/AVRDUDEPROG2.Tests` — консольные самотесты.
- `MainWindow.xaml(.cs)` — основной интерфейс и координация пользовательских операций; `FuseWriteConfirmationWindow.xaml(.cs)` — отдельный безопасный сценарий подтверждения fuse/lock.
- `Services/AvrdudeService.cs` — формирование команд и запуск AVRDUDE; `LegacyIniService.cs` — чтение описаний МК/программаторов и fuse; `FirmwareFileService.cs` — проверка файлов прошивок; `SettingsService.cs` — хранение настроек.
- `Models/DeviceDefinition.cs` содержит модели МК, fuse и программаторов; `Models/AppSettings.cs` — сохраняемые настройки и профили автопрограммирования.
- `Assets/` — ресурсы приложения (включая PNG для интерфейса и ICO для окна/EXE); `references/avrdudeprog33` — поставляемые AVRDUDE, конфигурации и драйверы; `references/avrdude-main` — лицензионные материалы.
- `App.xaml` содержит общие стили и палитру; `App.xaml.cs`, `AssemblyInfo.cs`, `app.manifest` — запуск и метаданные Windows. Каталоги `bin/` и `obj/` являются результатами сборки и вручную не редактируются.

## Критические инварианты fuse

- Обычные операции Flash/EEPROM не должны формировать fuse/lock-аргументы.
- Перед и после обычной записи выполняется только чтение fuse без lock и сравнение; автоматическое восстановление запрещено.
- AVRDUDE safemode отключён `-u`, чтобы исключить скрытую попытку записи старого снимка.
- Fuse/lock можно записать только после чтения, повторной проверки снимка, просмотра изменений и ввода `ЗАПИСАТЬ`.
- Зарезервированные/недоступные биты сохраняются из считанного байта и не могут меняться профилем или командой «по умолчанию».
- Lock записывается последним, затем обязательно выполняется контрольное чтение.
- Chip erase сохраняет fuse, но снимает lock и может стереть EEPROM в зависимости от EESAVE; предупреждение в UI удалять нельзя.

Таблица fuse-имён берётся из `atmel.ini`, а наличие памяти (`fuse` против `lfuse/hfuse/efuse`) сверяется с `avrdude.conf`, включая `part parent`-наследование. Не определять fuse-память только по полям `atmel.ini`.

## Проверка изменений

```powershell
dotnet build .\AVRDUDEPROG2\AVRDUDEPROG2.slnx
dotnet run --project .\AVRDUDEPROG2\AVRDUDEPROG2.Tests\AVRDUDEPROG2.Tests.csproj --no-build -- .
```

Самотесты проверяют 72 МК, 11 программаторов, наследование конфигурации, поддерживаемые форматы, raw-семантику fuse, сохранение недоступных битов и отсутствие fuse в Flash-команде. Аппаратные write/erase-тесты без отдельного стенда не выполнять.

Подробности для пользователя находятся в корневом `README.md`.
