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
        readonly float[][] _ring;    // задержка проводки: отдельный массив на слот
        int[] _prevFired, _currentFired;
        int _prevFiredCount;
        int _ringPos;

        public ManagedSpikeKernel(SpikeLayout layout, float[] values, TModel model)
        {
            _l = layout;
            _model = model;
            // Managed читает тот же массив, которым владеет DiomaBrain.
            // Native/Burst-кернел по-прежнему синхронизирует свою копию.
            _values = values;
            _state = new float[layout.N * model.StateSlots];
            _model.Reset(layout.N, _state);
            _ring = new float[layout.RingSize][];
            for (int i = 0; i < _ring.Length; i++)
                _ring[i] = new float[layout.N];
            _prevFired = new int[layout.N];
            _currentFired = new int[layout.N];
            _ringPos = layout.RingSize - 1;
        }

        public void SyncWeights(float[] values)
        {
            // Production-путь передаёт ту же ссылку, поэтому копировать
            // десятки тысяч весов после каждой награды не нужно. Внешние
            // пользователи интерфейса всё ещё могут синхронизировать другой
            // массив — сохраняем этот контракт без лишней цены в hot path.
            if (!ReferenceEquals(values, _values))
                Array.Copy(values, _values, _values.Length);
        }

        /// Первый слот состояния — «напряжение» (мембрана LIF, след SRM0).
        /// Разложение состояния: слот 0 занимает первые N элементов.
        public void ReadMembrane(float[] into) =>
            Array.Copy(_state, 0, into, 0, _l.N);

        public void Run(float[] ext, int steps, byte[] history)
        {
            int n = _l.N;
            for (int s = 0; s < steps; s++)
                Substep(ext, history, s * n);
        }

        void Substep(float[] ext, byte[] history, int outOffset)
        {
            int n = _l.N;
            int readPos = _ringPos;
            float[] input = _ring[readPos];

            // Модель полностью дочитывает слот до его переиспользования,
            // поэтому передаём массив напрямую без полного копирования N.
            int currentFiredCount = _model.Substep(
                n, _state, ext, input, history, outOffset, _currentFired);

            // Слот прочитан — очищаем и рассыпаем в него спайки ПРЕДЫДУЩЕГО
            // подшага (это и есть аксонная задержка), затем двигаем кольцо.
            //
            // Эталон пишет здесь `vals[k].mul_add(sign, slot)` — fused. Но
            // множитель ровно ±1, а умножение на ±1 точное: fused и обычное
            // mul+add дают идентичный результат (проверено на 20 млн случайных
            // весов — 0 расхождений). Поэтому обычное `+=` — и Burst-кернел
            // совпадает с этим бит-в-бит без спецкоманд.
            Array.Clear(input, 0, n);
            for (int f = 0; f < _prevFiredCount; f++)
            {
                int pre = _prevFired[f];
                float sign = _l.Nt[pre] < 0f ? -1f : 1f;
                for (int k = _l.RowPtr[pre]; k < _l.RowPtr[pre + 1]; k++)
                    input[_l.ColIdx[k]] += _values[k] * sign;
            }
            _ringPos = (readPos + 1) % _l.RingSize;

            // Стрелявшие сейчас рассыплются на следующем подшаге. Модель уже
            // собрала их индексы во время обязательного нейронного прохода.
            int[] swap = _prevFired;
            _prevFired = _currentFired;
            _currentFired = swap;
            _prevFiredCount = currentFiredCount;
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
        public void ReadMembrane(float[] into) => _inner.ReadMembrane(into);
        public void Run(float[] ext, int steps, byte[] history) => _inner.Run(ext, steps, history);
        public void Dispose() => _inner.Dispose();
    }
}
