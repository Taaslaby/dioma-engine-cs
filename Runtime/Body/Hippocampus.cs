// Гиппокамп-модуль: универсальная память, подключаемая к любому мозгу.
// Порт karmac (taas-checkmate) — WorkingMemory (prefrontal-like state
// retention, SPAUN-inspired) + GlobalWorkspace (Baars/Dehaene).
//
// ЗАЧЕМ: PatternBox замерил (7b778ef), что mean-field STDP порядок
// стимулов не кодирует нигде — избирательность UUDD против перепутанных
// 0.97–0.99. Мозгу нужен носитель времени между стимулами; этот модуль —
// отдельный от коннектома резервуар, который копит «что было» и отдаёт
// состояние как вход. Подключение НЕ через рёбра коннектома: раздельная
// динамика, склейка на уровне тика существа.
//
// Отличия порта от Rust:
//   • thread_rng → XorShift(seed): у Rust-версии веса недетерминированы
//     (каждый new() — новая память); для наследования геномом память
//     обязана воспроизводиться — сид становится параметром.
//   • tick() без аллокаций: delta-буфер как в Rust, но projected тоже
//     переиспользуется (Rust аллоцирует vec на каждый store).
//   • Матрицы row-major как в Rust: W[j*dim + k] — строка j.

using System;
using Dioma.Spikes;

namespace Dioma.Body
{
    /// Рабочая память: слоты × dim векторов, рекуррентное удержание,
    /// гейт по норме входа, LRU-вытеснение, round-robin запись.
    ///
    /// buffer = decay·buffer + gain·W_rec·buffer (аттрактор Хопфилда
    /// в малом): след стимула живёт дольше тика — это и есть носитель
    /// времени, которого не хватало PatternBox.
    public sealed class WorkingMemory
    {
        public readonly int Slots, Dim;
        readonly float[] _buffer;       // [slots × dim] row-major
        readonly float[] _wRec;         // [dim × dim]
        readonly float[] _wIn;          // [dim × dim]
        readonly uint[] _ages;          // LRU
        int _writePtr;
        readonly float[] _projected;    // переиспользуется (без аллокаций)
        readonly float[] _delta;        // как delta_buf в Rust

        /// Затухание буфера за шаг (0.9 в Rust).
        public float Decay = 0.9f;
        /// Порог нормы входа: ниже — не пишем (шум не хранится).
        public float GateThreshold = 0.1f;
        /// Усиление рекуррентной динамики (аттракторная сила).
        public float RecurrentGain = 0.01f;

        public long StepCount { get; private set; }

        public WorkingMemory(int slots, int dim, ulong seed)
        {
            Slots = slots;
            Dim = dim;
            var rng = new XorShift(seed);
            // Xavier: как в Rust — sqrt(2/dim), равномерный ±scale.
            float scale = MathF.Sqrt(2f / dim);
            _wRec = Init(rng, dim * dim, scale);
            _wIn = Init(rng, dim * dim, scale);
            _buffer = new float[slots * dim];
            _ages = new uint[slots];
            _projected = new float[dim];
            _delta = new float[slots * dim];
        }

