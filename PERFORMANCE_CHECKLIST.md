# Чеклист оптимизации stage-C

Baseline от 2026-09-02 (`Harness~/smoke.csproj`, Release, managed):

- все headless-проверки: 4/4 PASS;
- parity stage-C: 1,48 мс/тик;
- arena stage-C: 1,23 мс/тик.

После каждого пункта обязательны Release-сборка и полный headless-прогон.
Изменения порядка float-операций дополнительно требуют Unity EditMode golden/Burst parity.

## P0 — сначала

- [x] Delayed rewards: убрать временные `List` через in-place compaction.
- [x] Managed membrane kernel: держать исходный массив весов; `SyncWeights` — no-op для той же ссылки.
- [x] Managed ring: убрать `Array.Copy(ring -> input)` на каждом подшаге.
- [x] Total spikes: считать в уже обязательном проходе `NoteSpikes`.
- [x] Fired neurons: не сканировать историю повторно ради `_prevFired`.
- [x] AlphaLIF/LIF: проверить golden parity после слияния ext-прохода с основным циклом.
- [x] Plasticity: убрать лишний `Array.Clear(_ltp)`.
- [x] Plasticity: второй проход выполнять строками CSR.
- [x] Homeostasis: заранее вычислить список нормализуемых синапсов.

## P1 — после P0

- [x] Bookkeeping delayed reward/homeostasis выполнять один раз за тик.
- [x] Слить dopamine update с первым проходом Normalize.
- [x] Объединить NoteSpikes + Apply в tick-level API.
- [x] Перевести внутреннюю spike history `int[] -> byte[]`, сохранив публичный `IReadOnlyList<int>`.
- [x] D1/D2 обновлять рядом с владельцем eligibility, без `ReadEligibility` copy.
- [ ] На уровне арены распараллелить независимые мозги, сохранив порядок внутри мозга.
- [ ] HAAP: кэшировать RestingState для одинаковых профилей.
- [ ] HAAP: исследовать Burst + SoA параметры.

## P2 / отдельный fast-режим

- [ ] Добавить отдельный HAAP Fast; reference HAAP не менять.
- [ ] В HAAP Fast заменить `MathF.Exp` приближением/LUT и измерить ошибку.
- [ ] В HAAP Fast предвычислить обратные константы; зафиксировать изменение rounding.
- [ ] Проверить, что диагностика (`Clone`, `WeightSum`, `RegionPairWeight`) не вызывается на hot path.

## Журнал

- [x] Зафиксирован чистый baseline до изменений.
- [x] Пакет 1: Release build + headless 4/4 PASS; stage-C parity 1,27 мс/тик.
- [x] Пакет 2: Release build + headless 4/4 PASS; stage-C arena 1,18 мс/тик.
- [x] Пакет 3: Release build + headless 4/4 PASS; stage-C parity 1,14 мс/тик.
- [x] Пакет 4: ext-проход слит; headless 4/4 PASS; stage-C arena 1,12 мс/тик.
- [x] P0 целиком: Unity EditMode 162/162 PASS, включая managed/Burst parity.
- [x] P1 tick-level plasticity: headless 4/4 и Unity 162/162 PASS; Burst full tick 2,41 → 1,61 мс.
- [x] P1 memory/reward: headless 4/4 и Unity 162/162 PASS; Burst full tick 1,51 мс.
- [x] Итог после P0+P1+API: headless 4/4, Unity EditMode 164/164, PlayMode 14/14 PASS; stage-C parity 1,06 мс/тик, arena 0,16 мс/тик (baseline был 1,48/1,23).
