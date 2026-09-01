// HAAP — Hyperpolarization-Activated Adaptive Pulse-bursting: проводкостная
// модель мембраны с настоящей биофизикой. Порт `pillars::zoo::neurons::haap`
// числа-в-числа (Forward Euler, Больцман-гейты, спайк-бамп delta_u).
//
// Три омических тока (закон Ома, I = g·(V − E)):
//   I_leak = g_L·(V − E_L)          — утечка
//   I_M    = g_M·u·(V − E_K)        — M-ток: адаптация, гасит burst'ы
//   I_h    = g_h·h·(V − E_h)        — h-ток: rebound, плато (память)
//   C_m·dV/dt = I_syn − I_leak − I_M − I_h
//
// Гейты — стандартный Больцман (k — фактор склона, ДЕЛИТЕЛЬ):
//   u_∞ = 1/(1+exp(−(V−V½u)/k_u))  открывается при деполяризации
//   h_∞ = 1/(1+exp(+(V−V½h)/k_h))  открывается при гиперполяризации
// Спайк: V ≥ V_thresh → V = V_reset, u += delta_u (адаптация копится).
//
// Ключевая разница с LIF: у HAAP нет счётчика рефрактерности — частоту
// держит M-ток, накапливающийся с каждым спайком. Поэтому HAAP-мозг
// устойчив к runaway (VNC-баланс) и умеет ритмические burst'ы.
//
// Параметры per-neuron (17 колонок): архетип региона × гены экспрессии
// каналов задают каждому нейрону свою физиологию. Это единственная модель
// зоопарка с неоднородными нейронами — остальные читают скалярный
// `NeuronParams` на всех.

using System;

namespace Dioma.Spikes
{
    /// Колонки параметров HAAP (порядок = HAAPModelParamIdx в Rust).
    public enum HaapCol
    {
        Dt = 0,
        InvCm = 1,
        GL = 2, EL = 3,
        GM = 4, EK = 5,
        GH = 6, EH = 7,
        InvTauU = 8, InvTauH = 9,
        VHalfU = 10, KU = 11,
        VHalfH = 12, KH = 13,
        VThresh = 14, VReset = 15,
        DeltaU = 16,
    }

    public static class HaapCols
    {
        public const int N = 17;

        /// VNC-профиль из тестов Rust (make_vnc_params): моторный баланс
        /// адаптации. Это базовые ЗНАЧЕНИЯ колонок до архетипа и генов.
        public static void VncInto(float[] row, float dt)
        {
            row[(int)HaapCol.Dt] = dt;
            row[(int)HaapCol.InvCm] = 1f;
            row[(int)HaapCol.GL] = 0.1f;
            row[(int)HaapCol.EL] = -65f;
            row[(int)HaapCol.GM] = 0.5f;
            row[(int)HaapCol.EK] = -80f;
            row[(int)HaapCol.GH] = 0.3f;
            row[(int)HaapCol.EH] = -30f;
            row[(int)HaapCol.InvTauU] = 1f / 100f;
            row[(int)HaapCol.InvTauH] = 1f / 200f;
            row[(int)HaapCol.VHalfU] = -35f;
            row[(int)HaapCol.KU] = 5f;
            row[(int)HaapCol.VHalfH] = -75f;
            row[(int)HaapCol.KH] = 6f;
            row[(int)HaapCol.VThresh] = -40f;
            row[(int)HaapCol.VReset] = -55f;
            row[(int)HaapCol.DeltaU] = 0.02f;
        }

        /// Больцман-гейт в стационаре при v (для инициализации в равновесие,
        /// как NEURON INITIAL). u: открывается при деполяризации.
        public static float UInf(float v, float vHalf, float k) =>
            1f / (1f + MathF.Exp(-(v - vHalf) / k));

        /// h: открывается при гиперполяризации.
        public static float HInf(float v, float vHalf, float k) =>
            1f / (1f + MathF.Exp((v - vHalf) / k));
    }

    /// HAAP-модель мембраны для ManagedSpikeKernel. Параметры per-neuron
    /// приходят лэйаутом (`SpikeLayout.HaapProfile`, n × HaapCols.N):
    /// в C# нет макроса, разворачивающего слоты, поэтому модель читает
    /// плоский массив колонками — та же раскладка, что в Rust-раннере.
    public struct HaapModel : INeuronModel
    {
        readonly float[] _cols;    // n × 17, слот k нейрона i = _cols[k*n+i]
        readonly int _n;

        public HaapModel(SpikeLayout layout)
        {
            _cols = layout.HaapProfile;
            _n = layout.N;
            if (_cols == null || _cols.Length < _n * HaapCols.N)
                throw new ArgumentNullException(
                    "haap-модель требует per-neuron профиль (SpikeLayout.HaapProfile): " +
                    "архетип региона × гены каналов. Коннектом без «haapProfile» " +
                    "не может считаться этой моделью");
        }

