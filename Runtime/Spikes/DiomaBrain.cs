// Порт ядра мозга dioma (dioma-engine::brain::dynamics::neurons + pillars
//
// Семантика — бит-в-бит с Rust-эталоном (сборка без target_feature=fma,
// поэтому все mul_add в горячем пути — ДВУХРАУНДОВЫЕ обычные mul+add):
//   - run_lif_alpha (pillars-connectome/runner.rs): кольцевой буфер задержки
//     (t_delay=1.8мс → 4 подшага, size=5), внешний ток прямо в v, вход
//     маскируется рефрактерностью, строгий порог «>», fma-порядок выражений;
//   - fast_exp (pillars/math/fast_exp.rs): скалярный не-fused путь — Horner
//     без mul_add + масштаб через биты экспоненты;
//   - mean-field STDP за тик (tick_note_spikes/tick_plasticity_apply):
//     зеркала следов непрерывны через тики, LTP — след пре на среднем
//     моменте спайков поста, LTD — наоборот, eligibility *= d_e^S;
//   - дофамин Δw = lr·D·e с клэмпом [w_min, w_max], гомеостаз normalize.

using Dioma.Data;
using System;
using System.Collections.Generic;

namespace Dioma.Spikes
{
    public sealed class DiomaBrain : IDisposable
    {
        public sealed class Region
        {
            public string Tag;
            public int Start, End;
            public bool Inhibitory;
        }

        // ── Конфиг (из JSON ассета) ──
        public readonly float Dt;
        public readonly int StepsPerTick;
        public readonly int N;
        public readonly Region[] Regions;

        readonly float _tauRef, _tauM, _vRest, _vThresh, _vReset, _tauSyn, _tDelay;
        readonly float _tauPre, _tauPost, _tauE, _aLtp, _aLtd, _lr, _wMin, _wMax, _homeoRate;
        readonly int _rewardDelayTicks, _homeoEveryTicks;
        readonly HashSet<int> _homeoExcludePre = new HashSet<int>();

        // ── CSR-коннектом (build_connectome) ──
        readonly int[] _rowPtr, _colIdx;
        readonly float[] _values;
        readonly int[] _preOf, _postOf;
        readonly bool[] _inhSyn;
        readonly float[] _targetInSum;
        readonly int[] _regionOf;
        readonly float[] _nt;

        // ── Динамика мембраны: сменный кернел (см. ISpikeKernel) ──
        readonly ISpikeKernel _kernel;
        /// История спайков тика: StepsPerTick × N (0/1). Кернел пишет, пластичность читает.
        readonly int[] _history;
        /// Имя активного вычислителя мембраны («managed», «burst»).
        public string KernelName => _kernel.Name;
        readonly SpikeLayout _layout;
        /// Топология и параметры модели — для бенчмарков и своих кернелов.
        public SpikeLayout KernelLayout() => _layout;
        readonly PlasticityLayout _plastLayout;
        /// Топология и константы STDP — для бенчмарков и своих кернелов.
        public PlasticityLayout PlastLayout => _plastLayout;

        // ── Тик-пластичность (mean-field STDP) ──
        // ── Пластичность: сменный кернел (см. IPlasticityKernel) ──
        readonly IPlasticityKernel _plast;
        readonly float[] _elig;   // буфер чтения eligibility для дофамина
        int _tickRow;
        bool _tickActive;
        /// Имя активного вычислителя пластичности.
        public string PlasticityKernelName => _plast.Name;
        long _stepCount;
        readonly float[] _normSums;
        /// Долговременный снимок весов (эталон Болдуина); null до MemorySnapshot.
        float[] _memoryLt;
        readonly List<KeyValuePair<int, float>> _pending = new List<KeyValuePair<int, float>>();

        // Константы распада (считаются один раз — значения детерминированы).
        readonly float _dPre, _dPost, _dE;

