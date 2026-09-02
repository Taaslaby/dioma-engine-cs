# Architecture

## Mental model

Dioma is a closed organism loop:

```text
world → senses → brain → motor pools → body → action → world
                           │                  │
                           └─ cells → tissue → blood
```

The brain receives currents and produces spikes. The body determines the cost of activity, sensory gain, and whether movement can continue. The world does not enter these modules; it only builds an input frame and applies the result.

## Modules and dependencies

| Assembly | Responsibility | May depend on |
|---|---|---|
| `Dioma.Data` | JSON, `.taas`, CRC32 | standard library |
| `Dioma.Spikes` | neuron models, CSR, delays, STDP, sleep | `Dioma.Data` |
| `Dioma.Spikes.Burst` | native kernel implementation | `Spikes`, Unity Burst/Collections |
| `Dioma.Bio` | `Blood`, `BioCellRuntime`, `TissueChemistry` | standard library |
| `Dioma.Body` | `Creature`, `SpeciesCreature`, genome, lifecycle | `Spikes`, `Bio`, `Data` |
| `Dioma.World` | test arena and smoke scenarios | `Body`, `Data` |

The central rule is that `Spikes` does not import `Bio`, and `Bio` does not import `Spikes`. The feedback loop is represented by data and orchestrated by `Body`.

## Frame contracts

Use explicit frames at boundaries:

```text
SenseFrame  — named or indexed sensory values
Brain input — external currents by neuron
Brain output — spikes and motor-pool sums
Bio input   — motor work, glucose deficit, world events
ActionFrame — species-defined actuator values
```

`SpeciesSpec` describes channels and rules as data. `SpeciesBinding` resolves channel names, regions, and pools to indices for a concrete connectome exactly once. The hot loop must not perform string lookup.

## Tick order

1. The world creates a `SenseFrame`.
2. `Body` scales channels by blood sensory gain and builds external currents.
3. `Spikes` runs all neural substeps.
4. `Body` aggregates motor-pool sums.
5. Total motor work is written to metabolic drive loci.
6. `BioCellRuntime` advances local cells and emits secretion.
7. `TissueChemistry` absorbs events, applies decay, and exchanges with blood.
8. `Blood.Update()` runs reactions, decay, emitters, receptors, and organ metabolism.
9. `Body` evaluates rewards, actuators, and sleep state.
10. The world applies the action and records events for the next tick.

Age and death belong to `Lifecycle`, not `Creature.Tick`. This keeps the creature contract clean and separates world direction from neural mathematics.

## Neuron model zoo

Membrane model and execution kernel are separate axes:

| Axis | Options |
|---|---|
| Model | `alpha-lif`, `lif`, `srm0`, `haap` |
| Implementation | managed, Burst |

`HAAP` uses a per-neuron profile of 17 parameters: leak, M-current, h-current, gates, threshold, and reset. Its resting state is solved by bisection, and adaptation limits runaway activity. A missing profile should produce a clear error rather than silently selecting another model.

## Inheritance and lifecycle

`Genome` stores metabolic parameters, instincts, and inherited data. Learned weights must not become automatic behavioral copying: the Baldwin mechanism compresses stable acquired arcs into instincts.

`Lifecycle` separates:

- age;
- senescence;
- starvation damage stored in `Blood`;
- an agony period with a rescue window;
- death and corpse lifetime.

Sleep reduces metabolic load, but must not silently reorder the aging phase.

## Determinism

Numerical trajectories depend on addition order, intermediate numeric types, delays, CSR order, and rounding boundaries. Therefore:

- do not reorder phases without changing and documenting the contract;
- never use wall-clock time as a seed;
- do not parallelize scatter operations without proving identical ordering;
- record connectome and model versions in diagnostics;
- verify Managed and Burst independently.

## Persistence

A save must identify the format version, connectome identity, and state sections. Cellular physiology requires calcium, vesicle, and tissue state. After loading, the next tick must match continuation from the original run.

## Unity boundary

Runtime assemblies must not depend on `UnityEngine`. Unity owns scenes, rendering, physics, and input. This keeps the harness, golden-trace generators, and test arenas runnable under plain .NET.
