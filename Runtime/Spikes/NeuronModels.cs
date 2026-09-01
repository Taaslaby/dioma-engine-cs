// Зоопарк нейронов: модель мембраны как сменная деталь.
//
// Модель нейрона и РЕАЛИЗАЦИЯ (managed/Burst) — разные оси. `ISpikeKernel`
// отвечает за «где считаем», `INeuronModel` — за «что считаем». Кернел
// владеет кольцом аксонной задержки и рассылкой спайков по синапсам, модель —
// только состоянием нейрона за один подшаг.
//
// Шов выбран по замеру: на stage-C приходится 4.4 рассылки по рёбрам на один
// нейронный шаг, то есть время съедает случайный доступ к синапсам, а не
// арифметика мембраны. Значит рассылка обязана остаться в ОДНОМ месте, а
// модели меняться свободно — их вклад в цену невелик.
//
// Дисптач нулевой: модель — структура, кернел по ней параметризован
// (`ManagedSpikeKernel<TModel>`), JIT специализирует и инлайнит. Виртуального
// вызова на нейрон на подшаг (76 тысяч раз за тик у stage-C) здесь нет.

using System;

namespace Dioma.Spikes
{
    /// Известные модели мембраны. Имя приходит из коннектома полем `model`;
    /// его отсутствие значит `AlphaLif` — так читаются старые файлы.
    public enum NeuronModel
    {
        /// Alpha-LIF (Shiu et al. 2024): мембрана + альфа-синапс + строгая
        /// рефрактерность. Эталон, на нём держится паритет с Rust.
        AlphaLif,
        /// Плоский integrate-and-fire: та же мембрана без синаптической
        /// переменной — вход попадает в мембрану сразу. Дешевле на одно
        /// состояние и два умножения.
        Lif,
        /// SRM0 с экспоненциальным ядром: мембрана как след входов, спайк
        /// сбрасывает след. Математически близко к LIF, но рефрактерность
        /// задаётся ядром, а не счётчиком.
        Srm0,
        /// HAAP (VNC-стабильность): проводкостная мембрана с M-током
        /// (адаптация против runaway) и h-током (rebound, плато памяти).
        /// Единственная модель с per-neuron параметрами — читает
        /// `SpikeLayout.HaapProfile` (архетип × гены каналов).
        Haap,
    }

    /// Параметры мембраны из коннектома. Одна структура на все модели: у
    /// каждой свои используемые поля, но формат остаётся один и файл читается
    /// любой реализацией.
    public struct NeuronParams
    {
        public float Dt;
        public float TauM, TauRef, TauSyn, TDelay;
        public float VRest, VThresh, VReset;

        /// Производные величины, посчитанные один раз: любой кернел должен
        /// получить ровно те же числа, иначе паритет рассыпается.
        public float TimeFactor => Dt / MathF.Max(TauM, 1e-6f);
        public float SynDecay => 1.0f - Dt / MathF.Max(TauSyn, 1e-6f);
        public float RefrSteps => MathF.Max(MathF.Round(TauRef / Dt), 1.0f);
        public int RingSize => (int)MathF.Round(TDelay / Dt) + 1;
    }

    /// Модель мембраны: один подшаг для всех нейронов.
    ///
    /// Реализация — СТРУКТУРА, а не класс: кернел параметризуется по ней, и
    /// вызов инлайнится. Модель не знает ни о синапсах, ни о кольце задержки —
    /// ей приходит уже собранный входной ток.
    public interface INeuronModel
    {
        string Name { get; }

        /// Сколько float на нейрон нужно модели (мембрана, проводимость, …).
        int StateSlots { get; }

        /// Начальное состояние. `state` длиной `n * StateSlots`, разложение
        /// по слотам — дело модели (слот `k` нейрона `i` = `state[k * n + i]`,
        /// то есть массивы по слотам подряд: так проход по нейронам линейный).
        void Reset(int n, float[] state);

        /// Один подшаг. `ext` — внешний ток, `input` — вход, пришедший по
        /// синапсам (уже с учётом аксонной задержки), `history[at + i]` — куда
        /// писать спайк нейрона `i` (0/1).
        void Substep(int n, float[] state, float[] ext, float[] input,
                     int[] history, int at);
    }

    /// Alpha-LIF — эталон. Порт `run_lif_alpha` (pillars-connectome).
    ///
    /// Порядок операций сохранён БУКВАЛЬНО: внешний ток входит в мембрану
    /// отдельным проходом до шага нейронов, порог строгий («>», не «≥»),
    /// рефрактерность маскирует только вход и не замораживает мембрану.
    /// Любая перестановка ломает паритет 240/240 с Rust.
    public struct AlphaLifModel : INeuronModel
    {
        readonly float _timeFactor, _synDecay, _refrSteps, _vRest, _vThresh, _vReset;

        public AlphaLifModel(in NeuronParams p)
        {
            _timeFactor = p.TimeFactor;
            _synDecay = p.SynDecay;
            _refrSteps = p.RefrSteps;
            _vRest = p.VRest;
            _vThresh = p.VThresh;
            _vReset = p.VReset;
        }

