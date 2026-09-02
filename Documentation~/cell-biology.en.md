# Cell Biology

## Why this layer exists

`Blood` describes systemic chemistry. In living tissue, however, a hormone does not appear instantly throughout the bloodstream: a cell receives a signal, develops a calcium response, releases vesicle contents, and only then does the substance cross a tissue compartment into blood.

```text
neural or physiological signal
                ↓
        BioCellRuntime
 signal → calcium → vesicles → secretion event
                                  ↓
                         TissueChemistry
                    local storage / decay / transport
                                  ↓
                              Blood.Update()
```

This separation provides locality, delay, saturation, and a clear extension point for future cell types.

## Component responsibilities

| Component | Owns | Must not know |
|---|---|---|
| `BioCellRuntime` | calcium, vesicles, release | `Chem` indices, `Blood`, Unity, world |
| `BioEvent` | the result of a cell step | how the event is consumed |
| `SubstanceMap` | `SubstanceId` → `Chem` index | cell dynamics |
| `TissueChemistry` | accumulation, decay, exchange | neural semantics |
| `Blood` | systemic reactions and receptors | cell internals |
| `Body` | phase order and signal meaning | each layer's formula details |

## BioCellRuntime

The runtime represents a homogeneous cell population. State is stored in column-major order: state slot `k` for cell `i` is located at `k * N + i`. This matters for diagnostics and future serialization.

Parameters:

| Parameter | Meaning | Constraint |
|---|---|---|
| `threshold` | activation threshold | may be signed, but the contract must be explicit |
| `releaseGain` | calcium-to-release coupling | non-negative |
| `calciumResponse` | response speed toward activation | normally `[0, 1]` |
| `vesicleRecoveryRate` | pool recovery per step | normally `[0, 1]` |

The step is evaluated in exactly this order:

```text
activation = max(signal - threshold, 0)
calcium = calcium + (activation - calcium) * calciumResponse
release = min(calcium * releaseGain * vesicles, vesicles)
vesicles = clamp(vesicles - release + (1 - vesicles) * recoveryRate, 0, 1)
```

`Secretion` is the output of the latest step, not an accumulating queue. The host must consume it and turn it into `BioEvent[]`.

## Events and substances

`SubstanceId` is intentionally separate from `Chem`. For example, a species may define `SubstanceId(2)` as a “well-being signal” and bind it to `Chem.Serotonin` for a particular body:

```csharp
var map = new SubstanceMap(3);
map.Bind(new SubstanceId(2), Chem.Serotonin);
```

Create this mapping once during initialization rather than resolving it inside the hot loop.

## TissueChemistry

Tissue stores concentrations by abstract substance and has its own volume. An event with value `value` adds an amount of substance, therefore:

```text
Δconcentration = value / tissueVolume
```

Local decay is applied before exchange. Material then moves along the concentration gradient, with quantity converted through compartment volumes:

```text
amount = concentration × volume
```

Transport must be bounded by:

- material available in the source compartment;
- free blood capacity;
- permeability;
- non-negative concentration.

## Canonical integration order

```csharp
cells.SetSignal(cellSignals);
cells.Step();

// Example: one BioEvent per cell with non-zero secretion.
tissue.AbsorbEvents(events);
tissue.StepExchange(blood, bloodVolume, dt, substanceMap);
blood.Update();
```

Do not reorder these phases. Calling `Blood.Update()` before exchange delays receptor input by one tick. Transporting before `Decay` changes the local decay contract. Mutating blood directly from a cell bypasses the module boundary.

## Test checklist

The cell layer should cover:

1. sub-threshold input produces no release;
2. above-threshold input produces positive release;
3. release never exceeds available vesicles;
4. the vesicle pool recovers after depletion;
5. an unbound substance does not change blood;
6. zero permeability produces no exchange;
7. transport conserves quantity across compartment volumes;
8. identical state produces an identical next step;
9. snapshot restore produces an identical continuation;
10. the legacy `Blood`-only scenario behaves as before.

## Integration failures

**A hormone appears instantly throughout blood.** The cell or tissue layer was probably bypassed.

**A cell calls `Blood` directly.** This couples independent modules and makes parity tests fragile.

**A substance never reaches blood.** Check `SubstanceMap`, the `Chem` index, volumes, and gradient direction.

**Exchange creates mass from nothing.** Check source availability, both volume conversions, and gradient sign.

**The result depends on execution order.** Ensure event buffers are cleared or recreated per tick and random state is not global.

## Future extensions

The natural next level is to add heterogeneous cell types, receptor profiles, and multiple tissue compartments. Build these on top of the current contract: do not fold secretion into `Blood` formulas or alter default behavior for existing organisms.
