// Пластичность на Burst: mean-field дофаминовая STDP в нативном коде.
//
// Та же двухпроходная схема, что в `ManagedPlasticityKernel` (LTP по входящим
// рёбрам, LTD в порядке CSR) — см. её шапку про асимметрию доступа к истории
// следов. Здесь к этому добавляется нативная память и SIMD в проходе следов.
//
// `FloatMode.Strict` обязателен, как и в мембранном кернеле: без него Burst
// свернул бы mul+add в FMA и переупорядочил сложения. А вот следы STDP,
// наоборот, ОБЯЗАНЫ считаться fused (в эталоне это скалярный `mul_add`
// Rust, всегда fused) — поэтому там явное умножение через double: хаос
// рекуррентной коры превратил бы ulp в другое поведение за сотню тиков.

#if DIOMA_BURST
using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Dioma.Spikes.Burst
{
    public sealed class BurstPlasticityKernel : IPlasticityKernel
    {
        public string Name => "burst";

        readonly int _n, _spt, _nSyn;
        readonly float _dETick;
        NativeArray<int> _preOf, _postOf, _inPtr, _inSyn, _spikeCount, _meanOf;
        NativeArray<long> _spikeSumS;
        NativeArray<float> _elig, _tracePre, _tracePost, _preHist, _postHist, _ltp;
        NativeArray<int> _history;
        int _historyCapacity;
        readonly PlasticityLayout _l;

        public BurstPlasticityKernel(PlasticityLayout layout)
        {
            _l = layout;
            _n = layout.N;
            _spt = layout.StepsPerTick;
            _nSyn = layout.PreOf.Length;
            _dETick = FastMath.Powi(layout.DecayElig, _spt);

            _preOf = Copy(layout.PreOf);
            _postOf = Copy(layout.PostOf);
            _inPtr = Copy(layout.InPtr);
            _inSyn = Copy(layout.InSyn);
            _elig = new NativeArray<float>(_nSyn, Allocator.Persistent);
            _ltp = new NativeArray<float>(_nSyn, Allocator.Persistent);
            _tracePre = new NativeArray<float>(_n, Allocator.Persistent);
            _tracePost = new NativeArray<float>(_n, Allocator.Persistent);
            _preHist = new NativeArray<float>(_n * _spt, Allocator.Persistent);
            _postHist = new NativeArray<float>(_n * _spt, Allocator.Persistent);
            _spikeCount = new NativeArray<int>(_n, Allocator.Persistent);
            _spikeSumS = new NativeArray<long>(_n, Allocator.Persistent);
            _meanOf = new NativeArray<int>(_n, Allocator.Persistent);
        }

        static NativeArray<T> Copy<T>(T[] src) where T : struct
        {
            var dst = new NativeArray<T>(src.Length, Allocator.Persistent);
            dst.CopyFrom(src);
            return dst;
        }

        public void NoteSpikes(int[] history, int at, int row)
        {
            EnsureHistory(history.Length);
            NativeArray<int>.Copy(history, 0, _history, 0, history.Length);
            new NoteJob
            {
                N = _n,
                At = at,
                Row = row,
                DecayPre = _l.DecayPre,
                DecayPost = _l.DecayPost,
                History = _history,
                TracePre = _tracePre,
                TracePost = _tracePost,
                PreHist = _preHist,
                PostHist = _postHist,
                SpikeCount = _spikeCount,
                SpikeSumS = _spikeSumS,
            }.Run();
        }

        void EnsureHistory(int length)
        {
            if (_historyCapacity >= length)
                return;
            if (_history.IsCreated) _history.Dispose();
            _history = new NativeArray<int>(length, Allocator.Persistent);
            _historyCapacity = length;
        }

        public void Apply() => new ApplyJob
        {
            N = _n,
            Spt = _spt,
            NSyn = _nSyn,
            DETick = _dETick,
            ALtp = _l.ALtp,
            ALtd = _l.ALtd,
            PreOf = _preOf,
            PostOf = _postOf,
            InPtr = _inPtr,
            InSyn = _inSyn,
            SpikeCount = _spikeCount,
            SpikeSumS = _spikeSumS,
            MeanOf = _meanOf,
            PreHist = _preHist,
            PostHist = _postHist,
            Ltp = _ltp,
            Elig = _elig,
        }.Run();

        public void ReadEligibility(float[] dst) => _elig.CopyTo(dst);

        public void ClearEligibility()
        {
            for (int i = 0; i < _nSyn; i++)
                _elig[i] = 0f;
        }

        public void RestrictEligibility(bool[] keepNeurons)
        {
            for (int idx = 0; idx < _nSyn; idx++)
                if (!keepNeurons[_preOf[idx]] || !keepNeurons[_postOf[idx]])
                    _elig[idx] = 0f;
        }

        public double EligibilitySum()
        {
            double s = 0;
            for (int i = 0; i < _nSyn; i++) s += _elig[i];
            return s;
        }

        public void Dispose()
        {
            if (_preOf.IsCreated) _preOf.Dispose();
            if (_postOf.IsCreated) _postOf.Dispose();
            if (_inPtr.IsCreated) _inPtr.Dispose();
            if (_inSyn.IsCreated) _inSyn.Dispose();
            if (_elig.IsCreated) _elig.Dispose();
            if (_ltp.IsCreated) _ltp.Dispose();
            if (_tracePre.IsCreated) _tracePre.Dispose();
            if (_tracePost.IsCreated) _tracePost.Dispose();
            if (_preHist.IsCreated) _preHist.Dispose();
            if (_postHist.IsCreated) _postHist.Dispose();
            if (_spikeCount.IsCreated) _spikeCount.Dispose();
            if (_spikeSumS.IsCreated) _spikeSumS.Dispose();
            if (_meanOf.IsCreated) _meanOf.Dispose();
            if (_history.IsCreated) _history.Dispose();
        }

        /// Подшаг: продвинуть зеркала следов, записать их в историю, накопить
        /// статистику спайков. Порядок операций — как в эталоне.
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High,
                      CompileSynchronously = true)]
        struct NoteJob : IJob
        {
            public int N, At, Row;
            /// Распады держим в double НАМЕРЕННО: иначе Burst сворачивает
            /// `(double)trace * floatDecay` обратно во float-умножение, и
            /// след теряет точность fused-операции эталона (замерено:
            /// расхождение eligibility 3e-5 на первом же тике).
            public double DecayPre, DecayPost;
            [ReadOnly] public NativeArray<int> History;
            public NativeArray<float> TracePre, TracePost, PreHist, PostHist;
            public NativeArray<int> SpikeCount;
            public NativeArray<long> SpikeSumS;

            public void Execute()
            {
                int outAt = Row * N;
                for (int i = 0; i < N; i++)
                {
                    int spike = History[At + i];
                    float add = spike > 0 ? 1f : 0f;
                    // Одно округление, как скалярный mul_add в Rust. Считаем
                    // через double: произведение двух float точно представимо
                    // (24+24 ≤ 53 бита), значит округление ровно одно — при
                    // сужении результата. Тот же приём, что в FastMath.
                    TracePre[i] = (float)(TracePre[i] * DecayPre + add);
                    TracePost[i] = (float)(TracePost[i] * DecayPost + add);
                    PreHist[outAt + i] = TracePre[i];
                    PostHist[outAt + i] = TracePost[i];
                    if (spike > 0)
                    {
                        SpikeCount[i] += 1;
                        SpikeSumS[i] += Row;
                    }
                }
            }
        }

        /// Конец тика: LTP по входящим рёбрам, затем затухание + LTP + LTD в
        /// порядке CSR (см. шапку файла — раскладка ради локальности чтений).
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High,
                      CompileSynchronously = true)]
        struct ApplyJob : IJob
        {
            public int N, Spt, NSyn;
            public float DETick, ALtp, ALtd;
            [ReadOnly] public NativeArray<int> PreOf, PostOf, InPtr, InSyn;
            public NativeArray<int> SpikeCount, MeanOf;
            public NativeArray<long> SpikeSumS;
            [ReadOnly] public NativeArray<float> PreHist, PostHist;
            public NativeArray<float> Ltp, Elig;

            public void Execute()
            {
                for (int i = 0; i < N; i++)
                    MeanOf[i] = MeanS(SpikeCount[i], SpikeSumS[i], Spt);

                for (int i = 0; i < NSyn; i++)
                    Ltp[i] = 0f;
                for (int post = 0; post < N; post++)
                {
                    int cpost = SpikeCount[post];
                    if (cpost == 0)
                        continue;
                    int rowAt = MeanOf[post] * N;
                    int end = InPtr[post + 1];
                    for (int k = InPtr[post]; k < end; k++)
                    {
                        int idx = InSyn[k];
                        Ltp[idx] = (float)((double)ALtp * PreHist[rowAt + PreOf[idx]] * cpost);
                    }
                }

                for (int idx = 0; idx < NSyn; idx++)
                {
                    int pre = PreOf[idx];
                    float acc = (float)((double)Elig[idx] * DETick);
                    if (SpikeCount[PostOf[idx]] > 0)
                        acc += Ltp[idx];
                    int cpre = SpikeCount[pre];
                    if (cpre > 0)
                        acc -= (float)((double)ALtd * PostHist[MeanOf[pre] * N + PostOf[idx]] * cpre);
                    Elig[idx] = acc;
                }

                for (int i = 0; i < N; i++)
                {
                    SpikeCount[i] = 0;
                    SpikeSumS[i] = 0;
                }
            }

            /// Rust `f32::round` — half away from zero, `math.round` — to even.
            static int MeanS(int count, long sum, int spt)
            {
                if (count <= 0)
                    return 0;
                float x = sum / (float)count;
                int m = (int)math.floor(x + 0.5f);
                return math.min(m, spt - 1);
            }
        }
    }
}
#endif
