# API Reference

## Organism

### `SpeciesCreature`

The primary runtime object for a species bound to a concrete brain.

| API | Purpose |
|---|---|
| `Tick(SenseFrame)` | run one 50 ms tick and return an `ActionFrame` |
| `Blood` | inspect chemistry, loci, and organ state |
| `Brain` | inspect spikes and learning |
| `PoolSums` | inspect spike sums by motor pool |
| `SpawnGenome(seed)` | create a mutated child genome |
| `LearnedInstincts()` | extract stable acquired arcs |

`Tick` requires a frame belonging to the same species. Passing a frame from another `SpeciesSpec` must fail explicitly rather than silently matching values by position.

## Species description

`SpeciesSpec` defines:

- sensory channels and their brain regions;
- motor pools;
- actuators (`Difference`, `Sum`, `FromBody`, `Threshold`, `Ingest`);
- reward rules;
- reflex pairs used by the Baldwin effect.

`Bind(brain)` resolves names to indices and checks required regions. Create the binding once and reuse it.

## Cells

### `BioCellRuntime`

```csharp
var cells = new BioCellRuntime(
    nCells: 4,
    threshold: 0.5f,
    releaseGain: 0.2f,
    calciumResponse: 0.25f,
    vesicleRecoveryRate: 0.05f);
```

Call `Step` after `SetSignal`. Results are available through the read-only `Calcium`, `Vesicles`, and `Secretion` spans.

### `TissueChemistry`

```csharp
var tissue = new TissueChemistry(
    nSubstances: 8,
    volume: 1.0f,
    permeability: 0.1f);
```

Call `AbsorbEvents`, then `StepExchange`. Blood-channel indices are assigned only through `SubstanceMap`.

## Blood

`Blood.Creature()` creates the standard organism configuration. World events are supplied through `IngestFood` and `BumpWall`; motor work and glucose deficit are written to `Loci` before `Update()`.

Do not mutate `Conc` or `Loci` at arbitrary points in the frame: these arrays participate in the low-level tick-order contract.