        DiomaBrain(Dictionary<string, object> root, KernelMode? kernelMode)
        {
            N = (int)Json.Num(root["n"]);
            Dt = Json.F(root["dt"]);
            StepsPerTick = (int)Json.Num(root["stepsPerTick"]);

            var lif = Json.Obj(root["lif"]);
            _tauRef = Json.F(lif["tauRef"]);
            _tauM = Json.F(lif["tauM"]);
            _vRest = Json.F(lif["vRest"]);
            _vThresh = Json.F(lif["vThresh"]);
            _vReset = Json.F(lif["vReset"]);
            _tauSyn = Json.F(lif["tauSyn"]);
            _tDelay = Json.F(lif["tDelay"]);

            var stdp = Json.Obj(root["stdp"]);
            _tauPre = Json.F(stdp["tauPre"]);
            _tauPost = Json.F(stdp["tauPost"]);
            _tauE = Json.F(stdp["tauE"]);
            _aLtp = Json.F(stdp["aLtp"]);
            _aLtd = Json.F(stdp["aLtd"]);
            _lr = Json.F(stdp["lr"]);
            _wMin = Json.F(stdp["wMin"]);
            _wMax = Json.F(stdp["wMax"]);

            var plast = Json.Obj(root["plasticity"]);
            _homeoRate = Json.F(plast["homeoRate"]);
            _rewardDelayTicks = (int)Json.Num(plast["rewardDelayTicks"]);
            _homeoEveryTicks = (int)Json.Num(plast["homeoEveryTicks"]);
            // Регионы-ПРЕ вне гомеостаза (кора stage-C); может отсутствовать.
            if (plast.TryGetValue("homeoExcludePre", out var exclObj))
                foreach (var r in Json.Arr(exclObj))
                    _homeoExcludePre.Add((int)Json.Num(r));

            // Регионы (start/end экспортированы из Grown.region_of — тот же
            // порядок, что у Brain::region_bounds).
            var regionsJson = Json.Arr(root["regions"]);
            Regions = new Region[regionsJson.Count];
            for (int i = 0; i < regionsJson.Count; i++)
            {
                var r = Json.Obj(regionsJson[i]);
                Regions[i] = new Region
                {
                    Tag = Json.Str(r["tag"]),
                    Start = (int)Json.Num(r["start"]),
                    End = (int)Json.Num(r["end"]),
                    Inhibitory = Json.Bool(r["inhibitory"]),
                };
            }

            _nt = new float[N];
            var ntJson = Json.Arr(root["nt"]);
            for (int i = 0; i < N; i++) _nt[i] = Json.F(ntJson[i]);

            // ── build_connectome: grown-рёбра × гейны, нулевой гейн выпадает ──
            var gainsJson = Json.Arr(root["gains"]);
            int nRegions = Regions.Length;
            var gains = new float[nRegions, nRegions];
            for (int a = 0; a < nRegions; a++)
            {
                var row = Json.Arr(gainsJson[a]);
                for (int b = 0; b < nRegions; b++)
                    gains[a, b] = Json.F(row[b]);
            }

            var edgesJson = Json.Arr(root["edges"]);
            var per = new List<KeyValuePair<int, float>>[N];
            for (int i = 0; i < N; i++) per[i] = new List<KeyValuePair<int, float>>();
            _regionOf = new int[N];
            for (int r = 0; r < nRegions; r++)
                for (int i = Regions[r].Start; i < Regions[r].End; i++)
                    _regionOf[i] = r;
            foreach (var e in edgesJson)
            {
                var t = Json.Arr(e);
                int pre = (int)Json.Num(t[0]), post = (int)Json.Num(t[1]);
                float w = Json.F(t[2]);
                float gain = gains[RegionOf(pre), RegionOf(post)];
                if (gain > 0.0f)
                    per[pre].Add(new KeyValuePair<int, float>(post, w * gain));
            }

            _rowPtr = new int[N + 1];
            var ci = new List<int>();
            var vals = new List<float>();
            for (int i = 0; i < N; i++)
            {
                _rowPtr[i] = vals.Count;
                foreach (var kv in per[i])
                {
                    ci.Add(kv.Key);
                    vals.Add(kv.Value);
                }
            }
            _rowPtr[N] = vals.Count;
            _colIdx = ci.ToArray();
            _values = vals.ToArray();
            int nSyn = _values.Length;

            // ── plast_and_masks ──
            _preOf = new int[nSyn];
            _postOf = new int[nSyn];
            _inhSyn = new bool[nSyn];
            for (int i = 0; i < N; i++)
                for (int idx = _rowPtr[i]; idx < _rowPtr[i + 1]; idx++)
                {
                    _preOf[idx] = i;
                    _postOf[idx] = _colIdx[idx];
                    _inhSyn[idx] = _nt[i] < 0.0f;
                }

            _targetInSum = new float[N];
            for (int idx = 0; idx < nSyn; idx++)
                if (!_inhSyn[idx] && !_homeoExcludePre.Contains(_regionOf[_preOf[idx]]))
                    _targetInSum[_postOf[idx]] += _values[idx];

            // ── alpha-LIF (Shiu et al. 2024): параметры из alpha_lif_population ──
            // Считаются здесь, чтобы любой кернел получил ровно те же числа.
            var layout = new SpikeLayout
            {
                N = N,
                RowPtr = _rowPtr,
                ColIdx = _colIdx,
                Nt = _nt,
                TimeFactor = Dt / MathF.Max(_tauM, 1e-6f),
                RefrSteps = MathF.Max(MathF.Round(_tauRef / Dt), 1.0f),
                SynDecay = 1.0f - Dt / MathF.Max(_tauSyn, 1e-6f),
                VRest = _vRest,
                VThresh = _vThresh,
                VReset = _vReset,
                RingSize = (int)MathF.Round(_tDelay / Dt) + 1,
            };
            _layout = layout;
            _kernel = SpikeKernels.Create(kernelMode ?? SpikeKernels.Default, layout, _values);
            _history = new int[StepsPerTick * N];

            // ── Пластичность: mean-field STDP в сменном кернеле ──
            // CSC-индекс входящих рёбер строится один раз: он нужен, чтобы
            // читать историю следов пре локально (см. IPlasticityKernel).
            Spikes.PlasticityLayout.BuildIncoming(N, _postOf, out var inPtr, out var inSyn);
            var plastLayout = new PlasticityLayout
            {
                N = N,
                StepsPerTick = StepsPerTick,
                RowPtr = _rowPtr,
                PreOf = _preOf,
                PostOf = _postOf,
                InPtr = inPtr,
                InSyn = inSyn,
                DecayPre = FastExp(-Dt / _tauPre),
                DecayPost = FastExp(-Dt / _tauPost),
                DecayElig = FastExp(-Dt / _tauE),
                ALtp = _aLtp,
                ALtd = _aLtd,
            };
            _plastLayout = plastLayout;
            _plast = SpikeKernels.CreatePlasticity(kernelMode ?? SpikeKernels.Default, plastLayout);
            _elig = new float[nSyn];
            _normSums = new float[N];
        }

