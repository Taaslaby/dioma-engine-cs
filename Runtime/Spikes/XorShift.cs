// Детерминированный ГСЧ движка: порт `dioma_engine::rng::XorShift`.
// Живёт в Spikes, потому что нужен и наследованию (мутации), и миру (спавн).
// Числа воспроизводятся один-в-один с эталоном: один сид — один результат.

namespace Dioma.Spikes
{
    public sealed class XorShift
    {
        ulong _s;

        public XorShift(ulong seed) => _s = seed;

        public ulong NextU64()
        {
            ulong x = _s;
            x ^= x << 13;
            x ^= x >> 7;
            x ^= x << 17;
            _s = x;
            return x;
        }

        /// [0, 1) — 53 значащих бита, как в эталоне.
        public double Unit() => (NextU64() >> 11) / (double)(1UL << 53);

        /// Сумма трёх равномерных ≈ гауссиана: для мутаций достаточно, зато
        /// без трансцендентных функций — воспроизводимо на любой платформе.
        public double Gauss() => Unit() + Unit() + Unit() - 1.5;

        public float Range(float lo, float hi) => lo + (hi - lo) * (float)Unit();

        /// Детерминированный хэш индекса в [0, 1) — разброс ролей по нейронам
        /// без состояния (пейсмейкерная разреженность, сенсорный шум).
        public static double Hash01(int i)
        {
            ulong x = unchecked((ulong)i * 0x9E3779B97F4A7C15UL);
            return (x >> 33) / (double)uint.MaxValue;
        }
    }
}