        public string Name => "haap";
        /// Состояния: v, u, h (порядок как в Rust-раннере).
        public int StateSlots => 3;

        public void Reset(int n, float[] state)
        {
            // Равновесие покоя: мембрана в v, где токи суммируются в ноль, а
            // гейты — в стационарах при этом v (NEURON INITIAL). Иначе
            // стартовый транзиент сглаживал бы первые тики.
            for (int i = 0; i < n; i++)
            {
                int b = i * HaapCols.N;
                float v = RestingState(_cols, b, out float u, out float h);
                state[i] = v;
                state[n + i] = u;
                state[2 * n + i] = h;
            }
        }

        public void Substep(int n, float[] state, float[] ext, float[] input,
                            int[] history, int at)
        {
            // Индексация колонок ОБРАТНАЯ состояниям: слоты состояния лежат
            // подряд (v[0..n), u[n..2n), h[2n..3n)), а параметры — по
            // нейронам (row i при b = i*17). Так и в Rust: states — слоты,
            // params — по-нейронно.
            for (int i = 0; i < n; i++)
            {
                int b = i * HaapCols.N;
                float dt = _cols[b + (int)HaapCol.Dt];
                float invCm = _cols[b + (int)HaapCol.InvCm];
                float v = state[i], u = state[n + i], h = state[2 * n + i];

                // ── Омические токи ──
                float iLeak = _cols[b + (int)HaapCol.GL] *
                              (v - _cols[b + (int)HaapCol.EL]);
                float iM = _cols[b + (int)HaapCol.GM] * u *
                           (v - _cols[b + (int)HaapCol.EK]);
                float iH = _cols[b + (int)HaapCol.GH] * h *
                           (v - _cols[b + (int)HaapCol.EH]);
                float dv = (ext[i] + input[i] - iLeak - iM - iH) * invCm;
                float vNew = v + dv * dt;

                // ── Кинетика гейтов (Forward Euler, Больцман) ──
                // Аргумент экспоненты клэмпится в ±50 — страховка от
                // патологических V (вне биофизики), не участвует в норме.
                float uArg = Math.Clamp(
                    -(vNew - _cols[b + (int)HaapCol.VHalfU]) / _cols[b + (int)HaapCol.KU],
                    -50f, 50f);
                float uInf = 1f / (1f + MathF.Exp(uArg));
                float uNew = Math.Clamp(
                    u + dt * (uInf - u) * _cols[b + (int)HaapCol.InvTauU], 0f, 1f);

                float hArg = Math.Clamp(
                    (vNew - _cols[b + (int)HaapCol.VHalfH]) / _cols[b + (int)HaapCol.KH],
                    -50f, 50f);
                float hInf = 1f / (1f + MathF.Exp(hArg));
                float hNew = Math.Clamp(
                    h + dt * (hInf - h) * _cols[b + (int)HaapCol.InvTauH], 0f, 1f);

                // ── Спайк и сброс ──
                bool spike = vNew >= _cols[b + (int)HaapCol.VThresh];
                history[at + i] = spike ? 1 : 0;
                state[i] = spike ? _cols[b + (int)HaapCol.VReset] : vNew;
                state[n + i] = spike
                    ? Math.Clamp(uNew + _cols[b + (int)HaapCol.DeltaU], 0f, 1f)
                    : uNew;
                state[2 * n + i] = hNew;
            }
        }

        /// Равновесие покоя нейрона с колонками `cols` от смещения `b`:
        /// решаем g_l(V−e_l) + g_m·u∞(V)(V−e_k) + g_h·h∞(V)(V−e_h) = 0
        /// бисекцией по [−120, −20] мВ, гейты в стационаре. Порт
        /// `biology::physiology::resting_state` (60 итераций, как в Rust).
        public static float RestingState(float[] cols, int b,
            out float u0, out float h0)
        {
            float gl = cols[b + (int)HaapCol.GL], el = cols[b + (int)HaapCol.EL];
            float gm = cols[b + (int)HaapCol.GM], ek = cols[b + (int)HaapCol.EK];
            float gh = cols[b + (int)HaapCol.GH], eh = cols[b + (int)HaapCol.EH];
            float vhu = cols[b + (int)HaapCol.VHalfU], ku = cols[b + (int)HaapCol.KU];
            float vhh = cols[b + (int)HaapCol.VHalfH], kh = cols[b + (int)HaapCol.KH];

            float F(float v) =>
                gl * (v - el) + gm * HaapCols.UInf(v, vhu, ku) * (v - ek)
                              + gh * HaapCols.HInf(v, vhh, kh) * (v - eh);

            float lo = -120f, hi = -20f;
            for (int it = 0; it < 60; it++)
            {
                float mid = 0.5f * (lo + hi);
                if (F(mid) < 0f) lo = mid;
                else hi = mid;
            }
            float v = 0.5f * (lo + hi);
            u0 = HaapCols.UInf(v, vhu, ku);
            h0 = HaapCols.HInf(v, vhh, kh);
            return v;
        }
    }
}
