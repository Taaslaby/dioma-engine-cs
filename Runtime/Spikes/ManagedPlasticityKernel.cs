// Пластичность на обычном C#: эталон численного поведения.
//
// Порт `tick_note_spikes` / `tick_plasticity_apply` (dioma-engine, Rust) с
// одной оптимизацией раскладки памяти — двухпроходной схемой, которая НЕ
// меняет порядок арифметики (см. ниже).
//
// ── Оптимизация: два прохода вместо одного ───────────────────────────────
//
// Эталон идёт по синапсам в порядке CSR и на каждом делает два случайных
// чтения истории следов:
//
//   LTP: preHist [mPost·N + pre ]   pre фиксирован в строке, mPost скачет
//   LTD: postHist[mPre ·N + post]   mPre фиксирован в строке, post скачет
//
// История следов — 297 КБ на массив (760 × 100 × 4 Б), в L2 не влезает.
// Шаблоны доступа ПРОТИВОПОЛОЖНЫ: LTD в порядке CSR читает одну строку
// истории (3 КБ, помещается в L1), а LTP прыгает шагом 3 КБ и промахивается
// на каждом синапсе.
//
// Решение: LTP считать в порядке CSC (по входящим рёбрам поста). Тогда
// `post` фиксирован → `mPost` фиксирован → вся строка `preHist[mPost·N + ·]`
// читается последовательно.
//
// Почему это законно с точки зрения паритета: вклады LTP и LTD в eligibility
// НЕЗАВИСИМЫ друг от друга и от других синапсов — `elig[idx]` получает
// ровно `elig·d^S + ltp − ltd`. Складываем их в том же порядке, в котором
// это делает эталон (`acc = elig·d^S`, затем `+= ltp`, затем `-= ltd`),
// просто вычисляем `ltp` заранее и во втором проходе только прибавляем.
// Округления те же — тест паритета это и проверяет.

using System;

namespace Dioma.Spikes
{
    public sealed class ManagedPlasticityKernel : IPlasticityKernel
    {
        public string Name => "managed";

        readonly PlasticityLayout _l;
        readonly float[] _elig;
        readonly float[] _tracePre, _tracePost;      // зеркала следов (непрерывны)
        readonly float[] _preHist, _postHist;        // история следов: spt × N
        readonly int[] _spikeCount;
        readonly long[] _spikeSumS;
        readonly int[] _meanOf;                      // средний момент спайка нейрона
        readonly float[] _ltp;                       // предрасчёт LTP на синапс
        readonly float _dETick;

        public ManagedPlasticityKernel(PlasticityLayout layout)
        {
            _l = layout;
            int n = layout.N, spt = layout.StepsPerTick, nSyn = layout.PreOf.Length;
            _elig = new float[nSyn];
            _tracePre = new float[n];
            _tracePost = new float[n];
            _preHist = new float[n * spt];
            _postHist = new float[n * spt];
            _spikeCount = new int[n];
            _spikeSumS = new long[n];
            _meanOf = new int[n];
            _ltp = new float[nSyn];
            _dETick = FastMath.Powi(layout.DecayElig, spt);
        }

        public void NoteSpikes(int[] history, int at, int row)
        {
            int n = _l.N;
            int outAt = row * n;
            for (int i = 0; i < n; i++)
            {
                int spike = history[at + i];
                float add = spike > 0 ? 1.0f : 0.0f;
                // Эталон: `tp.mul_add(d, add)` — скалярный fused. Зеркала
                // следов обязаны совпадать бит-в-бит: ulp здесь расползается
                // хаосом рекуррентной коры в другое поведение.
                _tracePre[i] = FastMath.FusedMulAdd(_tracePre[i], _l.DecayPre, add);
                _tracePost[i] = FastMath.FusedMulAdd(_tracePost[i], _l.DecayPost, add);
                _preHist[outAt + i] = _tracePre[i];
                _postHist[outAt + i] = _tracePost[i];
                if (spike > 0)
                {
                    _spikeCount[i]++;
                    _spikeSumS[i] += row;
                }
            }
        }

        public void Apply()
        {
            int n = _l.N, spt = _l.StepsPerTick, nSyn = _l.PreOf.Length;
            for (int i = 0; i < n; i++)
                _meanOf[i] = MeanS(_spikeCount[i], _spikeSumS[i], spt);

            // ── Проход 1: LTP по входящим рёбрам (CSC). Для поста `mPost`
            //    фиксирован, поэтому строка истории пре читается локально.
            Array.Clear(_ltp, 0, nSyn);
            for (int post = 0; post < n; post++)
            {
                int cpost = _spikeCount[post];
                if (cpost == 0)
                    continue;
                int rowAt = _meanOf[post] * n;
                int end = _l.InPtr[post + 1];
                for (int k = _l.InPtr[post]; k < end; k++)
                {
                    int idx = _l.InSyn[k];
                    // Порядок множителей как в эталоне: a_ltp · trace · count.
                    // Считаем в double и сужаем один раз: так выражение имеет
                    // ОДНО определённое значение независимо от того, свернёт
                    // ли компилятор умножения в FMA. Иначе Burst-реализация
                    // (даже со FloatMode.Strict) расходится на 1e-5.
                    _ltp[idx] = (float)((double)_l.ALtp
                        * _preHist[rowAt + _l.PreOf[idx]] * cpost);
                }
            }

            // ── Проход 2: затухание + LTP + LTD в порядке CSR. LTD читает
            //    postHist[mPre·N + post] — `mPre` фиксирован внутри строки,
            //    так что это одна строка истории (3 КБ, L1).
            for (int idx = 0; idx < nSyn; idx++)
            {
                int pre = _l.PreOf[idx];
                float acc = (float)((double)_elig[idx] * _dETick);
                if (_spikeCount[_l.PostOf[idx]] > 0)
                    acc += _ltp[idx];
                int cpre = _spikeCount[pre];
                if (cpre > 0)
                    acc -= (float)((double)_l.ALtd
                        * _postHist[_meanOf[pre] * n + _l.PostOf[idx]] * cpre);
                _elig[idx] = acc;
            }

            Array.Clear(_spikeCount, 0, n);
            Array.Clear(_spikeSumS, 0, n);
        }

        /// Средний момент спайка за тик. Rust `f32::round` — half away from
        /// zero, а `MathF.Round` — to even: считаем через floor(x + 0.5).
        static int MeanS(int count, long sum, int spt)
        {
            if (count <= 0)
                return 0;
            float x = sum / (float)count;
            int m = (int)MathF.Floor(x + 0.5f);
            return Math.Min(m, spt - 1);
        }

        public void ReadEligibility(float[] dst) => Array.Copy(_elig, dst, _elig.Length);

        public void ClearEligibility() => Array.Clear(_elig, 0, _elig.Length);

        public void RestrictEligibility(bool[] keepNeurons)
        {
            for (int idx = 0; idx < _elig.Length; idx++)
                if (!keepNeurons[_l.PreOf[idx]] || !keepNeurons[_l.PostOf[idx]])
                    _elig[idx] = 0f;
        }

        public double EligibilitySum()
        {
            double s = 0;
            foreach (float e in _elig) s += e;
            return s;
        }

        public void Dispose() { }
    }
}