        /// Загрузить мозг из JSON-коннектома. `kernelMode` выбирает вычислитель
        /// мембраны; по умолчанию — `SpikeKernels.Default`.
        public static DiomaBrain FromJson(string text, KernelMode? kernelMode = null) =>
            new DiomaBrain(Json.Parse(text) as Dictionary<string, object>, kernelMode);

        public int RegionOf(int neuron) => _regionOf[neuron];

        /// Индекс региона по тегу (SL/SR/WL/WR/I/ML/MR/VL/VR/…), −1 если нет.
        public int FindRegion(string tag)
        {
            for (int i = 0; i < Regions.Length; i++)
                if (Regions[i].Tag == tag)
                    return i;
            return -1;
        }

        public int SynapseCount => _values.Length;

        public long StepCount => _stepCount;

        // ── Сон и долговременная память (эталон Болдуина) ──

        /// Обнулить eligibility: след ненаграждённой пробы не должен дожить
        /// до следующей награды и обобщиться на неё.
        public void ClearEligibility() => _plast.ClearEligibility();

        /// Оставить eligibility только на синапсах, у которых И пре, И пост
        /// отмечены в маске. Нужно прожиганию рефлекса: ко-активация сенсора
        /// и мотора возбуждает и тормозные пути, и без фильтра награда
        /// усилила бы их — рефлекс глушил бы собственный драйв.
        public void RestrictEligibility(bool[] keepNeurons) =>
            _plast.RestrictEligibility(keepNeurons);

