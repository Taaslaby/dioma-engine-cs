// Численные кернелы, портированные из Rust-эталона с сохранением порядка
// округлений. Мозг с рекуррентной корой хаотичен: ulp расхождения за сотню
// тиков превращаются в другое поведение, поэтому арифметика здесь буквальная.

using System;

namespace Dioma.Spikes
{
    public static class FastMath
    {
        /// Fused multiply-add для float: `a·b + c` с ОДНИМ округлением.
        /// Эталон (`f32::mul_add` в Rust) всегда fused. `MathF.FusedMultiplyAdd`
        /// в Unity недоступен (.NET Standard 2.1), поэтому считаем в double:
        /// произведение двух float точно представимо (24+24 ≤ 53 бита), так что
        /// округление одно — при обратном сужении до float.
        public static float FusedMulAdd(float a, float b, float c) =>
            (float)((double)a * (double)b + (double)c);

        /// `fast_exp` из pillars: приведение диапазона + минимакс-полином,
        /// масштаб через биты экспоненты IEEE 754. Портирован НЕ-fused путь
        /// (эталон собран без `target_feature=fma`): Horner с двумя
        /// округлениями на шаг — иначе распады следов разойдутся с эталоном.
        public static float FastExp(float x)
        {
            const float Log2E = 1.4426950408889634f; // f32::LOG2_E
            float t = x * Log2E;
            int n = (int)MathF.Floor(t);
            float f = t - n;

            const float C1 = 0.6931471805599453f;    // f32::LN_2
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
            return BitConverter.Int32BitsToSingle((int)(biased << 23)) * poly;
        }

        /// `f32::powi` — возведение в степень квадратами от младших битов
        /// (порядок LLVM expandPowi). Критично для затухания eligibility
        /// за тик (`d_e^S`).
        public static float Powi(float b, int e)
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
    }
}