        static float[] Init(XorShift rng, int n, float scale)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++)
                w[i] = (float)(rng.Unit() * 2.0 * scale - scale);
            return w;
        }

        /// Записать вектор (round-robin): проекция W_in, гейт по норме,
        /// возраст остальных слотов растёт.
        public void Store(float[] input)
        {
            if (input.Length != Dim)
                throw new ArgumentException($"вход длиной {input.Length}, а dim={Dim}");
            StepCount++;

            float norm = 0f;
            for (int i = 0; i < Dim; i++) norm += input[i] * input[i];
            if (MathF.Sqrt(norm) < GateThreshold)
                return;   // гейт: тишину не храним

            for (int j = 0; j < Dim; j++)
            {
                int row = j * Dim;
                float acc = 0f;
                for (int k = 0; k < Dim; k++)
                    acc += _wIn[row + k] * input[k];
                _projected[j] = acc;
            }
            int off = _writePtr * Dim;
            Array.Copy(_projected, 0, _buffer, off, Dim);
            int written = _writePtr;
            _ages[written] = 0;   // запись обновляет возраст (Rust)
            _writePtr = (_writePtr + 1) % Slots;
            // Возраст — семантика Rust: записанный слот = 0, ВСЕ прочие +1.
            // (Моя первая версия мудрила с условием и двойным инкрементом —
            // тест round-robin поймал: слот 0 после wrap имел возраст 2.)
            for (int s = 0; s < Slots; s++)
                if (s != written)
                    _ages[s]++;
        }

        /// Считать слот (копия: буфер — внутреннее состояние).
        public float[] Recall(int slot)
        {
            if (slot < 0 || slot >= Slots)
                throw new ArgumentOutOfRangeException(nameof(slot));
            var v = new float[Dim];
            Array.Copy(_buffer, slot * Dim, v, 0, Dim);
            return v;
        }

        /// Весь буфер плоско [slots × dim] — для моста в мозг (копия).
        public float[] RecallAll()
        {
            var all = new float[Slots * Dim];
            Array.Copy(_buffer, all, all.Length);
            return all;
        }

        /// Возраст слота (тиков с последней записи; 0 = свежий).
        public int SlotAge(int slot) => (int)_ages[slot];

        /// Самый давно неиспользованный слот (LRU).
        public int LruSlot()
        {
            int best = 0;
            for (int i = 1; i < Slots; i++)
                if (_ages[i] > _ages[best]) best = i;
            return best;
        }

        /// Шаг рекуррентной динамики без входа:
        /// buffer = decay·buffer + gain·W_rec·buffer. Следы стимулов
        /// затухают медленно — «что было» переживает «что есть».
        public void Tick()
        {
            StepCount++;
            for (int i = 0; i < _buffer.Length; i++)
                _buffer[i] *= Decay;

            Array.Clear(_delta, 0, _delta.Length);
            for (int slot = 0; slot < Slots; slot++)
            {
                int off = slot * Dim;
                for (int j = 0; j < Dim; j++)
                {
                    int row = j * Dim;
                    float acc = 0f;
                    for (int k = 0; k < Dim; k++)
                        acc += _wRec[row + k] * _buffer[off + k];
                    _delta[off + j] = acc * RecurrentGain;
                }
            }
            for (int i = 0; i < _buffer.Length; i++)
                _buffer[i] += _delta[i];
        }

        /// Стереть всё (сон/рождение).
        public void Clear()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            Array.Clear(_ages, 0, _ages.Length);
            _writePtr = 0;
        }
    }

    /// Глобальное рабочее пространство (Baars/Dehaene): softmax-конкуренция
    /// за workspace + broadcast всем нейронам, residual-склейка. Для моста
    /// HC→мозг: сжать буфер памяти до ws-гецев и разослать обратно.
    public sealed class GlobalWorkspace
    {
        readonly float[] _gateW;       // [ws × n]
        readonly float[] _broadcastW;  // [n × ws]
        readonly int _n, _ws;
        readonly float[] _delta = null!;
        readonly float[] _gates = null!;

        public GlobalWorkspace(int nNeurons, int workspaceSize, ulong seed)
        {
            _n = nNeurons;
            _ws = workspaceSize;
            var rng = new XorShift(seed);
            float gateScale = MathF.Sqrt(6f / (nNeurons + workspaceSize));
            _gateW = Init(rng, nNeurons * workspaceSize, gateScale);
            // broadcast ×0.01: не доминировать над динамикой (как в Rust)
            _broadcastW = Init(rng, workspaceSize * nNeurons, gateScale * 0.01f);
            _delta = new float[nNeurons];
            _gates = new float[workspaceSize];
        }

        static float[] Init(XorShift rng, int n, float scale)
        {
            var w = new float[n];
            for (int i = 0; i < n; i++)
                w[i] = (float)(rng.Unit() * 2.0 * scale - scale);
            return w;
        }

        /// r += broadcast(softmax(gate(r))). Численно стабильный softmax
        /// (как в Rust: вычитание максимума до exp).
        public void Forward(float[] r)
        {
            if (r.Length != _n)
                throw new ArgumentException($"r длиной {r.Length}, а n={_n}");

            for (int i = 0; i < _ws; i++)
            {
                int row = i * _n;
                float acc = 0f;
                for (int k = 0; k < _n; k++)
                    acc += _gateW[row + k] * r[k];
                _gates[i] = acc;
            }

            float max = float.NegativeInfinity;
            for (int i = 0; i < _ws; i++) max = MathF.Max(max, _gates[i]);
            float sum = 0f;
            for (int i = 0; i < _ws; i++)
            {
                _gates[i] = MathF.Exp(_gates[i] - max);
                sum += _gates[i];
            }
            float inv = 1f / sum;
            for (int i = 0; i < _ws; i++) _gates[i] *= inv;

            for (int i = 0; i < _n; i++)
            {
                int row = i * _ws;
                float acc = 0f;
                for (int k = 0; k < _ws; k++)
                    acc += _broadcastW[row + k] * _gates[k];
                _delta[i] = acc;
            }
            for (int i = 0; i < _n; i++)
                r[i] += _delta[i];
        }
    }
}