        /// Цель гомеостаза подтягивается к текущим суммам входов: выученное
        /// за день становится новой «нормой» и больше не вымывается
        /// нормализацией (`target += alpha·(sum − target)`).
        public void SleepConsolidate(float alpha)
        {
            Array.Clear(_normSums, 0, N);
            for (int idx = 0; idx < _values.Length; idx++)
                if (!_inhSyn[idx] && !_homeoExcludePre.Contains(_regionOf[_preOf[idx]]))
                    _normSums[_postOf[idx]] += _values[idx];
            for (int i = 0; i < N; i++)
                if (_normSums[i] > 1e-6f)
                    _targetInSum[i] += alpha * (_normSums[i] - _targetInSum[i]);
        }

        /// Снять долговременный снимок весов — эталон Болдуина. От него
        /// считается `LearnedGain`: «что существо нажило за жизнь». Снимок
        /// НЕ обновляется ночным сном (иначе прирост обнулялся бы каждую
        /// ночь и наследовать было бы нечего) — только при рождении и
        /// прожигании инстинктов.
        public void MemorySnapshot() => _memoryLt = (float[])_values.Clone();

        /// Восстановление к снимку: дрейф дня стирается, выученный рисунок
        /// возвращается (`w += alpha·(lt − w)`).
        public void MemoryRecall(float alpha)
        {
            if (_memoryLt == null)
                return;
            int n = Math.Min(_memoryLt.Length, _values.Length);
            for (int i = 0; i < n; i++)
                _values[i] += alpha * (_memoryLt[i] - _values[i]);
            _kernel.SyncWeights(_values);
        }

        /// Ночная консолидация: сильные выученные дельты (относительно снимка)
        /// закрепляются и слегка усиливаются, слабые — шум и дрейф гомеостаза —
        /// обрезаются к снимку. Так навык переживает ночь, а случайные
        /// колебания нет.
        public void ConsolidateSleep(float strongMin, float reinforce, float prune)
        {
            if (_memoryLt == null)
                return;
            int n = Math.Min(_memoryLt.Length, _values.Length);
            for (int i = 0; i < n; i++)
            {
                float d = _values[i] - _memoryLt[i];
                _values[i] = MathF.Abs(d) >= strongMin
                    ? _memoryLt[i] + d * (1f + reinforce)
                    : _memoryLt[i] + d * (1f - prune);
            }
            _kernel.SyncWeights(_values);
        }

        /// Сколько веса нажито за жизнь на дуге pre→post: сумма
        /// `w − snapshot` по её синапсам. Основа эффекта Болдуина —
        /// сильно выученные пути сворачиваются в инстинкты потомка.
        public float LearnedGain(int preRegion, int postRegion)
        {
            if (_memoryLt == null)
                return 0f;
            int n = Math.Min(_memoryLt.Length, _values.Length);
            float gain = 0f;
            for (int idx = 0; idx < n; idx++)
                if (_regionOf[_preOf[idx]] == preRegion && _regionOf[_postOf[idx]] == postRegion)
                    gain += _values[idx] - _memoryLt[idx];
            return gain;
        }

        /// Веса, с которыми существо родилось (снимок Болдуина). Именно они
        /// наследуются: передавать выученное напрямую было бы ламаркизмом,
        /// а нажитое за жизнь идёт потомку через гены инстинктов.
        public float[] BirthWeights() => (float[])(_memoryLt ?? _values).Clone();

