// Вычислитель мембраны на Burst. Тот же алгоритм, что в
// `ManagedSpikeKernel`, но в нативном коде и на нативной памяти.
//
// Почему это безопасно для детерминизма. Burst по умолчанию волен
// переупорядочивать float-операции и сворачивать mul+add в FMA, что сдвинуло
// бы результат на ulp — а рекуррентная кора хаотична, и через сотню тиков
// ulp превращается в другое поведение. Поэтому job помечен
// `FloatMode.Strict`: порядок операций сохраняется, FMA не подставляется,
// числа совпадают с managed-кернелом бит-в-бит (это проверяет тест паритета).
//
// Что здесь НЕ распараллеливается и почему: рассылка спайков по CSR — это
// scatter со коллизиями (два пресинапса пишут в один пост), а порядок
// сложений влияет на результат. Параллелить её значило бы потерять
// детерминизм — а он в этом проекте важнее ещё одного ускорения. Выигрыш
// даёт сам нативный код и SIMD в шаге нейронов.

#if DIOMA_BURST
using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Dioma.Spikes.Burst
{
    public sealed class BurstSpikeKernel : ISpikeKernel
    {
        public string Name => "burst";

        readonly int _n, _ringSize;
        NativeArray<int> _rowPtr, _colIdx, _history, _prevFired;
        NativeArray<float> _values, _nt, _v, _g, _refrCnt, _ring, _ext;
        NativeArray<int> _prevFiredCount;
        readonly SpikeLayout _l;
        int _historyCapacity;

        public BurstSpikeKernel(SpikeLayout layout, float[] values)
        {
            _l = layout;
            _n = layout.N;
            _ringSize = layout.RingSize;

            _rowPtr = Copy(layout.RowPtr);
            _colIdx = Copy(layout.ColIdx);
            _nt = Copy(layout.Nt);
            _values = Copy(values);
            _v = new NativeArray<float>(_n, Allocator.Persistent);
            _g = new NativeArray<float>(_n, Allocator.Persistent);
            _refrCnt = new NativeArray<float>(_n, Allocator.Persistent);
            _ring = new NativeArray<float>(_ringSize * _n, Allocator.Persistent);
            _ext = new NativeArray<float>(_n, Allocator.Persistent);
            // Список стрелявших + его длина: живёт между подшагами, потому что
            // рассылка идёт с задержкой на один подшаг (аксонная задержка).
            _prevFired = new NativeArray<int>(_n, Allocator.Persistent);
            _prevFiredCount = new NativeArray<int>(1, Allocator.Persistent);

            for (int i = 0; i < _n; i++)
            {
                _v[i] = layout.VRest;
                _refrCnt[i] = layout.RefrSteps; // старт вне рефрактерности
            }
            // Кольцо читается с позиции RingSize−1 (как в эталоне).
            _ringPos = _ringSize - 1;
        }

        int _ringPos;

        static NativeArray<T> Copy<T>(T[] src) where T : struct
        {
            var dst = new NativeArray<T>(src.Length, Allocator.Persistent);
            dst.CopyFrom(src);
            return dst;
        }

        public void SyncWeights(float[] values) => _values.CopyFrom(values);

        public void Run(float[] ext, int steps, int[] history)
        {
            int needed = steps * _n;
            if (_historyCapacity < needed)
            {
                if (_history.IsCreated) _history.Dispose();
                _history = new NativeArray<int>(needed, Allocator.Persistent);
                _historyCapacity = needed;
            }
            _ext.CopyFrom(ext);

            var job = new SubstepJob
            {
                N = _n,
                RingSize = _ringSize,
                RingPos = _ringPos,
                Steps = steps,
                TimeFactor = _l.TimeFactor,
                SynDecay = _l.SynDecay,
                RefrSteps = _l.RefrSteps,
                VRest = _l.VRest,
                VThresh = _l.VThresh,
                VReset = _l.VReset,
                RowPtr = _rowPtr,
                ColIdx = _colIdx,
                Values = _values,
                Nt = _nt,
                Ext = _ext,
                V = _v,
                G = _g,
                RefrCnt = _refrCnt,
                Ring = _ring,
                History = _history,
                PrevFired = _prevFired,
                PrevFiredCount = _prevFiredCount,
            };
            job.Run();
            _ringPos = (_ringPos + steps) % _ringSize;
            NativeArray<int>.Copy(_history, 0, history, 0, needed);
        }

        public void Dispose()
        {
            if (_rowPtr.IsCreated) _rowPtr.Dispose();
            if (_colIdx.IsCreated) _colIdx.Dispose();
            if (_values.IsCreated) _values.Dispose();
            if (_nt.IsCreated) _nt.Dispose();
            if (_v.IsCreated) _v.Dispose();
            if (_g.IsCreated) _g.Dispose();
            if (_refrCnt.IsCreated) _refrCnt.Dispose();
            if (_ring.IsCreated) _ring.Dispose();
            if (_history.IsCreated) _history.Dispose();
            if (_ext.IsCreated) _ext.Dispose();
            if (_prevFired.IsCreated) _prevFired.Dispose();
            if (_prevFiredCount.IsCreated) _prevFiredCount.Dispose();
        }

        /// Подшаг alpha-LIF. `FloatMode.Strict` обязателен: без него Burst
        /// подставит FMA и переупорядочит сложения, и числа разойдутся с
        /// managed-кернелом (а через сотню тиков — и поведение).
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High,
                      CompileSynchronously = true)]
        struct SubstepJob : IJob
        {
            public int N, RingSize, RingPos, Steps;
            public float TimeFactor, SynDecay, RefrSteps, VRest, VThresh, VReset;

            [ReadOnly] public NativeArray<int> RowPtr;
            [ReadOnly] public NativeArray<int> ColIdx;
            [ReadOnly] public NativeArray<float> Values;
            [ReadOnly] public NativeArray<float> Nt;
            [ReadOnly] public NativeArray<float> Ext;

            public NativeArray<float> V, G, RefrCnt, Ring;
            public NativeArray<int> History, PrevFired, PrevFiredCount;

            public void Execute()
            {
                int ringPos = RingPos;
                for (int step = 0; step < Steps; step++)
                {
                    // 1. Внешний ток — прямо в мембрану.
                    for (int i = 0; i < N; i++)
                        V[i] += Ext[i];

                    // 2. Шаг нейронов: вход из слота задержки; рефрактерность
                    //    маскирует только вход.
                    int at = step * N;
                    int baseIdx = ringPos * N;
                    for (int i = 0; i < N; i++)
                    {
                        float gOld = G[i];
                        float delayed = RefrCnt[i] >= RefrSteps ? Ring[baseIdx + i] : 0f;

                        float integrand = gOld - (V[i] - VRest);
                        float vNew = TimeFactor * integrand + V[i];

                        bool spike = vNew > VThresh; // порог строгий
                        History[at + i] = spike ? 1 : 0;
                        V[i] = spike ? VReset : vNew;

                        float gNew = SynDecay * gOld + delayed;
                        G[i] = spike ? 0f : gNew;
                        RefrCnt[i] = spike ? 0f : RefrCnt[i] + 1f;
                    }

                    // 3. Слот прочитан — очищаем и рассыпаем спайки
                    //    ПРЕДЫДУЩЕГО подшага (аксонная задержка). Порядок
                    //    обхода строго по возрастанию pre и k: он определяет
                    //    порядок сложений в scatter'е.
                    for (int i = 0; i < N; i++)
                        Ring[baseIdx + i] = 0f;
                    int fired = PrevFiredCount[0];
                    for (int f = 0; f < fired; f++)
                    {
                        int pre = PrevFired[f];
                        float sign = Nt[pre] < 0f ? -1f : 1f;
                        int end = RowPtr[pre + 1];
                        for (int k = RowPtr[pre]; k < end; k++)
                            Ring[baseIdx + ColIdx[k]] += Values[k] * sign;
                    }
                    ringPos = (ringPos + 1) % RingSize;

                    // 4. Стрелявшие сейчас рассыплются на следующем подшаге.
                    int count = 0;
                    for (int i = 0; i < N; i++)
                        if (History[at + i] != 0)
                            PrevFired[count++] = i;
                    PrevFiredCount[0] = count;
                }
            }
        }
    }
}
#endif
