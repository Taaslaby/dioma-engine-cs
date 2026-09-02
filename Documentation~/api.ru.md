# Справочник API

## Существо

### `SpeciesCreature`

Главный runtime-объект для вида, привязанного к конкретному мозгу.

| API | Назначение |
|---|---|
| `Tick(SenseFrame)` | выполнить один тик 50 мс и вернуть `ActionFrame` |
| `Blood` | прочитать химии, локусы и состояние органа |
| `Brain` | диагностировать спайки и обучение |
| `PoolSums` | суммы спайков по моторным пулам |
| `SpawnGenome(seed)` | получить мутировавший геном потомка |
| `LearnedInstincts()` | извлечь устойчивые приобретённые дуги |

`Tick` требует кадр каналов именно этого вида. Передача кадра другой `SpeciesSpec` должна быть ошибкой, а не молчаливым сопоставлением по позиции.

## Описание вида

`SpeciesSpec` задаёт:

- сенсорные каналы и их регионы;
- моторные пулы;
- актуаторы (`Difference`, `Sum`, `FromBody`, `Threshold`, `Ingest`);
- правила награды;
- пары рефлексов для эффекта Болдуина.

`Bind(brain)` разрешает имена на индексы и проверяет обязательные регионы. Создавайте binding один раз и переиспользуйте его.

## Клетки

### `BioCellRuntime`

```csharp
var cells = new BioCellRuntime(
    nCells: 4,
    threshold: 0.5f,
    releaseGain: 0.2f,
    calciumResponse: 0.25f,
    vesicleRecoveryRate: 0.05f);
```

После `SetSignal` вызовите `Step`. Результаты доступны через `Calcium`, `Vesicles` и `Secretion` как read-only spans.

### `TissueChemistry`

```csharp
var tissue = new TissueChemistry(
    nSubstances: 8,
    volume: 1.0f,
    permeability: 0.1f);
```

Используйте `AbsorbEvents`, затем `StepExchange`. Индекс химии задаётся только через `SubstanceMap`.

## Кровь

`Blood.Creature()` создаёт стандартную конфигурацию организма. События мира передаются методами `IngestFood` и `BumpWall`, а моторная работа и дефицит глюкозы — через `Loci` до `Update()`.

Не изменяйте `Conc` и `Loci` в произвольной фазе: эти массивы являются частью низкоуровневого контракта порядка тика.
