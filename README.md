# SkyPilot

Пилотский клиент сети виртуальной авиации **SkyNetwork** для **Microsoft Flight Simulator 2020 и 2024**. Написан на C# (.NET 8, WPF).

SkyPilot связывает симулятор с FSD-сервером сети (репозиторий **Skynetwork-fsd**):

- отправляет вашу позицию, высоту, скорость, положение самолёта и код ответчика каждые 5 секунд;
- показывает в симуляторе других пилотов сети как AI-самолёты. Модель подбирается по типу ВС и авиакомпании, движение плавное, с экстраполяцией между обновлениями;
- показывает текстовые сообщения на частотах, на которые настроены COM1/COM2, и личные сообщения в отдельных вкладках;
- отправляет план полёта;
- показывает список диспетчеров рядом. Двойной щелчок настраивает COM1 на частоту диспетчера;
- поддерживает режим ответчика Standby / Mode C и IDENT;
- отвечает на запросы диспетчерских клиентов: тип ВС, имя, ping.

## Команды

| Команда | Действие |
|---|---|
| `текст` | сообщение на частоту COM1 |
| `.com1 118.100`, `.com2 121.500` | настроить радио |
| `.x 7000` (`.xpdr`, `.squawk`) | код ответчика |
| `.ident` | IDENT |
| `.modec` | переключить Standby / Mode C |
| `.msg AFL123 текст` | личное сообщение |
| `.disconnect` | отключиться |
| `.help` | справка |

Во вкладке личного чата текст без точки уходит собеседнику.

## Структура

| Проект | Назначение |
|---|---|
| `src/SkyPilot.Core` | Кроссплатформенное ядро: протокол FSD, сессия, трафик, интерполяция, подбор моделей, команды, настройки |
| `src/SkyPilot.SimConnect` | Связь с MSFS 2020/2024 через SimConnect |
| `src/SkyPilot.App` | Интерфейс на WPF |
| `tools/SimConnectStub` | Заглушка SimConnect, чтобы проект собирался без MSFS SDK (например, в CI) |
| `tests/SkyPilot.Core.Tests` | Тесты ядра, в том числе сквозной тест с настоящим FSD-сервером |

## Сборка

Нужны Windows 10/11 x64, [.NET 8 SDK](https://dotnet.microsoft.com/download) и **MSFS SDK** (устанавливается из режима разработчика MSFS). SDK создаёт переменную окружения `MSFS_SDK` (или `MSFS2024_SDK`). SkyPilot берёт из SDK `Microsoft.FlightSimulator.SimConnect.dll` и `SimConnect.dll`. Эти библиотеки принадлежат Microsoft, поэтому их нет в репозитории.

```powershell
dotnet build SkyPilot.sln -c Release
dotnet run --project src/SkyPilot.App -c Release
```

Если SDK не найден, сборка всё равно пройдёт, но с заглушкой SimConnect. Подключиться к симулятору такая сборка не сможет, и MSBuild об этом предупредит.

## Готовая сборка через GitHub Actions

Workflow **Release** собирает готовый к запуску `SkyPilot.exe` для Windows x64 со встроенным .NET. Результат лежит в артефактах запуска, а при пуше тега `v*` публикуется как GitHub Release.

У MSFS SDK нет публичной ссылки для скачивания, поэтому файлы SimConnect нужно один раз передать workflow:

1. В MSFS включите режим разработчика: *Options → General → Developers → Developer Mode*. Затем установите SDK: *Help → SDK Installers*.
2. На GitHub откройте *Releases → Draft a new release*. В названии укажите `msfs-sdk`, тег `msfs-sdk`.
3. Приложите два файла из папки SDK:
   - `SimConnect SDK\lib\SimConnect.dll`
   - `SimConnect SDK\lib\managed\Microsoft.FlightSimulator.SimConnect.dll`
4. Нажмите **Save draft**, не публикуйте. Черновик видят только владельцы репозитория.

После этого:
- **Разовая сборка:** *Actions → Release → Run workflow*, готовый архив появится в артефактах запуска.
- **Релиз:** `git tag v0.1.0 && git push origin v0.1.0` создаст GitHub Release с архивом `SkyPilot-0.1.0-win-x64.zip`.

## Первый запуск

1. Запустите MSFS и загрузитесь в самолёт. В строке состояния SkyPilot появится «MSFS: подключён».
2. **Настройки**: CID, пароль, имя и адрес FSD-сервера. Пароль хранится в зашифрованном виде (Windows DPAPI).
3. **Подключиться**: позывной и ICAO-код типа ВС.
4. Ответчик по умолчанию в режиме Standby. Перед выруливанием включите **Mode C**.

Настройки лежат в `%APPDATA%\SkyPilot\settings.json`.

## Подбор моделей

Другие пилоты показываются моделями из вашего симулятора. По умолчанию используются стандартные самолёты MSFS. Если нужной модели нет, подставляется Airbus A320neo. Свои правила задаются в `%APPDATA%\SkyPilot\model-matching.json`: пример в [`docs/model-matching.example.json`](docs/model-matching.example.json). Название модели (`title`) — это значение `title=` из `aircraft.cfg` пакета.

## Тесты

```bash
dotnet test tests/SkyPilot.Core.Tests
```

Сквозной тест с настоящим сервером запускается, если указать каталог сборки Skynetwork-fsd:

```bash
SKYNET_FSD_BUILD=/path/to/Skynetwork-fsd/build dotnet test tests/SkyPilot.Core.Tests
```

## Планы

- Голосовая связь (сервер Skynetwork-voice): Opus, PTT, радиоэффекты
- Поддержка Prepar3D и X-Plane
- Прижатие AI-самолётов к земле с учётом высоты рельефа, анимация шасси и фар
- Установщик
