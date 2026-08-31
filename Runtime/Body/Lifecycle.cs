// Жизненный цикл: возраст, старение, смерть. Порт фазы смертности из
// `sim/mod.rs` (Rust), но ОТДЕЛЬНЫМ объектом, а не внутри Creature.Tick.
//
// Почему отдельно. В Rust смертность — фаза арены, не часть тика существа.
// Так же и здесь: `Lifecycle` двигает мир/режиссёр, а `Creature.Tick`
// остаётся чистым (сенсоры→мозг→тело→действие). Это сохраняет бит-в-бит
// паритет мозга — старение не подмешивается в динамику, оно поверх неё.
//
// Две копилки урона, как в эталоне:
//   - голодная — у КРОВИ (`Blood.Organ.Damage` 0..1, за моторную работу при
//     пустой глюкозе), её ведёт сам организм;
//   - старческая — ЗДЕСЬ (`Senescence`, в очках), растёт от возраста после
//     порога старости, тает от изобилия.
// Порог смерти считает СУММУ: `Senescence + Organ.Damage × DamageScale`.

using Dioma.Bio;

namespace Dioma.Body
{
    public sealed class Lifecycle
    {
        // ── Константы (эталон sim/mod.rs при HARSHNESS = 1) ──
        public const float DamageScale = 100.0f;      // кровь 0..1 → очки
        public const float DeathDamage = 100.0f;      // порог агонии, очки
        public const int DyingTicks = 200;            // агония: время на спасение
        public const int CorpseTicks = 200;           // сколько лежит труп
        public const float RecoverGlucose = 50.0f;    // глюкоза для спасения
        public const float RescueHeal = 40.0f;        // сколько лечит спасение
        public const ulong BaseMaxAge = 72_000;       // база продолжительности жизни
        public const float SenescentRate = 0.02f;     // старческий урон/тик после порога
        public const float SenescentEndgame = 3.0f;   // ×ускорение при age ≥ maxAge
        public const int OldAgeDiv = 2;                // порог старости = maxAge / 2
        public const float StarveDamageRate = 0.25f;  // катаболизм при пустой глюкозе
        public const float HealRate = 0.02f;          // лечение старости при изобилии
        public const float HealGlucose = 200.0f;      // порог изобилия для лечения

        public ulong Age { get; private set; }
        public readonly ulong MaxAge;
        public float Senescence { get; private set; }
        public bool Dying { get; private set; }
        public int DyingLeft { get; private set; }
        public bool Dead { get; private set; }
        public int CorpseLeft { get; private set; }

        public Lifecycle(ulong maxAge) => MaxAge = maxAge;

        /// Индивидуальная продолжительность жизни: база ±30% от сида. Считается
        /// один раз при рождении. Разброс — чтобы поколение не вымирало разом.
        /// `baseMaxAge` позволяет укоротить срок под аквариум (движок по
        /// умолчанию живёт ~час мира — для наблюдения за сменой поколений долго).
        public static Lifecycle Newborn(ulong seed, ulong baseMaxAge = BaseMaxAge)
        {
            // splitmix64-скаляр 0..1 из сида, детерминированно.
            ulong z = seed + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            float unit = (z >> 11) / (float)(1UL << 53);
            ulong maxAge = (ulong)(baseMaxAge * (0.7f + 0.6f * unit));
            return new Lifecycle(maxAge);
        }

        /// Суммарный урон в очках: старческая копилка + голодная (кровь).
        public float DamagePoints(Blood blood) =>
            Senescence + blood.Organ.Damage * DamageScale;

        /// Жив: не труп и голодный урон крови не добил мозг.
        public bool Alive(Blood blood) => !Dead && blood.Organ.Damage < 1.0f;

        /// Одна фаза смертности за тик мира. `asleep` — спящий не копит урон
        /// (метаболизм стоит), но возраст всё равно идёт. Хост поднимает
        /// глюкозу сам (кормлением); агония сама не ест.
        /// Возвращает true, если существо только что стало трупом.
        public bool Advance(Blood blood, bool asleep)
        {
            if (Dead)
            {
                if (CorpseLeft > 0) CorpseLeft--;
                return false;
            }

            Age++;

            if (Dying)
            {
                // Агония. Голод лечится едой (молодой, накормленный —
                // спасён), а СТАРОСТЬ не лечится: перешагнувшего свой срок
                // едой не вернуть. Иначе успешный охотник держит глюкозу выше
                // порога спасения и живёт вечно — поколения не сменяются
                // (замерено: в арене глюкоза ~118 > RecoverGlucose = 50).
                // Это осознанное отклонение от Rust ради смены поколений.
                bool oldAge = Age >= MaxAge;
                if (!oldAge && blood.Conc[Chem.Glucose] >= RecoverGlucose)
                {
                    Rescue(blood);
                }
                else if (--DyingLeft <= 0)
                {
                    Die();
                    CorpseLeft = CorpseTicks;
                    return true;
                }
                return false;
            }

            if (asleep) return false;   // спящий стареет, но урон не копит

            // Голодное истощение: пустая глюкоза — базовый катаболизм.
            if (blood.Conc[Chem.Glucose] <= 0f)
                Senescence += StarveDamageRate;

            // Старческий урон: после половины срока, с ускорением на финише.
            if (Age > MaxAge / (ulong)OldAgeDiv)
                Senescence += SenescentRate;
            if (Age >= MaxAge)
                Senescence += SenescentRate * SenescentEndgame;

            // Изобилие лечит старческую копилку (голодную лечит сама кровь).
            if (blood.Conc[Chem.Glucose] > HealGlucose)
                Senescence = System.MathF.Max(Senescence - HealRate, 0f);

            if (DamagePoints(blood) >= DeathDamage)
                StartDying();

            return false;
        }

        public void StartDying()
        {
            if (Dying || Dead) return;
            Dying = true;
            DyingLeft = DyingTicks;
        }

        void Rescue(Blood blood)
        {
            Dying = false;
            DyingLeft = 0;
            Senescence = System.MathF.Max(Senescence - RescueHeal, 0f);
            blood.Organ.Damage = System.MathF.Max(
                blood.Organ.Damage - RescueHeal / DamageScale, 0f);
        }

        void Die()
        {
            Dead = true;
            Dying = false;
            DyingLeft = 0;
        }
    }
}