        /// Среднеквадратичный дрейф весов от снимка (диагностика).
        public float MemoryDriftRms()
        {
            if (_memoryLt == null)
                return 0f;
            int n = Math.Min(_memoryLt.Length, _values.Length);
            if (n == 0)
                return 0f;
            double s = 0;
            for (int i = 0; i < n; i++)
            {
                double d = _values[i] - _memoryLt[i];
                s += d * d;
            }
            return (float)Math.Sqrt(s / n);
        }

        /// Заменить веса CSR (наследование выученного, загрузка сохранения).
        public void SetCsrValues(float[] vals)
        {
            if (vals.Length != _values.Length)
                throw new ArgumentException(
                    $"весов {vals.Length}, а синапсов {_values.Length}");
            Array.Copy(vals, _values, _values.Length);
            _kernel.SyncWeights(_values);
        }

        // ── Тик мира: StepsPerTick подшагов + моторные суммы ──
        //
        // Мембрану считает кернел, пластичность/награды/гомеостаз — мы. Тик
        // отдаётся кернелу ЦЕЛИКОМ: внутри тика веса константны (награды и
        // гомеостаз живут на границах), поэтому кернел прогоняет все подшаги
        // подряд и возвращает историю спайков, а пластичность доигрывается по
        // ней в том же порядке. Для Burst это один переход в нативный код за
        // тик вместо ста.

        public (int Ml, int Mr, long Spikes) TickMotor(float[] ext, int ml0, int ml1, int mr0, int mr1)
        {
            _kernel.Run(ext, StepsPerTick, _history);
            long total = 0;
            int ml = 0, mr = 0;
            for (int s = 0; s < StepsPerTick; s++)
            {
                int at = s * N;
                for (int i = ml0; i < ml1; i++) ml += _history[at + i];
                for (int i = mr0; i < mr1; i++) mr += _history[at + i];
                for (int i = 0; i < N; i++) total += _history[at + i];
                AfterSubstep(at);
            }
            return (ml, mr, total);
        }

        /// Пост-обработка подшага: следы STDP, счётчик, отложенные награды.
        /// `at` — смещение подшага в истории спайков.
        void AfterSubstep(int at)
        {
            TickNoteSpikes(at);
            PostSubstepTick();
        }

        // ── Тик-пластичность живёт в кернеле (mean-field STDP) ──

        void TickNoteSpikes(int at)
        {
            _plast.NoteSpikes(_history, at, _tickRow);
            _tickRow = (_tickRow + 1) % StepsPerTick;
            _tickActive = true;
        }

        void TickPlasticityApply() => _plast.Apply();

        void PostSubstepTick()
        {
            _stepCount++;

            if (_tickActive && _stepCount % StepsPerTick == 0)
                TickPlasticityApply();

            if (_homeoEveryTicks > 0 &&
                _stepCount % ((long)_homeoEveryTicks * StepsPerTick) == 0)
                Normalize();

            if (_pending.Count == 0)
                return;

            float dueSum = 0f;
            var keep = new List<KeyValuePair<int, float>>();
            foreach (var kv in _pending)
            {
                if (_stepCount >= kv.Key) dueSum += kv.Value;
                else keep.Add(kv);
            }
            _pending.Clear();
            _pending.AddRange(keep);
            if (dueSum != 0f)
            {
                ApplyDopamine(dueSum, _wMax);
                Normalize();
            }
        }

        // ── Дофамин и гомеостаз ──

        public void ScheduleReward(float dopamine) =>
            _pending.Add(new KeyValuePair<int, float>(
                (int)(_stepCount + StepsPerTick * _rewardDelayTicks), dopamine));

        public void RewardNow(float dopamine)
        {
            ApplyDopamine(dopamine, _wMax);
            Normalize();
        }

        /// Награда с явным потолком весов. Рождение прожигает рефлексы ВЫШЕ
        /// рабочего `w_max`: у выращенного коннектома хвосты весов — десятки
        /// единиц, и обычный дофамин срезал бы их при первом же награждении,
        /// ОСЛАБИВ рефлекс вместо усиления.
        public void RewardNowCapped(float dopamine, float wCap)
        {
            ApplyDopamine(dopamine, wCap);
            Normalize();
        }

