# API сайта SkyNetwork для SkyPilot

Планы полёта подаются на сайте SkyNetwork. SkyPilot их не редактирует, а только забирает и передаёт на FSD-сервер.

Адрес сайта задаётся в настройках SkyPilot, поле «Сайт SkyNetwork». По умолчанию `http://127.0.0.1:8000/`.

## Страница подачи плана

```
GET {site}/flightplan?callsign={CALLSIGN}
```

Кнопка плана полёта в SkyPilot открывает эту страницу в браузере. Параметр `callsign` передаётся, если позывной известен: он введён при подключении или сохранён с прошлого раза.

## Последний поданный план участника

```
GET {site}/api/flightplans/latest?cid={CID}
```

- `200 OK`: план в JSON (формат ниже);
- `404 Not Found`: поданного плана нет.

```json
{
  "rules": "IFR",
  "aircraft": "A20N",
  "cruiseSpeed": 450,
  "departure": "UUEE",
  "destination": "ULLI",
  "alternate": "ULLO",
  "departureTime": "1200",
  "cruiseAltitude": "FL350",
  "enrouteMinutes": 70,
  "fuelMinutes": 180,
  "route": "DCT",
  "remarks": "/V/"
}
```

| Поле | Тип | Описание |
|---|---|---|
| `rules` | строка | `IFR` или `VFR` |
| `aircraft` | строка | ICAO-код типа ВС |
| `cruiseSpeed` | число | истинная скорость, узлы |
| `departure`, `destination`, `alternate` | строка | ICAO-коды аэропортов |
| `departureTime` | строка | время вылета UTC, `HHmm` |
| `cruiseAltitude` | строка | эшелон или высота, например `FL350` |
| `enrouteMinutes`, `fuelMinutes` | число | время в пути и запас топлива, минуты |
| `route`, `remarks` | строка | маршрут и примечания |

## Как это работает в SkyPilot

1. Пилот нажимает на полосу плана полёта (красная «Нет плана полёта»), подаёт план на сайте и нажимает **ОБНОВИТЬ**.
2. SkyPilot запрашивает `api/flightplans/latest`. Полоса становится зелёной и показывает маршрут: `UUEE → ULLI  A20N  FL350`.
3. Если пилот в сети, план отправляется на FSD-сервер пакетом `$FP`, и диспетчеры его видят.
4. Пока пилот в сети, SkyPilot проверяет сайт раз в 30 секунд. Изменённый план отправляется на сервер повторно.
