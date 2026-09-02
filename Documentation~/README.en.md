# Dioma Engine

## About

Dioma Engine is a deterministic, headless-compatible Unity core for growing spiking organisms. An organism combines a neural brain, biochemical body, cellular physiology, and a world-facing interface.

The project optimizes for three properties:

- **behavior**: an organism must perceive, learn, tire, and act rather than merely produce spikes;
- **reproducibility**: identical inputs and seeds produce identical trajectories;
- **extensibility**: new species, neuron models, and physiology must not break established contracts.

## Navigation

| Section | Audience | Contents |
|---|---|---|
| [Architecture](architecture.en.md) | every developer | layers, dependencies, and tick order |
| [Cell Biology](cell-biology.en.md) | physiology authors | cells, calcium, vesicles, tissue, and blood |
| [Developer Guide](development.en.md) | contributors | verification, parity, APIs, and diagnostics |

Russian versions: `README.ru.md`, `architecture.ru.md`, `cell-biology.ru.md`, and `development.ru.md`.

## Quick start

1. Open the Unity package root.
2. Run `scripts/verify.sh`.
3. Read `architecture.en.md` before changing phase order.
4. Add boundary and determinism tests with every new stateful feature.

```bash
scripts/verify.sh
```

## Project boundaries

```text
Dioma.Data   → persistence and formats
Dioma.Spikes → brain and learning
Dioma.Bio    → blood, cells, and tissue
Dioma.Body   → brain-body integration
Dioma.World  → test world
```

`Spikes` and `Bio` are intentionally independent. Only `Body` connects them. This is an architectural constraint, not a style preference: it prevents hidden reverse dependencies and protects numerical parity.

## Definition of done

A change is complete when:

- its public contract and units are documented;
- computational phase order is preserved;
- normal, boundary, and replay tests exist;
- `scripts/verify.sh` passes without editing golden traces;
- Russian and English documentation remain aligned.
