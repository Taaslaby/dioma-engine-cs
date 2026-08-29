// Вычислитель мембраны на обычном C#: эталон, с которым сверяются остальные.
// Точный порт `run_lif_alpha` (pillars-connectome) — alpha-LIF Shiu et al.
// 2024 с кольцевым буфером аксонной задержки.

using System;
using System.Collections.Generic;

namespace Dioma.Spikes
{
    public sealed class ManagedSpikeKernel : ISpikeKernel
    {
        public string Name => "managed";

        readonly SpikeLayout _l;
        readonly float[] _values;
        readonly float[] _v, _g, _refrCnt;
        readonly float[] _ring;      // задержка проводки: RingSize × N
        readonly List<int> _prevFired = new List<int>();
        int _ringPos;

        public ManagedSpikeKernel(SpikeLayout layout, float[] values)
        {
            _l = layout;
            _values = (float[])values.Clone();
            _v = new float[layout.N];
            _g = new float[layout.N];
            _refrCnt = new float[layout.N];
            for (int i = 0; i < layout.N; i++)
            {
                _v[i] = layout.VRest;
                _refrCnt[i] = layout.RefrSteps; // старт вне рефрактерности
            }
            _ring = new float[layout.RingSize * layout.N];
            _ringPos = layout.RingSize - 1;
        }

        public void SyncWeights(float[] values) =>
            Array.Copy(values, _values, _values.Length);

        public void Run(float[] ext, int steps, int[] history)
        {
            int n = _l.N;
            for (int s = 0; s < steps; s++)
                Substep(ext, history, s * n);
        }

        void Substep(float[] ext, int[] history, int outOffset)
        {
            int n = _l.N;

            // 1. Внешний ток — прямо в мембрану (voltage jump, как в эталоне).
            for (int i = 0; i < n; i++)
                _v[i] += ext[i];

            // 2. Шаг нейронов: вход — слот кольца задержки; рефрактерность
            //    маскирует ТОЛЬКО вход, но не замораживает мембрану.
            int readPos = _ringPos;
            int baseIdx = readPos * n;
            for (int i = 0; i < n; i++)
            {
                float gOld = _g[i];
                float delayed = _refrCnt[i] >= _l.RefrSteps ? _ring[baseIdx + i] : 0f;

                float integrand = gOld - (_v[i] - _l.VRest);
                float vNew = _l.TimeFactor * integrand + _v[i];

                bool spike = vNew > _l.VThresh; // порог строгий: «>», не «≥»
                history[outOffset + i] = spike ? 1 : 0;
                _v[i] = spike ? _l.VReset : vNew;

                float gNew = _l.SynDecay * gOld + delayed;
                _g[i] = spike ? 0f : gNew;
                _refrCnt[i] = spike ? 0f : _refrCnt[i] + 1f;
            }

            // 3. Слот прочитан — очищаем и рассыпаем в него спайки ПРЕДЫДУЩЕГО
            //    подшага (это и есть аксонная задержка), затем двигаем кольцо.
            //
            //    Эталон пишет здесь `vals[k].mul_add(sign, slot)` — fused. Но
            //    множитель здесь ровно ±1, а умножение на ±1 точное: fused и
            //    обычное mul+add дают идентичный результат (проверено на 20 млн
            //    случайных весов — 0 расхождений). Поэтому обычное `+=` — и
            //    Burst-кернел совпадает с этим бит-в-бит без спецкоманд.
            Array.Clear(_ring, baseIdx, n);
            for (int f = 0; f < _prevFired.Count; f++)
            {
                int pre = _prevFired[f];
                float sign = _l.Nt[pre] < 0f ? -1f : 1f;
                for (int k = _l.RowPtr[pre]; k < _l.RowPtr[pre + 1]; k++)
                    _ring[baseIdx + _l.ColIdx[k]] += _values[k] * sign;
            }
            _ringPos = (readPos + 1) % _l.RingSize;

            // 4. Стрелявшие сейчас рассыплются на следующем подшаге.
            _prevFired.Clear();
            for (int i = 0; i < n; i++)
                if (history[outOffset + i] != 0)
                    _prevFired.Add(i);
        }

        public void Dispose() { }
    }
}