        public float WMax => _wMax;

        /// Δw = lr·D·e, клэмп [w_min, wCap]; синапсы с Δw = 0 не трогаются
        /// (врождённые хвосты выше потолка сохраняются бит-в-бит).
        void ApplyDopamine(float dopamine, float wCap)
        {
            _plast.ReadEligibility(_elig);
            float lrDopa = _lr * dopamine;
            for (int i = 0; i < _values.Length; i++)
            {
                float delta = lrDopa * _elig[i];
                if (delta == 0f)
                    continue;
                float w = _values[i] + delta;
                _values[i] = Math.Clamp(w, _wMin, wCap);
            }
        }

        /// Мягкий гомеостаз: суммы входящих возбуждающих весов тянутся к
        /// стартовым (target_in_sum); тормозные пресинапсы не скейлятся.
        void Normalize()
        {
            Array.Clear(_normSums, 0, N);
            for (int idx = 0; idx < _values.Length; idx++)
                if (!_inhSyn[idx] && !_homeoExcludePre.Contains(_regionOf[_preOf[idx]]))
                    _normSums[_postOf[idx]] += _values[idx];

            for (int idx = 0; idx < _values.Length; idx++)
            {
                if (_inhSyn[idx] || _homeoExcludePre.Contains(_regionOf[_preOf[idx]]))
                    continue;
                int post = _postOf[idx];
                float target = _targetInSum[post];
                if (_normSums[post] > 1e-6f && target > 1e-6f)
                {
                    float scale = 1f + _homeoRate * (target / _normSums[post] - 1f);
                    if (float.IsFinite(scale))
                        _values[idx] *= scale;
                }
            }
            // Веса поменялись — кернел должен увидеть новые (он держит копию,
            // чтобы Burst работал на нативной памяти).
            _kernel.SyncWeights(_values);
        }

        // ── Тик без моторных сумм (холостые тики, зазоры протоколов) ──

        /// Прогнать тик, не собирая моторные суммы. Нужно для затухания
        /// eligibility между пробами: без зазора награда кредитует и пути
        /// предыдущих проб, и избирательности обучения не возникает.
        public long Tick(float[] ext)
        {
            _kernel.Run(ext, StepsPerTick, _history);
            long total = 0;
            for (int s = 0; s < StepsPerTick; s++)
            {
                int at = s * N;
                for (int i = 0; i < N; i++) total += _history[at + i];
                AfterSubstep(at);
            }
            return total;
        }

        // ── Диагностика и доступ к весам ──

        /// Копия весов CSR в каноническом порядке (сохранение существа,
        /// проверка протоколов обучения, наследование).
        public float[] CsrValues() => (float[])_values.Clone();

        /// Вес одного синапса CSR по индексу (протоколы смотрят конкретные
        /// пути; порядок совпадает с `edges` коннектома после отбора гейнами).
        public float WeightAt(int synapse) => _values[synapse];

        /// Суммарный вес рёбер между парой регионов: «насколько сильна дуга
        /// pre→post». Основная метрика обучения — усилился ли путь.
        public double RegionPairWeight(int preRegion, int postRegion)
        {
            double s = 0;
            for (int idx = 0; idx < _values.Length; idx++)
                if (_regionOf[_preOf[idx]] == preRegion && _regionOf[_postOf[idx]] == postRegion)
                    s += _values[idx];
            return s;
        }

        public double WeightSum()
        {
            double s = 0;
            foreach (float w in _values) s += w;
            return s;
        }

        public double EligibilitySum() => _plast.EligibilitySum();

        // ── Численные кернелы живут в FastMath (порты pillars) ──

        static float FastExp(float x) => FastMath.FastExp(x);

        /// Освободить нативную память кернелов (Burst держит NativeArray).
        /// Managed-кернелы ничего не делают, так что вызывать не обязательно —
        /// но в долгих сессиях со спавном существ лучше не забывать.
        public void Dispose()
        {
            _kernel.Dispose();
            _plast.Dispose();
        }
    }
}
