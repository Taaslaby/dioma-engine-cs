# Dioma Spikes — API

Ядро спайковой симуляции dioma. Пространство имён: `Dioma.Spikes`.
Runtime-ассемблер не ссылается на UnityEngine (`noEngineReferences`) —
ядро работает и под чистым .NET.

## Модель

Каждый тик мира (50 мс) мозг делает `stepsPerTick` = 100 подшагов по
dt = 0.5 мс. Нейрон — **LIF-alpha** (Shiu et al. 2024): мембрана v,
синаптический ток g с распадом τ_syn, абсолютная рефрактерность,
аксонная задержка кольцевым буфером (t_delay = 1.8 мс → 4 подшага).
Внешний ток добавляется **прямо в мембранный потенциал** (voltage jump).

Пластичность — **mean-field дофаминовая STDP, один проход за тик**:
за тик копятся счётчики спайков и непрерывные зеркала следов (τ_pre =
τ_post = 20 мс); в конце тика по каждому синапсу eligibility получает
LTP (след пре на среднем моменте спайков поста) и LTD (след поста на
среднем моменте спайков пре) и затухает d_e^S (τ_e = 1000 мс). Дофамин
(в т.ч. отложенный) фиксирует Δw = lr·D·e с клэмпом [w_min, w_max];
после дофамина — мягкий гомеостаз: суммы входящих возбуждающих весов
тянутся к стартовым с темпом homeo_rate.

## DiomaBrain

```csharp
public static DiomaBrain FromJson(string text);
public (int Ml, int Mr, long Spikes) TickMotor(
    float[] ext, int ml0, int ml1, int mr0, int mr1);
public void ScheduleReward(float dopamine); // с задержкой rewardDelayTicks
public void RewardNow(float dopamine);      // немедленно + normalize
public double WeightSum();                  // контрольная сумма CSR
public double EligibilitySum();
public Region[] Regions;                    // Tag, Start, End, Inhibitory
public int N; public int StepsPerTick; public float Dt;
public long StepCount; public int SynapseCount;
```

- `ext[n]` — внешний ток на нейрон, **мВ на подшаг** (в отличие от
  классических токовых единиц: в Rust-оригинале стимул тоже идёт прямо
  в v). Базовый фон существа — 0.12, масштаб сенсоров — 4.0·значение.
- `TickMotor` возвращает суммы спайков за все 100 подшагов по диапазонам
  моторных регионов — это и есть «активность» моторов для руля
  (`steer = clamp((mr − ml) · 0.25, −1, 1)`).
- Веса и eligibility остаются внутри; доступ только через контрольные
  суммы (диагностика) — расширение API по мере надобности.

## GoldenRunner

Реплей сценария «золотого следа» (упрощённый контракт существа без
крови/гиппокампа: gain = 1, drive = 0.5, еда → `ScheduleReward(1.0)`,
удар о стену → `RewardNow(-0.5)`):

```csharp
var runner = GoldenRunner.Load(dataDir); // dioma_connectome.json + dioma_golden.json
foreach (var row in runner.Rows) {
    runner.Step();                       // тик C#-мозга + сверочные поля
    // row.Ml/Mr/Spikes/Wsum — наш прогон; *Ref — эталон из Rust
}
```

## SmokeRunner

```csharp
int code = SmokeRunner.Run(dataDir); // 0 = PASS, 1 = FAIL
```

Для CLI (`-executeMethod` нужна обёртка в скомпилированной сборке, см.
`Assets/Dioma/Demo/HeadlessRunner.cs` в проекте dioma-unity).

## Формат входного JSON

```jsonc
{
  "n": 96, "dt": 0.5, "stepsPerTick": 100,
  "lif":  { "tauRef":2.2, "tauM":20.0, "vRest":-52.0, "vThresh":-45.0,
            "vReset":-52.0, "tauSyn":3.0, "tDelay":1.8 },
  "stdp": { "tauPre":20.0, "tauPost":20.0, "tauE":1000.0,
            "aLtp":0.1, "aLtd":0.005, "lr":0.15, "wMin":0.0, "wMax":6.0 },
  "plasticity": { "homeoRate":0.25, "rewardDelayTicks":1, "homeoEveryTicks":0 },
  "regions": [ { "tag":"SL", "start":0, "end":20, "inhibitory":false }, … ],
  "gains":   [[…region×region…]],           // гейны анатомии
  "nt":      [1, …],                        // ±1 нейротрансмиттер нейрона
  "edges":   [[pre, post, weight], …]       // grown-рёбра ДО гейнов
}
```

Готовый ассет — `Tests/Editor/Data/dioma_connectome.json`
(`agent_layout`, 96 нейронов, 1563 grown-синапса, сид 42). Генератор —
`export_connectome.rs` в репозитории dioma (Rust):

```
cargo run --release --bin export_connectome -- <dir>
```

## Детерминизм и паритет

Кернелы портированы с сохранением порядка операций Rust-эталона (сборка
без `target_feature=fma`): `fast_exp` — не-fused Horner + масштаб через
биты экспоненты IEEE 754; `powi` — square-and-multiply от младших битов
(порядок LLVM expandPowi); все умножения горячей цепочки — двухраундовые
(не fused). Тест паритета держит **точное совпадение моторных сумм** на
всём золотом следе (240/240 тиков); контрольные суммы весов расходятся
не более ~1e-4 относительно (ulp `powi` в d_e^S).

## CLI-рецепты

```
# компиляция без окна
Unity.exe -batchmode -projectPath <proj> -quit -logFile compile.log

# тесты (EditMode)
Unity.exe -batchmode -projectPath <proj> -runTests -testPlatform EditMode \
  -testResults results.xml -logFile tests.log

# смоук
Unity.exe -batchmode -projectPath <proj> \
  -executeMethod Dioma.Demo.HeadlessRunner.Run -quit -logFile smoke.log
```
