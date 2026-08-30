// Вычислитель мембраны на обычном C#: эталон, с которым сверяются остальные.
//
// Кернел владеет тем, что общее для ВСЕХ моделей нейрона: кольцом аксонной
// задержки и рассылкой спайков по синапсам. Сама мембрана — в `INeuronModel`.
// Шов выбран по замеру: на stage-C приходится 4.4 рассылки по рёбрам на один
// нейронный шаг, то есть время съедает случайный доступ к синапсам. Рассылка
// поэтому живёт в одном месте, а модели меняются свободно.
//
// Параметризация по СТРУКТУРЕ-модели, а не по интерфейсу: JIT специализирует
// generic и инлайнит `Substep`, так что виртуального вызова на нейрон на
// подшаг (76 тысяч раз за тик у stage-C) здесь нет.

using System;
using System.Collections.Generic;

namespace Dioma.Spikes
{
    public sealed class ManagedSpikeKernel<TModel> : ISpikeKernel
        where TModel : struct, INeuronModel
    {
        public string Name => "managed:" + _model.Name;

        readonly SpikeLayout _l;
        readonly TModel _model;
        readonly float[] _values;
        readonly float[] _state;     // состояние модели: n × StateSlots
        readonly float[] _input;     // вход подшага (слот кольца)
        readonly float[] _ring;      // задержка проводки: RingSize × N
        readonly List<int> _prevFired = new List<int>();
        int _ringPos;

        public ManagedSpikeKernel(SpikeLayout layout, float[] values, TModel model)
        {
            _l = layout;
            _model = model;
            _values = (float[])values.Clone();
            _state = new float[layout.N * model.StateSlots];
            _input = new float[layout.N];
            _model.Reset(layout.N, _state);
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
            int readPos = _ringPos;
            int baseIdx = readPos * n;

            // Вход подшага — слот кольца задержки. Копируем, потому что слот
            // сразу после этого переиспользуется под спайки этого подшага.
            Array.Copy(_ring, baseIdx, _input, 0, n);

            _model.Substep(n, _state, ext, _input, history, outOffset);

            // Слот прочитан — очищаем и рассыпаем в него спайки ПРЕДЫДУЩЕГО
            // подшага (это и есть аксонная задержка), затем двигаем кольцо.
            //
            // Эталон пишет здесь `vals[k].mul_add(sign, slot)` — fused. Но
            // множитель ровно ±1, а умножение на ±1 точное: fused и обычное
            // mul+add дают идентичный результат (проверено на 20 млн случайных
            // весов — 0 расхождений). Поэтому обычное `+=` — и Burst-кернел
            // совпадает с этим бит-в-бит без спецкоманд.
            Array.Clear(_ring, baseIdx, n);
            for (int f = 0; f < _prevFired.Count; f++)
            {
                int pre = _prevFired[f];
                float sign = _l.Nt[pre] < 0f ? -1f : 1f;
                for (int k = _l.RowPtr[pre]; k < _l.RowPtr[pre + 1]; k++)
                    _ring[baseIdx + _l.ColIdx[k]] += _values[k] * sign;
            }
            _ringPos = (readPos + 1) % _l.RingSize;

            // Стрелявшие сейчас рассыплются на следующем подшаге.
            _prevFired.Clear();
            for (int i = 0; i < n; i++)
                if (history[outOffset + i] != 0)
                    _prevFired.Add(i);
        }

        public void Dispose() { }
    }

    /// Alpha-LIF на managed-кернеле: имя сохранено, потому что на него
    /// ссылаются тесты и `SpikeKernels` по умолчанию.
    public sealed class ManagedSpikeKernel : ISpikeKernel
    {
        readonly ManagedSpikeKernel<AlphaLifModel> _inner;

        public ManagedSpikeKernel(SpikeLayout layout, float[] values)
        {
            _inner = new ManagedSpikeKernel<AlphaLifModel>(
                layout, values, new AlphaLifModel(layout.Neuron));
        }

        public string Name => "managed";
        public void SyncWeights(float[] values) => _inner.SyncWeights(values);
        public void Run(float[] ext, int steps, int[] history) => _inner.Run(ext, steps, history);
        public void Dispose() => _inner.Dispose();
    }
}
