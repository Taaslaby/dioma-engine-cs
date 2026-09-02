# Чеклист верхнеуровневого API организма

Этот план дополняет `PERFORMANCE_CHECKLIST.md`. Низкоуровневые P0/P1
оптимизации уже сохраняют golden/Burst parity; следующий приоритет — убрать
знание внутренностей мозга и биохимии из игрового слоя.

## P0

- [x] Типизированные `SenseFrame` / `ActionFrame` без аллокаций на тик уже есть.
- [x] `SpeciesBinding` один раз разрешает логические порты в диапазоны нейронов.
- [x] `SpeciesCreature.Tick` — канонический путь вида; legacy `Creature` сохранён для совместимости и parity-тестов.
- [x] Добавлен `BrainBlueprint` и прямой `GrowResult -> DiomaBrain`; JSON оставлен для ассетов/golden/tooling.
- [x] Скрыть внешнюю мутацию `Blood.Conc` / `Blood.Loci` за событийным API.
- [x] Удалить опасный `BrainRecipe.WithBackend(instance)`; принимать только фабрику нового владеемого backend.

## P1

- [ ] Разделить наследуемый `OrganismGenome` и технические `BrainBuildOptions`.
- [x] Ввести единый `CreatureSnapshot` для genome/brain/blood/lineage/RNG.
- [x] Отделить `IBrainDiagnostics` от simulation API.
- [ ] Добавить batch API популяции и распараллеливание независимых организмов.

## P2 / совместимость

- [ ] Сохранить старые Brain/Creature методы как compatibility-обёртки до миграции игры.
- [ ] Убрать дорогие диагностические обходы из кадрового HUD либо кэшировать их.
- [ ] Для каждого этапа держать старый и новый пути в parity-тесте до удаления старого.

## Порядок внедрения

1. Backend ownership (изолированный баг, без изменения биологии).
2. BrainPorts + типизированный Brain I/O поверх существующего `DiomaBrain` — уже покрыто `SpeciesBinding` и кадрами.
3. CreatureRuntime.Step и миграция обоих путей тела с parity-тестами — `SpeciesCreature.Tick` уже каноничен.
4. BrainBlueprint и удаление JSON из runtime-рождения — выполнено.
5. Blood encapsulation, snapshot, diagnostics и population API.
