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
    public sealed class DiomaBrain : IDisposable, IBrainDiagnostics
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
        public IBrainDiagnostics Diagnostics => this;
        public int NeuronCount => N;
        IReadOnlyList<Region> IBrainDiagnostics.Regions => Regions;

        readonly float _tauRef, _tauM, _vRest, _vThresh, _vReset, _tauSyn, _tDelay;
        readonly float _tauPre, _tauPost, _tauE, _aLtp, _aLtd, _lr, _wMin, _wMax, _homeoRate;
        readonly int _rewardDelayTicks, _homeoEveryTicks;
        readonly HashSet<int> _homeoExcludePre = new HashSet<int>();

        // ── CSR-коннектом (build_connectome) ──
        readonly int[] _rowPtr, _colIdx;
        readonly float[] _values;
        readonly int[] _preOf, _postOf;
        readonly bool[] _inhSyn;
        readonly int[] _homeoSynapses;
        readonly float[] _targetInSum;
        readonly int[] _regionOf;
        readonly float[] _nt;

        // ── Динамика мембраны: сменный кернел (см. ISpikeKernel) ──
        readonly ISpikeKernel _kernel;
        /// История спайков тика: StepsPerTick × N (0/1). Кернел пишет, пластичность читает.
        readonly byte[] _history;
        readonly IReadOnlyList<int> _historyView;
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
        /// Имя активного вычислителя пластичности.
        public string PlasticityKernelName => _plast.Name;
        long _stepCount;
        readonly float[] _normSums;
        /// Долговременный снимок весов (эталон Болдуина); null до MemorySnapshot.
        float[] _memoryLt;
        readonly List<KeyValuePair<int, float>> _pending = new List<KeyValuePair<int, float>>();
        // Отложенные D1/D2-награды: долги другого механизма — не смешивать
        // с _pending (порядок применения и семантика разные).
        readonly List<KeyValuePair<int, float>> _pendingPathed = new List<KeyValuePair<int, float>>();
        // ── D1/D2-следы (порт R-STDP из BrainCog BDM-SNN) ──
        // Тень eligibility с ДРУГИМ затуханием: прямой путь (D1) помнит
        // коротко, косвенный (D2) — дольше. Награда с выбором пути кладёт
        // дофамин в один из них: наказание растит тормозящий косвенный
        // след, а не роняет прямой (как dw_d2 = −r·trace в BDM-SNN).
        // Идёт ПОВЕРХ кернела: паритет существующего пути не тронут.
        float[] _eligD1, _eligD2;
        /// Затухание следов за тик (trace_decay из BDM-SNN, единый на
        /// оба пути). Различие путей НЕ в памяти (квадратичное затухание
        /// дало обратное: вклад D2 в любой прошлый тик 0.64^k < 0.8^k —
        /// «медленнее» получалось только в комментариях), а в ЗНАКЕ при
        /// применении: RewardPathed переворачивает D2, как dw_d2 = −r·trace.
        public const float D1Decay = 0.8f;
        public const float D2Decay = 0.8f;

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

            var homeoSynapses = new List<int>();
            for (int idx = 0; idx < nSyn; idx++)
                if (!_inhSyn[idx] && !_homeoExcludePre.Contains(_regionOf[_preOf[idx]]))
                    homeoSynapses.Add(idx);
            _homeoSynapses = homeoSynapses.ToArray();

            _targetInSum = new float[N];
            foreach (int idx in _homeoSynapses)
                _targetInSum[_postOf[idx]] += _values[idx];

            // ── Модель мембраны: alpha-LIF по умолчанию ──
            // Поля `lif` читаются всегда: формат один на все модели, у каждой
            // свои используемые параметры. Отсутствие `model` в файле значит
            // alpha-LIF — так читаются коннектомы, выпущенные до зоопарка.
            var neuron = new NeuronParams
            {
                Dt = Dt,
                TauM = _tauM,
                TauRef = _tauRef,
                TauSyn = _tauSyn,
                TDelay = _tDelay,
                VRest = _vRest,
                VThresh = _vThresh,
                VReset = _vReset,
            };
            var model = NeuronModel.AlphaLif;
            if (root.TryGetValue("model", out var modelObj) && modelObj != null)
                model = ParseModel(Json.Str(modelObj));

            // Производные величины считаются здесь, чтобы любой кернел получил
            // ровно те же числа.
            var layout = new SpikeLayout
            {
                N = N,
                RowPtr = _rowPtr,
                ColIdx = _colIdx,
                Nt = _nt,
                TimeFactor = neuron.TimeFactor,
                RefrSteps = neuron.RefrSteps,
                SynDecay = neuron.SynDecay,
                VRest = _vRest,
                VThresh = _vThresh,
                VReset = _vReset,
                RingSize = neuron.RingSize,
                Model = model,
                Neuron = neuron,
                HaapProfile = ParseHaapProfile(root, N),
            };
            _layout = layout;
            _kernel = SpikeKernels.Create(kernelMode ?? SpikeKernels.Default, layout, _values);
            _history = new byte[StepsPerTick * N];
            _historyView = new SpikeHistoryView(_history);

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

        /// Создать мозг из типизированного описания. Это путь рантайма для
        /// выращенного коннектома: без JSON-строки и её обратного разбора.
        public static DiomaBrain FromBlueprint(BrainBlueprint blueprint,
            KernelMode? kernelMode = null)
        {
            if (blueprint == null) throw new ArgumentNullException(nameof(blueprint));
            return new DiomaBrain(blueprint.ToObjectGraph(), kernelMode);
        }

        /// Модель мембраны по имени из коннектома. Неизвестное имя — ошибка, а
        /// не молчаливый откат: файл, написанный под другую динамику, дал бы
        /// другое существо, и это выглядело бы как «мозг разучился».
        static NeuronModel ParseModel(string name)
        {
            switch (name?.ToLowerInvariant())
            {
                case null:
                case "":
                case "alpha-lif":
                case "alphalif":
                case "lif-alpha":
                    return NeuronModel.AlphaLif;
                case "lif":
                case "if":
                    return NeuronModel.Lif;
                case "srm0":
                case "srm":
                    return NeuronModel.Srm0;
                case "haap":
                    return NeuronModel.Haap;
                default:
                    throw new FormatException(
                        $"неизвестная модель нейрона '{name}'; есть alpha-lif, lif, srm0, haap");
            }
        }

        /// Модель мембраны этого мозга.
        public NeuronModel Model => _layout.Model;

        /// Per-neuron профиль HAAP из коннектома: `"haapProfile": [[17 колонок
        /// на нейрон], …]`. Отсутствует → null (и тогда HAAP-модель откажет
        /// внятно — профиль обязателен, у неё нет скалярных значений).
        /// Формат зеркалит `haap_columns` Rust: строки = нейроны, колонки =
        /// HaapCol в порядке enum.
        static float[] ParseHaapProfile(Dictionary<string, object> root, int n)
        {
            if (!root.TryGetValue("haapProfile", out var profileObj) || profileObj == null)
                return null;
            var rows = Json.Arr(profileObj);
            if (rows.Count != n)
                throw new FormatException(
                    $"haapProfile: строк {rows.Count}, а нейронов {n} — профиль " +
                    $"обязан покрывать каждого (архетип региона × гены)");
            var cols = new float[n * HaapCols.N];
            for (int i = 0; i < n; i++)
            {
                var row = Json.Arr(rows[i]);
                if (row.Count != HaapCols.N)
                    throw new FormatException(
                        $"haapProfile[{i}]: колонок {row.Count}, а нужно {HaapCols.N} " +
                        $"(порядок HaapCol: dt, invCm, g_l, e_l, g_m, e_k, g_h, e_h, " +
                        $"invTauU, invTauH, vHalfU, kU, vHalfH, kH, vThresh, vReset, deltaU)");
                for (int c = 0; c < HaapCols.N; c++)
                    cols[i * HaapCols.N + c] = Json.F(row[c]);
            }
            return cols;
        }

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
            foreach (int idx in _homeoSynapses)
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
        /// возвращается (`w += alpha·(lt − w)`). Пол `w_min` — как в
        /// `ConsolidateSleep`: вес это магнитуда, отрицательный перевернул бы
        /// знак синапса.
        public void MemoryRecall(float alpha)
        {
            if (_memoryLt == null)
                return;
            int n = Math.Min(_memoryLt.Length, _values.Length);
            for (int i = 0; i < n; i++)
                _values[i] = MathF.Max(_values[i] + alpha * (_memoryLt[i] - _values[i]), _wMin);
            _kernel.SyncWeights(_values);
        }

        /// Ночная консолидация: сильные выученные дельты (относительно снимка)
        /// закрепляются и слегка усиливаются, слабые — шум и дрейф гомеостаза —
        /// обрезаются к снимку. Так навык переживает ночь, а случайные
        /// колебания нет.
        ///
        /// Пол `w_min` обязателен, и это ОТКЛОНЕНИЕ от Rust-эталона (там его
        /// нет). Вес синапса — магнитуда: знак связи несёт нейротрансмиттер
        /// пресинапса (`nt` = ±1), поэтому отрицательный вес переворачивает
        /// знак — возбуждающий синапс начинает тормозить.
        ///
        /// Без пола получается разнос: усиление сильной дельты
        /// `w = lt + d·1.15` при d &lt; 0 (вес просел ниже снимка от LTD или
        /// гомеостаза) и |d| &gt; lt/1.15 даёт w &lt; 0, а дальше каждая ночь
        /// умножает уже отрицательную дельту на 1.15 — геометрическая спираль.
        /// Замерено на обеих реализациях (совпадают бит-в-бит): за 60 ночей
        /// 148 синапсов уходят до −4.9 млн, суммарный вес мозга
        /// 180 тыс. → −20 млн, и существо в арене перестаёт есть.
        public void ConsolidateSleep(float strongMin, float reinforce, float prune)
        {
            if (_memoryLt == null)
                return;
            int n = Math.Min(_memoryLt.Length, _values.Length);
            for (int i = 0; i < n; i++)
            {
                float d = _values[i] - _memoryLt[i];
                float w = MathF.Abs(d) >= strongMin
                    ? _memoryLt[i] + d * (1f + reinforce)
                    : _memoryLt[i] + d * (1f - prune);
                // Потолок: рабочий `w_max` ИЛИ врождённый вес, если он выше
                // (у выращенного коннектома хвосты логнормали — десятки и
                // сотни, их резать нельзя). Без потолка усиление дельты на
                // 15% каждую ночь даёт ×1.15^N — за 66 ночей это ×10⁴.
                _values[i] = Math.Clamp(w, _wMin, MathF.Max(_wMax, _memoryLt[i]));
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

        /// Восстановить текущие и родовые веса persistence-снимка. Порядок
        /// важен: сначала родовые, затем снимок Болдуина, потом текущие.
        public void RestoreWeights(float[] current, float[] birth)
        {
            if (birth == null) throw new ArgumentNullException(nameof(birth));
            SetCsrValues(birth);
            MemorySnapshot();
            SetCsrValues(current);
        }

        // ── Тик мира: StepsPerTick подшагов + моторные суммы ──
        //
        // Мембрану считает кернел, пластичность/награды/гомеостаз — мы. Тик
        // отдаётся кернелу ЦЕЛИКОМ: внутри тика веса константны (награды и
        // гомеостаз живут на границах), поэтому кернел прогоняет все подшаги
        // подряд и возвращает историю спайков, а пластичность доигрывается по
        // ней в том же порядке. Для Burst это один переход в нативный код за
        // тик вместо ста.

        // ── Визуализация: чтение состояния без влияния на динамику ──

        /// Пороги мембраны для оси графика: покой, порог, сброс.
        public (float VRest, float VThresh, float VReset) MembraneRange =>
            (_vRest, _vThresh, _vReset);

        /// «Напряжение» всех нейронов после последнего тика — мембрана LIF,
        /// след SRM0. Заполняет `into` длиной N; на динамику не влияет.
        public void ReadMembrane(float[] into) => _kernel.ReadMembrane(into);

        /// История спайков последнего тика: `StepsPerTick × N`, элемент 0/1,
        /// подшаг `s` нейрон `i` → `[s * N + i]`. Внутренний буфер — читать
        /// сразу после `Tick`/`TickMotor`, не мутировать.
        public IReadOnlyList<int> SpikeHistory => _historyView;

        /// Суммарный дофамин за последний тик: положительный — награда,
        /// отрицательный — наказание. Для растра подкрепления.
        public float LastDopamine { get; private set; }

        public (int Ml, int Mr, long Spikes) TickMotor(float[] ext, int ml0, int ml1, int mr0, int mr1)
        {
            LastDopamine = 0f;
            _kernel.Run(ext, StepsPerTick, _history);
            ProcessDueRewards(_stepCount); // delay=0: до пластичности нового тика
            int ml = 0, mr = 0;
            for (int s = 0; s < StepsPerTick; s++)
            {
                int at = s * N;
                for (int i = ml0; i < ml1; i++) ml += _history[at + i];
                for (int i = mr0; i < mr1; i++) mr += _history[at + i];
            }
            long total = FinishTick();
            return (ml, mr, total);
        }

        /// Тот же тик на ПРОИЗВОЛЬНОЕ число моторных пулов: `starts[i]..ends[i)`
        /// суммируется в `sums[i]`. Двухпуловая перегрузка выше — частный
        /// случай, и она остаётся нетронутой: на ней держится паритет 240/240.
        ///
        /// Пулов может быть сколько угодно, потому что кернел и так возвращает
        /// историю спайков целиком (`StepsPerTick × N`) — суммы считаются по
        /// уже посчитанным данным, лишней работы нет.
        public long TickMotorPools(float[] ext, int[] starts, int[] ends, int[] sums)
        {
            if (starts == null || ends == null || sums == null)
                throw new ArgumentNullException(nameof(starts));
            if (starts.Length != ends.Length || starts.Length != sums.Length)
                throw new ArgumentException(
                    $"длины не совпадают: starts={starts.Length}, ends={ends.Length}, sums={sums.Length}");

            LastDopamine = 0f;
            _kernel.Run(ext, StepsPerTick, _history);
            ProcessDueRewards(_stepCount); // delay=0
            Array.Clear(sums, 0, sums.Length);
            int np = starts.Length;
            for (int s = 0; s < StepsPerTick; s++)
            {
                int at = s * N;
                for (int p = 0; p < np; p++)
                {
                    int acc = sums[p];
                    for (int i = starts[p]; i < ends[p]; i++) acc += _history[at + i];
                    sums[p] = acc;
                }
            }
            return FinishTick();
        }

        /// Пластичность и bookkeeping на границе тика. Публичный API отдаёт
        /// мембранному кернелу сразу весь тик, поэтому между подшагами никто
        /// не может вклиниться и повторять эти проверки 100 раз не нужно.
        int FinishTick()
        {
            int total = _plast.ProcessTick(_history);
            _stepCount += StepsPerTick;
            UpdatePathTraces();

            if (_homeoEveryTicks > 0 &&
                _stepCount % ((long)_homeoEveryTicks * StepsPerTick) == 0)
                Normalize();

            ProcessDueRewards(_stepCount);
            return total;
        }

        void ProcessDueRewards(long step)
        {
            if (_pendingPathed.Count > 0)
            {
                float duePathed = DrainDue(_pendingPathed, step);
                if (duePathed != 0f)
                    RewardPathed(duePathed);
            }

            if (_pending.Count == 0)
                return;

            float dueSum = DrainDue(_pending, step);
            if (dueSum != 0f)
                ApplyDopamine(dueSum, _wMax);
        }

        /// Собрать созревшие награды и уплотнить список на месте. Порядок
        /// суммирования и оставшихся элементов совпадает с прежним foreach.
        static float DrainDue(List<KeyValuePair<int, float>> pending, long step)
        {
            float due = 0f;
            int write = 0;
            for (int read = 0; read < pending.Count; read++)
            {
                var item = pending[read];
                if (step >= item.Key)
                    due += item.Value;
                else
                    pending[write++] = item;
            }
            if (write < pending.Count)
                pending.RemoveRange(write, pending.Count - write);
            return due;
        }

        // ── Дофамин и гомеостаз ──

        public void ScheduleReward(float dopamine) =>
            _pending.Add(new KeyValuePair<int, float>(
                (int)(_stepCount + StepsPerTick * _rewardDelayTicks), dopamine));

        /// Отложенная награда через D1/D2-следы (см. RewardPathed).
        /// Память следов ленива: SchedulePathed включает их — правило
        /// вида не должно падать от порядка вызовов.
        public void SchedulePathed(float dopamine)
        {
            EnablePathTraces();
            _pendingPathed.Add(new KeyValuePair<int, float>(
                (int)(_stepCount + StepsPerTick * _rewardDelayTicks), dopamine));
        }

        public void RewardNow(float dopamine)
        {
            ApplyDopamine(dopamine, _wMax);
        }

        // ── D1/D2-пути (порт R-STDP из BrainCog BDM-SNN) ────────────
        //
        // В BDM-SNN выбор действия решают БАЗАЛЬНЫЕ ГАНГЛИИ двумя дорожками:
        // прямая (DLPFC→StrD1→…) включает действие, косвенная (→StrD2→…)
        // — ТОРМОЗИТ. Обучение — R-STDP: два затухающих следа копятся от
        // спайков, а награда домножает ИХ: dw_d1 = +r·trace_d1,
        // dw_d2 = −r·trace_d2. Отрицательная награда усиливает КОСВЕННЫЙ
        // путь: наказание строит торможение, а не роняет возбуждение.
        //
        // У нас нет ганглиев, но есть то же семейство механизмов: след
        // eligibility уже считается кернелом. D1/D2 — ТЕНЬ его поверх
        // (косинусные затухания), поэтому старый путь бит-в-бит не тронут.

        /// Включить D1/D2-следы (лениво: память только если вызвали).
        public void EnablePathTraces()
        {
            if (_eligD1 != null) return;
            _eligD1 = new float[SynapseCount];
            _eligD2 = new float[SynapseCount];
        }

        /// Тени следов для диагностики (NeuroMonitor/HUD): копия вовне,
        /// null если пути не включены. Индексация — как у CSR-весов.
        public (float[] D1, float[] D2) ReadPathTraces()
        {
            if (_eligD1 == null) return (null, null);
            return ((float[])_eligD1.Clone(), (float[])_eligD2.Clone());
        }

        /// Обновление теней eligibility на границе тика. Оба следа получают
        /// ОДИН прирост (trace_decay BDM-SNN един), но разной природы:
        ///   D1 — полный знакопеременный eligibility кернела (LTP+LTD):
        ///         награда усиливает то, что реально двигало спайки;
        ///   D2 — только LTP-составляющая (max(e,0)): как в BDM-SNN след
        ///         маскируется в положительную область (их clamp 0..None на
        ///         кортико-стриарных путях) — знак приносит ТОЛЬКО r.
        /// Без знакоустойчивости D2 наказание через −trace даёт хаос:
        /// eligibility знакопеременен, и «строить НЕ-действие» вырождалось
        /// в случайные броски весов (замер: 0 расходящихся рёбер).
        void UpdatePathTraces()
        {
            if (_eligD1 == null) return;
            _plast.UpdatePathTraces(_eligD1, _eligD2, D1Decay, D2Decay);
        }

        /// Награда с выбором ПУТИ (R-STDP BDM-SNN):
        ///   positive → домножает D1-след (полный, знакопеременный): как
        ///     RewardNow, но с собственным окном затухания;
        ///   negative → домножает D2-след (знакоустойчивый) и ЗНАК r
        ///     разворачивает его: рёбра, чья активность привела к
        ///     наказанию, падают, но МЕХАНИЗМОМ следа — окно косвенного
        ///     пути консервативнее безличного LTD кернела.
        public void RewardPathed(float dopamine)
        {
            if (_eligD1 == null)
                throw new InvalidOperationException(
                    "RewardPathed требует EnablePathTraces(): тени D1/D2 ленивы");
            LastDopamine += dopamine;
            float lrDopa = _lr * dopamine;
            Array.Clear(_normSums, 0, N);
            int homeoAt = 0;
            for (int i = 0; i < _values.Length; i++)
            {
                float trace = dopamine >= 0f ? _eligD1[i] : _eligD2[i];
                float delta = lrDopa * trace;
                if (delta != 0f)
                {
                    float w = _values[i] + delta;
                    _values[i] = Math.Clamp(w, _wMin, _wMax);
                }
                if (homeoAt < _homeoSynapses.Length && _homeoSynapses[homeoAt] == i)
                {
                    _normSums[_postOf[i]] += _values[i];
                    homeoAt++;
                }
            }
            ApplyNormalization();
        }

        /// Награда с явным потолком весов. Рождение прожигает рефлексы ВЫШЕ
        /// рабочего `w_max`: у выращенного коннектома хвосты весов — десятки
        /// единиц, и обычный дофамин срезал бы их при первом же награждении,
        /// ОСЛАБИВ рефлекс вместо усиления.
        public void RewardNowCapped(float dopamine, float wCap)
        {
            ApplyDopamine(dopamine, wCap);
        }

        public float WMax => _wMax;

        /// Δw = lr·D·e, клэмп [w_min, wCap]; синапсы с Δw = 0 не трогаются
        /// (врождённые хвосты выше потолка сохраняются бит-в-бит).
        void ApplyDopamine(float dopamine, float wCap)
        {
            LastDopamine += dopamine;   // сумма за тик для растра подкрепления
            _plast.ReadEligibility(_elig);
            float lrDopa = _lr * dopamine;
            Array.Clear(_normSums, 0, N);
            int homeoAt = 0;
            for (int i = 0; i < _values.Length; i++)
            {
                float delta = lrDopa * _elig[i];
                if (delta != 0f)
                {
                    float w = _values[i] + delta;
                    _values[i] = Math.Clamp(w, _wMin, wCap);
                }
                if (homeoAt < _homeoSynapses.Length && _homeoSynapses[homeoAt] == i)
                {
                    _normSums[_postOf[i]] += _values[i];
                    homeoAt++;
                }
            }
            ApplyNormalization();
        }

        /// Мягкий гомеостаз: суммы входящих возбуждающих весов тянутся к
        /// стартовым (target_in_sum); тормозные пресинапсы не скейлятся.
        void Normalize()
        {
            Array.Clear(_normSums, 0, N);
            foreach (int idx in _homeoSynapses)
                _normSums[_postOf[idx]] += _values[idx];

            ApplyNormalization();
        }

        void ApplyNormalization()
        {
            foreach (int idx in _homeoSynapses)
            {
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
            LastDopamine = 0f;
            _kernel.Run(ext, StepsPerTick, _history);
            ProcessDueRewards(_stepCount); // delay=0
            return FinishTick();
        }

        // ── Диагностика и доступ к весам ──

        /// Копия весов CSR в каноническом порядке (сохранение существа,
        /// проверка протоколов обучения, наследование).
        public float[] CsrValues() => (float[])_values.Clone();
        public float[] WeightsSnapshot() => CsrValues();

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