        public string Name => "alpha-lif";
        public int StateSlots => 3;    // v, g, refrCnt

        public void Reset(int n, float[] state)
        {
            for (int i = 0; i < n; i++)
            {
                state[i] = _vRest;                 // v
                state[n + i] = 0f;                 // g
                state[2 * n + i] = _refrSteps;     // старт вне рефрактерности
            }
        }

        public void Substep(int n, float[] state, float[] ext, float[] input,
                            int[] history, int at)
        {
            // 1. Внешний ток — прямо в мембрану (voltage jump, как в эталоне).
            for (int i = 0; i < n; i++)
                state[i] += ext[i];

            // 2. Шаг нейронов.
            for (int i = 0; i < n; i++)
            {
                float v = state[i];
                float gOld = state[n + i];
                float refr = state[2 * n + i];
                float delayed = refr >= _refrSteps ? input[i] : 0f;

                float integrand = gOld - (v - _vRest);
                float vNew = _timeFactor * integrand + v;

                bool spike = vNew > _vThresh;
                history[at + i] = spike ? 1 : 0;
                state[i] = spike ? _vReset : vNew;

                float gNew = _synDecay * gOld + delayed;
                state[n + i] = spike ? 0f : gNew;
                state[2 * n + i] = spike ? 0f : refr + 1f;
            }
        }
    }

    /// Плоский integrate-and-fire: синаптической переменной нет, вход входит
    /// в мембрану напрямую. Дешевле alpha-LIF на одно состояние и два
    /// умножения; спайк резче — нет сглаживания альфа-синапсом.
    public struct LifModel : INeuronModel
    {
        readonly float _timeFactor, _refrSteps, _vRest, _vThresh, _vReset;

        public LifModel(in NeuronParams p)
        {
            _timeFactor = p.TimeFactor;
            _refrSteps = p.RefrSteps;
            _vRest = p.VRest;
            _vThresh = p.VThresh;
            _vReset = p.VReset;
        }

        public string Name => "lif";
        public int StateSlots => 2;    // v, refrCnt

        public void Reset(int n, float[] state)
        {
            for (int i = 0; i < n; i++)
            {
                state[i] = _vRest;
                state[n + i] = _refrSteps;
            }
        }

        public void Substep(int n, float[] state, float[] ext, float[] input,
                            int[] history, int at)
        {
            for (int i = 0; i < n; i++)
                state[i] += ext[i];

            for (int i = 0; i < n; i++)
            {
                float v = state[i];
                float refr = state[n + i];
                // Рефрактерность гасит синаптический вход, но не утечку:
                // мембрана продолжает сползать к покою.
                float drive = refr >= _refrSteps ? input[i] : 0f;

                float vNew = _timeFactor * (drive - (v - _vRest)) + v;
                bool spike = vNew > _vThresh;
                history[at + i] = spike ? 1 : 0;
                state[i] = spike ? _vReset : vNew;
                state[n + i] = spike ? 0f : refr + 1f;
            }
        }
    }

    /// SRM0 с экспоненциальным ядром: мембрана — затухающий след входов,
    /// спайк обнуляет след (это и есть «сброс» вместо явной рефрактерности).
    ///
    /// От LIF отличается тем, что утечка приложена к СЛЕДУ, а не к разности
    /// с потенциалом покоя: нейрон, переставший получать вход, забывает его
    /// экспоненциально и не «дрейфует» к порогу от собственного смещения.
    public struct Srm0Model : INeuronModel
    {
        readonly float _kernelDecay, _refrSteps, _vRest, _vThresh;

        public Srm0Model(in NeuronParams p)
        {
            // Ядро затухает по τ_m, как мембранный фильтр LIF.
            _kernelDecay = 1.0f - p.Dt / MathF.Max(p.TauM, 1e-6f);
            _refrSteps = p.RefrSteps;
            _vRest = p.VRest;
            _vThresh = p.VThresh;
        }

        public string Name => "srm0";
        public int StateSlots => 2;    // след, refrCnt

        public void Reset(int n, float[] state)
        {
            for (int i = 0; i < n; i++)
            {
                state[i] = 0f;
                state[n + i] = _refrSteps;
            }
        }

        public void Substep(int n, float[] state, float[] ext, float[] input,
                            int[] history, int at)
        {
            for (int i = 0; i < n; i++)
            {
                float trace = state[i];
                float refr = state[n + i];
                float drive = refr >= _refrSteps ? input[i] + ext[i] : ext[i];

                float traceNew = _kernelDecay * trace + drive;
                // Порог считается от покоя: след — это отклонение от него.
                bool spike = _vRest + traceNew > _vThresh;
                history[at + i] = spike ? 1 : 0;
                state[i] = spike ? 0f : traceNew;
                state[n + i] = spike ? 0f : refr + 1f;
            }
        }
    }
}
