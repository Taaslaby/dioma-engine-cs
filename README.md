# Dioma Engine

Движок выращиваемых спайковых существ для Unity: мозг — настоящая спайковая
сеть (LIF-alpha + дофаминовая STDP), тело — биохимия крови (голод,
утомление, гормоны), мир — забота игры.

Порт Rust-движка dioma-engine; паритет с эталоном держат тесты золотых
следов.

## Модули

| Сборка | Что | Зависимости |
|---|---|---|
| `Dioma.Data` | `.taas` v2 — сохранения; `Json`, `Crc32` | нет |
| `Dioma.Spikes` | `DiomaBrain` — LIF-alpha, mean-field STDP, гомеостаз, сон, память Болдуина; `ISpikeKernel`/`IPlasticityKernel` — сменные вычислители; `SkinnerBox` | Data |
| `Dioma.Spikes.Burst` | те же алгоритмы в нативном коде: мембрана ×1.5, пластичность ×4, тик ×1.9 | Spikes, Burst, Collections, Mathematics |
| `Dioma.Bio` | `Blood` — реакции, рецепторы, эмиттеры, орган «мозг» | нет |
| `Dioma.Body` | `Creature` — мост сенсоры→мозг→тело→действие; `SpeciesSpec`/`SpeciesCreature` — то же по спецификации вида; `Genome`/`Instinct` — наследственность; `CreatureSave` | Spikes, Bio, Data |
| `Dioma.World` | `TestArena` — эталонный мирок; `SpeciesArena` — мир по каналам вида; `ArenaSmoke`/`BioSmoke` — headless-проверки | Body, Data |

Spikes и Bio изолированы компилятором: спайки ничего не знают о химии,
химия — о спайках. `Dioma.Spikes` не зависит от Burst — модуль регистрирует
себя сам, и билд без него работает на обычном C#. Все модули, кроме
Burst-обёртки, `noEngineReferences` — работают headless и под чистым .NET.
Подробности — `Documentation~/architecture.md`.

## Жизненный цикл существа

```
рождение   наследство весов + прожигание врождённых рефлексов + снимок памяти
   ↓
день       сенсоры → мозг → действие; дофамин за еду, LTD за боль
   ↓
ночь       окно обучения закрывается, сильное закрепляется, шум обрезается
   ↓
потомство  SpawnGenome: проводка рождения с дрейфом + мутация метаболизма +
           пути, нажитые за жизнь, свёрнутые в гены инстинктов
```

Выученные веса потомку **не передаются** — это был бы ламаркизм. Наследуется
проводка рождения; нажитое идёт через гены инстинктов, и ребёнок отстраивает
рефлекс сам при рождении (эффект Болдуина). Девелопментальный рост для
размножения не нужен: топология пересаживается от родителя как есть.

## Сохранения

```csharp
byte[] saved = CreatureSave.Save(creature, "dioma_c");   // 255 КБ на 32.5k синапсов
File.WriteAllBytes("kit.taas", saved);

var restored = CreatureSave.Load(File.ReadAllBytes("kit.taas"), LoadBrain());
```

Формат `.taas` v2: магия `TAAS2`, JSON-манифест, именованные типизированные
секции с CRC32 на каждой. В файле — выученные веса, снимок Болдуина,
концентрации крови и геном с инстинктами. Мембранные потенциалы **не**
сохраняются: они сходятся за первые тики сами, а хранить их значило бы
привязать формат к версии модели нейрона. Топология тоже не пишется — в файле
имя коннектома и число синапсов, по которому загрузка проверяет совпадение.

## Быстрый старт

```csharp
using Dioma.Bio; using Dioma.Body; using Dioma.Spikes;

// Кернелы (мембрана + пластичность): Managed — эталон, Burst — быстрый,
// Fastest — что доступно
var brain = DiomaBrain.FromJson(File.ReadAllText(connectomeJson), KernelMode.Fastest);
var creature = new Creature(brain);           // тело создаётся само

// каждый тик мира (50 мс):
var sense = new Sense {
    SmellL = …, SmellR = …,   // градиент запаха по бортам
    WallL  = …, WallR  = …,   // только для мозгов с регионами WL/WR
    VisL   = …, VisR   = …,
    FoodNear = …,             // еда в радиусе укуса
    WallHit  = …,             // движение заблокировано (боль)
};
var action = creature.Tick(sense);
if (action.Asleep) return;    // спит — не двигать и не кормить
// action.Steer (−1..1) — от мозга; action.Drive (0..1) — от крови; action.Eat
```

Диагностика доступна напрямую: `creature.Blood.Conc[Chem.Hunger]`,
`creature.LastSpikes`, `creature.FoodEaten`, `creature.Blood.Organ.Damage`.

## Виды: тело как данные

`Creature` выше — это ОДИН вид, записанный кодом: шесть именованных каналов,
два моторных пула, три правила награды. Другому существу — с хвостом-мотором,
щупальцем, тремя сенсорами — там места нет.

`SpeciesSpec` переносит это в данные. Вид = сенсорные каналы + моторные пулы +
актуаторы + правила награды + пары рефлексов:

