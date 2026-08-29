// Порт ядра мозга dioma (dioma-engine::brain::dynamics::neurons + pillars
// alpha-LIF) на C#. Этап 1 «самое нужное»: CPU-путь LIF-alpha без GPU/HAAP.
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

using System;
using System.Collections.Generic;

namespace Dioma.Spikes
{
    public sealed class DiomaBrain
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

        // ── CSR-коннектом (build_connectome) ──
        readonly int[] _rowPtr, _colIdx;
        readonly float[] _values;
        readonly int[] _preOf, _postOf;
        readonly bool[] _inhSyn;
        readonly float[] _targetInSum;
        readonly float[] _nt;

        // ── Состояние динамики (run_lif_alpha) ──
        readonly float _timeFactor, _synDecay, _refrSteps;
        readonly float[] _v, _g, _refrCnt;
        readonly float[] _ring; // задержка аксонной проводки: size·n
        readonly int _ringSize;
        int _ringPos;
        readonly List<int> _prevFired = new List<int>();
        readonly int[] _spikes;

        // ── Тик-пластичность (mean-field STDP) ──
        readonly float[] _elig;
        readonly int[] _tickSpikeCount;
        readonly long[] _tickSpikeSumS;
        readonly float[] _tracePre, _tracePost, _tracePreHist, _tracePostHist;
        int _tickRow;
        bool _tickActive;
        long _stepCount;
        readonly float[] _normSums;
        readonly List<KeyValuePair<int, float>> _pending = new List<KeyValuePair<int, float>>();

        // Константы распада (считаются один раз — значения детерминированы).
        readonly float _dPre, _dPost, _dE;

        DiomaBrain(Dictionary<string, object> root)
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
                if (!_inhSyn[idx])
                    _targetInSum[_postOf[idx]] += _values[idx];

            // ── alpha-LIF (Shiu et al. 2024): параметры из alpha_lif_population ──
            _timeFactor = Dt / MathF.Max(_tauM, 1e-6f);
            _refrSteps = MathF.Max(MathF.Round(_tauRef / Dt), 1.0f);
            _synDecay = 1.0f - Dt / MathF.Max(_tauSyn, 1e-6f);

            _v = new float[N];
            _g = new float[N];
            _refrCnt = new float[N];
            for (int i = 0; i < N; i++)
            {
                _v[i] = _vRest;
                _refrCnt[i] = _refrSteps; // старт вне рефрактерности
            }

            _ringSize = (int)MathF.Round(_tDelay / Dt) + 1;
            _ring = new float[_ringSize * N];
            _ringPos = _ringSize - 1;
            _spikes = new int[N];

            // ── пластичность ──
            _elig = new float[nSyn];
            _tickSpikeCount = new int[N];
            _tickSpikeSumS = new long[N];
            _tracePre = new float[N];
            _tracePost = new float[N];
            _tracePreHist = new float[N * StepsPerTick];
            _tracePostHist = new float[N * StepsPerTick];
            _normSums = new float[N];

