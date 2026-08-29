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
| `Dioma.Spikes` | `DiomaBrain` — LIF-alpha, mean-field STDP, гомеостаз, сон, память Болдуина; `ISpikeKernel` — сменный вычислитель мембраны; `SkinnerBox` | Data |
| `Dioma.Spikes.Burst` | `BurstSpikeKernel` — та же динамика в нативном коде (мембрана ×1.7) | Spikes, Burst, Collections |
| `Dioma.Bio` | `Blood` — реакции, рецепторы, эмиттеры, орган «мозг» | нет |
| `Dioma.Body` | `Creature` — мост сенсоры→мозг→тело→действие; `Genome`/`Instinct` — наследственность; `CreatureSave` | Spikes, Bio, Data |
| `Dioma.World` | `TestArena` — эталонный мирок для тестов и смоука | Body |

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

// Кернел мембраны: Managed (эталон), Burst (быстрый), Fastest (что есть)
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

Формат: `n`, `dt`, `stepsPerTick`, `lif`, `stdp`, `plasticity`, `regions`
(тег/границы/тормозной), `gains` (матрица гейнов анатомии), `nt` (±1),
`edges` (grown-рёбра до гейнов). Гейны — это врождённые рефлексы: SL→ML и
SR→MR = 1.0, WL→MR = WR→ML = 2.0 (отворот сильнее гоминга), кора→моторы 0.1.

## Тесты

EditMode; путь к данным через `PackageInfo.resolvedPath`, так что работают и
для embedded-, и для `file:`-подключения пакета.

```
Unity.exe -batchmode -projectPath <proj> -runTests -testPlatform EditMode \
  -testResults results.xml -logFile tests.log
```

Проверяют: обучение (ящик Скиннера — награждаемый путь растёт, прочие стоят,
контроль «без награды веса не двигаются»), паритет мозга (240 тиков × 2 коннектома, точные моторные суммы),
паритет биохимии (600 тиков, все химии/локусы/параметры), поведение арены
(существо ест, засыпает, не бьётся о стены), контрольные точки метаболизма
(кортизол переживает боль, утомление насыщается за 60 тиков полного газа,
сон капит глюкозу ниже порога изобилия).

Быстрая итерация без редактора — dotnet-харнесс `tools/csharpsmoke` в
проекте dioma-unity (`dotnet run -c Release -- parity|bio|arena`).
