# Developer Guide

## Working process

Before changing code:

1. Identify the owning module and its existing public contract.
2. Inspect assembly dependencies.
3. Locate the Rust reference or explicitly record that none exists.
4. Define units, phase order, and valid ranges.
5. Write the smallest useful test scenario.

After changing code:

```bash
# Fast harness build
export PATH="$PATH:/c/Users/anton/.dotnet"
dotnet build Harness~/smoke.csproj -c Release

# Full gate
scripts/verify.sh
```

## Changes covered by numerical parity

When a golden trace already exists:

1. never edit the trace manually;
2. locate the first divergent operation;
3. compare operation order, `float`/`double`, delays, and clamps;
4. fix the port or regenerate reference data through the agreed Rust process;
5. run `parity`, `bio`, `skinner`, and `arena`.

When no golden trace exists, add invariants. For HAAP these include resting equilibrium, silence without current, current response, step adaptation, and long-run stability.

## Template for a new stateful component

Document:

```text
Purpose: why the component exists
Inputs: values, units, valid range
State: every mutable field and memory layout
Step order: exact operation order
Outputs: return values and buffer ownership
Determinism: seed, random source, numeric requirements
Persistence: version and snapshot fields
Failure modes: errors that must be explicit
```

## API and memory ownership

- Constructors validate sizes, volumes, and coefficients.
- `readonly` does not protect the contents of a mutable array.
- Hot-loop buffers are reused and have a single owner.
- Use `ReadOnlySpan` for zero-copy read access.
- Clear event buffers between ticks.
- Resolve names to indices once in `Bind`.
- Never pass a `SubstanceId` to `Blood` without `SubstanceMap`.

## Test matrix

| Level | Verifies |
|---|---|
| Unit | formulas, boundaries, and API errors |
| Integration | Body → Spikes/Bio → Action ordering |
| Replay | identical continuation from identical state |
| Golden | exact parity with Rust |
| Smoke | build and complete scenario without Unity |
| Performance | allocations and hot-loop cost |

## Diagnosing divergence

Capture a minimal reproducible record containing:

- seed and format version;
- connectome identity;
- neuron model and kernel mode;
- tick and phase;
- input frame;
- first field that differs;
- values before and after the operation.

Compare state first, then inputs, then operation order. Do not start by adding tolerance: tolerance can hide drift that becomes a behavioral change hundreds of ticks later.

## Definition of done

A change is complete when:

- the contract is documented in both languages;
- normal and boundary tests exist;
- snapshot continuation is reproducible;
- dependency direction is preserved;
- no hidden hot-loop allocations were introduced;
- `dotnet build Harness~/smoke.csproj -c Release` passes;
- `scripts/verify.sh` passes;
- golden files were not edited manually.