            _dPre = FastExp(-Dt / _tauPre);
            _dPost = FastExp(-Dt / _tauPost);
            _dE = FastExp(-Dt / _tauE);
        }

        public static DiomaBrain FromJson(string text) => new DiomaBrain(Json.Parse(text) as Dictionary<string, object>);

        public int RegionOf(int neuron)
        {
            for (int r = 0; r < Regions.Length; r++)
                if (neuron >= Regions[r].Start && neuron < Regions[r].End)
                    return r;
            throw new ArgumentException($"нейрон {neuron} вне регионов");
        }

        public int SynapseCount => _values.Length;

        public long StepCount => _stepCount;

        // ── Тик мира: StepsPerTick подшагов + моторные суммы (step_batch_motor) ──

        public (int Ml, int Mr, long Spikes) TickMotor(float[] ext, int ml0, int ml1, int mr0, int mr1)
        {
            long total = 0;
            int ml = 0, mr = 0;
            for (int s = 0; s < StepsPerTick; s++)
            {
                Substep(ext);
                for (int i = ml0; i < ml1; i++) ml += _spikes[i];
                for (int i = mr0; i < mr1; i++) mr += _spikes[i];
                for (int i = 0; i < N; i++) total += _spikes[i];
            }
            return (ml, mr, total);
        }

        /// Один подшаг динамики: точный порт run_lif_alpha.
        void Substep(float[] ext)
        {
            // 1. Внешний ток — прямо в v (voltage jump).
            for (int i = 0; i < N; i++)
                _v[i] += ext[i];

            // 2. Шаг нейронов: вход — слот кольца задержки.
            int readPos = _ringPos;
            int baseIdx = readPos * N;
            for (int i = 0; i < N; i++)
            {
                float gOld = _g[i];
                float delayed = _refrCnt[i] >= _refrSteps ? _ring[baseIdx + i] : 0f;

                float integrand = gOld - (_v[i] - _vRest);
                float vNew = _timeFactor * integrand + _v[i];

                bool spike = vNew > _vThresh; // строгий порог «>»
                _spikes[i] = spike ? 1 : 0;
                _v[i] = spike ? _vReset : vNew;

                float gNew = _synDecay * gOld + delayed;
                _g[i] = spike ? 0f : gNew;
                _refrCnt[i] = spike ? 0f : _refrCnt[i] + 1f;
            }

            // 3. Очищаем прочитанный слот, рассыпаем спайки ПРЕДЫДУЩЕГО
            //    подшага (знак торможения — в множителе), двигаем кольцо.
            Array.Clear(_ring, baseIdx, N);
            foreach (int pre in _prevFired)
            {
                float sign = _nt[pre] < 0.0f ? -1.0f : 1.0f;
                for (int k = _rowPtr[pre]; k < _rowPtr[pre + 1]; k++)
                    _ring[baseIdx + _colIdx[k]] += _values[k] * sign;
            }
            _ringPos = (readPos + 1) % _ringSize;

            // 4. Список стрелявших на этом подшаге — рассыльщик следующего.
            _prevFired.Clear();
            for (int i = 0; i < N; i++)
                if (_spikes[i] != 0)
                    _prevFired.Add(i);

            TickNoteSpikes();
            PostSubstepTick();
        }

        // ── Тик-пластичность (mean-field STDP, один проход за тик) ──

        void TickNoteSpikes()
        {
            for (int i = 0; i < N; i++)
            {
                float add = _spikes[i] > 0 ? 1.0f : 0.0f;
                _tracePre[i] = _tracePre[i] * _dPre + add;
                _tracePost[i] = _tracePost[i] * _dPost + add;
                _tracePreHist[_tickRow * N + i] = _tracePre[i];
                _tracePostHist[_tickRow * N + i] = _tracePost[i];
                if (_spikes[i] > 0)
                {
                    _tickSpikeCount[i]++;
                    _tickSpikeSumS[i] += _tickRow;
                }
            }
            _tickRow = (_tickRow + 1) % StepsPerTick;
            _tickActive = true;
        }

        void TickPlasticityApply()
        {
            int spt = StepsPerTick;
            float dETick = Powi(_dE, spt);
            int nSyn = _values.Length;

            var meanOfPre = new int[N];
            for (int i = 0; i < N; i++)
                meanOfPre[i] = MeanS(_tickSpikeCount[i], _tickSpikeSumS[i], spt);

            for (int idx = 0; idx < nSyn; idx++)
            {
                int pre = _preOf[idx], post = _postOf[idx];
                float acc = _elig[idx] * dETick;
                int cpost = _tickSpikeCount[post];
                if (cpost > 0)
                {
                    int mPost = MeanS(cpost, _tickSpikeSumS[post], spt);
                    acc += _aLtp * _tracePreHist[mPost * N + pre] * cpost;
                }
                int cpre = _tickSpikeCount[pre];
                if (cpre > 0)
                {
                    int mPre = meanOfPre[pre];
                    acc -= _aLtd * _tracePostHist[mPre * N + post] * cpre;
                }
                _elig[idx] = acc;
            }

            Array.Clear(_tickSpikeCount, 0, N);
            Array.Clear(_tickSpikeSumS, 0, N);
        }

        static int MeanS(int count, long sum, int spt)
        {
            if (count <= 0)
                return 0;
            // Rust f32::round — half away from zero; MathF.Round — to even.
            float x = sum / (float)count;
            int m = (int)MathF.Floor(x + 0.5f);
            return Math.Min(m, spt - 1);
        }

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
                ApplyDopamine(dueSum);
                Normalize();
            }
        }

        // ── Дофамин и гомеостаз ──

        public void ScheduleReward(float dopamine) =>
            _pending.Add(new KeyValuePair<int, float>(
                (int)(_stepCount + StepsPerTick * _rewardDelayTicks), dopamine));

        public void RewardNow(float dopamine)
        {
            ApplyDopamine(dopamine);
            Normalize();
        }

        /// Δw = lr·D·e, клэмп [w_min, w_max]; синапсы с Δw = 0 не трогаются.
        void ApplyDopamine(float dopamine)
        {
            float wCap = _wMax;
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
                if (!_inhSyn[idx])
                    _normSums[_postOf[idx]] += _values[idx];

            for (int idx = 0; idx < _values.Length; idx++)
            {
                if (_inhSyn[idx])
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
        }

        // ── Диагностика ──

        public double WeightSum()
        {
            double s = 0;
            foreach (float w in _values) s += w;
            return s;
        }

        public double EligibilitySum()
        {
            double s = 0;
            foreach (float e in _elig) s += e;
            return s;
        }

        // ── Численные кернелы (порты pillars, не-fused пути) ──

        /// fast_exp: range reduction + минимакс-полином, масштаб через биты
        /// экспоненты. Портируется скалярный НЕ-fused путь (сборка эталона
        /// без target_feature=fma) — Horner с двумя округлениями на шаг.
        static float FastExp(float x)
        {
            const float Log2E = 1.4426950408889634f; // f32::LOG2_E
            float t = x * Log2E;
            int n = (int)MathF.Floor(t);
            float f = t - n;

            const float C1 = 0.6931471805599453f; // f32::LN_2
            const float C2 = 0.2402265f;
            const float C3 = 0.05550411f;
            const float C4 = 0.009618129f;
            const float C5 = 0.001333356f;
            const float C6 = 0.0001540353f;

            float poly = (C6 * f + C5) * f + C4;
            poly = (poly * f + C3) * f + C2;
            poly = (poly * f + C1) * f + 1.0f;

            if (n < -126) return 0.0f;
            if (n > 127) return float.PositiveInfinity;
            uint biased = (uint)(n + 127);
            return Int32BitsToSingle((int)(biased << 23)) * poly;
        }

        /// f32::powi — возведение в степень возведением в квадрат от младших
        /// битов (порядок LLVM expandPowi), критично для d_e^S.
        static float Powi(float b, int e)
        {
            float result = 1f, cur = b;
            while (e != 0)
            {
                if ((e & 1) != 0)
                    result *= cur;
                cur *= cur;
                e >>= 1;
            }
            return result;
        }

        static float Int32BitsToSingle(int bits) =>
            BitConverter.Int32BitsToSingle(bits);
    }
}