```csharp
var fish = SpeciesBuilder.Create("fish")
    .Sense("smell.left",  "SL", 4f, required: true)
    .Sense("smell.right", "SR", 4f, required: true)
    .Sense("food.near", null, 0f)          // канал без региона: его читает тело
    .Motor("left", "ML").Motor("right", "MR").Motor("tail", "TAIL")
    .Actuate(Actuator.Difference("steer", "left", "right", 0.25f))
    .Actuate(Actuator.Sum("drive", new[] { "tail" }, 1f / 600f))   // тяга из СПАЙКОВ
    .Actuate(Actuator.Ingest("eat", "food.near"))
    .Reward(RewardRule.ForEating(1f))
    .Reward(RewardRule.ForCollision(-0.5f))
    .Reward(RewardRule.ForApproach(0.3f, 0.02f, "smell.left", "smell.right"))
    .Reflex("SL", "TAIL").Reflex("SR", "TAIL")
    .Build();

var creature = new SpeciesCreature(fish.Bind(brain));
var action = creature.Tick(senseFrame);       // ActionFrame по каналам вида
```

Тот же вид читается из файла: `SpeciesJson.Parse(text)`. Загрузчик не
дублирует правила — он собирает тот же builder, и валидирует его
`SpeciesSpec.Validate()`.

Что даёт вид сверх старого пути:

- **любое число моторных пулов**, не два (`TickMotorPools`);
- **тяга из спайков** (`Actuator.Sum`) наравне с тягой из крови
  (`Actuator.FromBody`). Это разница видов: рыбка стоит, если хвост молчит;
  агент ползёт на моторном тонусе даже с молчащим мозгом. Первое можно
  научить, второе нет;
- **своя награда новому органу**. Без неё орган вырастет, будет спайкать, и
  STDP нечем будет его закрепить;
- **вид беднее схемы работает**: необязательный канал без региона в
  коннектоме просто не подаётся (мозг без `WL`/`WR` узнаёт о стене только
  через боль удара).

Имена каналов разрешаются в индексы один раз — при `Bind` и при создании
мира. В тике только массивы.

`SpeciesArena` — тот же мирок, что `TestArena`, но заполняет каналы по именам:
нет у вида зрения — мир его и не считает.

Что стережут тесты: `SpeciesCreature(Species.Agent())` ведёт себя **бит-в-бит**
как захардкоженный `Creature` (позиции, руль, сенсоры, веса, биохимия на 1200
тиках), вид из JSON — как вид из кода, а `Validate` ловит восемь классов
ошибок описания.

## Коннектомы

Мозг загружается из JSON, который выращивает Rust-эталон:

```
cargo run --release --bin export_connectome -- <dir> both
cargo run --release --bin export_bio        -- <dir>
```

Готовые ассеты в `Tests/Editor/Data/`:

| Файл | Мозг |
|---|---|
| `dioma_connectome.json` | `agent_layout`: 96 нейронов, 5 регионов (SL/SR/I/ML/MR) |
| `dioma_c_connectome.json` | `stage_c_lite`: 760 нейронов, 11 регионов (+стены, зрение, кора) |
| `dioma_fish_connectome.json` | `Fish`: 120 нейронов, 6 регионов (+TAIL). Растит Прометей — Rust-эталона у этого вида нет |
| `dioma_fish_species.json` | спецификация рыбки как данные |

Формат: `n`, `dt`, `stepsPerTick`, `lif`, `stdp`, `plasticity`, `regions`
(тег/границы/тормозной), `gains` (матрица гейнов анатомии), `nt` (±1),
`edges` (grown-рёбра до гейнов). Гейны — это врождённые рефлексы: SL→ML и
SR→MR = 1.0, WL→MR = WR→ML = 2.0 (отворот сильнее гоминга), кора→моторы 0.1.

## Тесты

Без Unity — одной командой:

```
scripts/verify.sh                          # паритет мозга, биохимия, обучение, арена
scripts/verify.sh parity|bio|skinner|arena
```

Харнесс `Harness~/smoke.csproj` собирает `Runtime/*` напрямую (.NET 8), так
что пакет проверяется сам, без проекта. Burst-кернелы в него не входят —
их паритет ловят EditMode-тесты.

С редактором — EditMode; путь к данным через `PackageInfo.resolvedPath`, так
что работают и для embedded-, и для `file:`-подключения пакета.

```
Unity.exe -batchmode -projectPath <proj> -runTests -testPlatform EditMode \
  -testResults results.xml -logFile tests.log
```

Полный прогон (EditMode 48, PlayMode 1, три headless-точки) —
`../dioma-unity/scripts/verify.sh`.

Проверяют: обучение (ящик Скиннера — награждаемый путь растёт, прочие стоят,
контроль «без награды веса не двигаются»), паритет мозга (240 тиков × 2 коннектома, точные моторные суммы),
паритет биохимии (600 тиков, все химии/локусы/параметры), поведение арены
(существо ест, засыпает, не бьётся о стены), контрольные точки метаболизма
(кортизол переживает боль, утомление насыщается за 60 тиков полного газа,
сон капит глюкозу ниже порога изобилия).

Эталоны в `Tests/Editor/Data` пишет `dioma/scripts/goldens.sh` — руками их
не правим, иначе паритет проверяет сам себя.
