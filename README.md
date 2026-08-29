# Dioma Spikes

Ядро спайковой симуляции dioma для Unity. Порт CPU-пути `dioma-engine`
(Rust) на C#: коннектом из grown-рёбер → **LIF-alpha** (Shiu et al. 2024,
аксонная задержка кольцевым буфером) → **mean-field дофаминовая STDP**
за тик → мягкий гомеостаз весов.

Паритет с Rust-эталоном поддерживается тестом «золотого следа»:
`export_connectome.rs` (в репозитории dioma) растит коннектом, гоняет
240 тиков сценария и экспортирует JSON; тест в пакете реплеит тот же
сценарий на C# и сверяет моторные суммы и контрольные суммы весов.

## Использование

```csharp
using Dioma.Spikes;

var brain = DiomaBrain.FromJson(File.ReadAllText(connectomeJsonPath));

// тик мира (50 мс): 100 подшагов LIF, моторные суммы регионов ML/MR
var (ml, mr, totalSpikes) = brain.TickMotor(ext, ml0, ml1, mr0, mr1);

// подкрепление: дофамин с задержкой (dt=0.5 мс, 100 подшагов/тик)
brain.ScheduleReward(1.0f);   // за еду
brain.RewardNow(-0.5f);       // наказание (удар о стену) немедленно
```

`ext` — внешний ток на нейрон (в мВ на подшаг: ток добавляется прямо
в мембранный потенциал, как в Rust-оригинале). Сенсорные токи размазываются
по диапазонам регионов (`brain.Regions[i].Start/End`).

## Формат входного JSON

`n`, `dt`, `stepsPerTick`, `lif` (tauRef/tauM/vRest/vThresh/vReset/tauSyn/tDelay),
`stdp` (tauPre/tauPost/tauE/aLtp/aLtd/lr/wMin/wMax), `plasticity`
(homeoRate/rewardDelayTicks/homeoEveryTicks), `regions` (tag/start/end/inhibitory),
`gains` (матрица гейнов анатомии region×region), `nt` (±1 на нейрон),
`edges` ([[pre, post, weight], …] — grown-рёбра до гейнов).

Готовый ассет — `Tests/Editor/Data/dioma_connectome.json`
(`agent_layout`, 96 нейронов, сид 42).

## Детерминизм

Численные кернелы портированы с сохранением порядка операций Rust-эталона
(сборка без FMA): `fast_exp` — не-fused Horner + масштаб через биты
экспоненты; `powi` — square-and-multiply от младших битов. Тест паритета
держит **точное совпадение моторных сумм** на всём золотом следе; суммы
весов расходятся не более ~1e-4 относительно (ulp `powi`).

## Тесты

EditMode, запуск через Test Runner или CLI:

```
Unity.exe -batchmode -projectPath <proj> -runTests -testPlatform EditMode \
  -testResults results.xml -logFile tests.log
```
