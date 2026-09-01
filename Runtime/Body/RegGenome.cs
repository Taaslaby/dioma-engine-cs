// Регуляторный геном: гены меняют СИЛУ существующих механизмов — пластичность,
// инстинкты, перестройку, сытость — но не создают структуру. Порт
// `brain::genes::regulatory` (Rust) числа-в-числа: та же арифметика,
// тот же порядок вызовов ГСЧ, тот же эпистаз.
//
// Онтогенез бесплатно: окно on_tick..off_tick делает ген «юношеским»
// (быстро учиться, пока молод) или «взрослым» (консерватизм с возрастом).
// Несколько активных генов объединяются УМНОЖЕНИЕМ (эпистаз): два гена 1.4
// дают 1.96, а не среднее — сильные аллели усиливают друг друга нелинейно.
//
// Дикий тип полностью нейтрален (всё = 1), поэтому наследование без
// регуляторных генов не меняет НИ ОДНОГО числа поведения — паритет
// старых сценариев не шевелится.

using System;
using System.Collections.Generic;
using Dioma.Spikes;

namespace Dioma.Body
{
    /// Что модулирует регуляторный ген.
    public enum RegTarget
    {
        /// Множитель lr пластичности региона (0..2, 1 = нейтрально).
        Plasticity = 0,
        /// Множитель силы инстинктов при прожиге (0..2).
        Instinct = 1,
        /// Множитель числа переносов тропической перестройки за вызов (0..2).
        Rewire = 2,
        /// Порог сытости: во сколько раз дольше сытость держится (0..2).
        Satiety = 3,
    }

    public static class RegTargets
    {
        public static readonly RegTarget[] All =
        {
            RegTarget.Plasticity, RegTarget.Instinct,
            RegTarget.Rewire, RegTarget.Satiety,
        };

        /// Число системных слотов (сериализация и кроссовер).
        public const int N = 4;

        public static int Idx(this RegTarget t) => (int)t;
    }

    /// Один регуляторный ген: цель + регион + сила + доминантность.
    ///
    /// `Region` = `SystemRegion` (все регионы, системный модулятор).
    /// `OnTick`/`OffTick` — окно экспрессии по возрасту (в тиках мира):
    /// ген активен только когда `age >= OnTick && age < OffTick`.
    public struct RegGene : IEquatable<RegGene>
    {
        /// Региона «на все регионы». В Rust это usize::MAX.
        public const int SystemRegion = int.MaxValue;

        public RegTarget Target;
        /// Регион действия; SystemRegion — системный.
        public int Region;
        /// Множитель эффекта (1.0 = нейтрально). Клэмпится в [0..2].
        public float Strength;
        /// true — доминантный аллель: в кроссовере перекрывает рецессивного.
        public bool Dominant;
        /// Тик мира, с которого ген выражен.
        public int OnTick;
        /// Тик, с которого ген гаснет; Never = бессрочно.
        public int OffTick;

        /// «Никогда не гаснет». В Rust — usize::MAX.
        public const int Never = int.MaxValue;

        /// Нейтральный ген цели.
        public static RegGene Of(RegTarget target) => new RegGene
        {
            Target = target,
            Region = SystemRegion,
            Strength = 1f,
            Dominant = false,
            OnTick = 0,
            OffTick = Never,
        };

        /// Региональный ген: активен всегда, действует на один регион.
        /// Строит «сырой» ген с нейтральными шапкой и окном — для тестов
        /// и ручной сборки, где важна только пара (target, region, strength).
        public static RegGene Regional(RegTarget target, int region, float strength) =>
            new RegGene
            {
                Target = target,
                Region = region,
                Strength = strength,
                Dominant = false,
                OnTick = 0,
                OffTick = Never,
            };

        /// Юношеский ген: активен первые `duration` тиков жизни.
        public static RegGene Juvenile(RegTarget target, float strength, int duration) =>
            new RegGene
            {
                Target = target,
                Strength = strength,
                OffTick = Math.Max(duration, 1),
                Region = SystemRegion,
                OnTick = 0,
            };

        /// Взрослый ген: включается после `onset` тиков и остаётся навсегда.
        public static RegGene Adult(RegTarget target, float strength, int onset) => new RegGene
        {
            Target = target,
            Strength = strength,
            OnTick = onset,
            Region = SystemRegion,
            OffTick = Never,
        };

        /// Активен ли ген в данном возрасте.
        public bool ActiveAt(int age) => age >= OnTick && age < OffTick;

        /// Мутация: дрейф силы, изредка флип доминантности, цели и окна.
        /// Порядок вызовов ГСЧ сохранён из Rust — тот же сид даёт ту же
        /// мутацию.
        public RegGene Mutate(XorShift rng, float sigma)
        {
            var g = this;
            g.Strength = Math.Clamp(g.Strength + (float)rng.Gauss() * sigma, 0f, 2f);
            if (rng.Unit() < 0.05)
                g.Dominant = !g.Dominant;
            if (rng.Unit() < 0.02)
            {
                // Редкий сдвиг цели: ген начинает регулировать другое.
                var all = RegTargets.All;
                g.Target = all[(int)(rng.NextU64() % (ulong)all.Length)];
            }
            if (rng.Unit() < 0.04)
            {
                // Дрейф окна экспрессии: онтогенетический тайминг сам
                // эволюционирует. Бессрочный офсет не двигается.
                float shift = (float)rng.Gauss() * sigma * 2000f;
                int onset = Math.Max(0, (int)(OnTick + shift));
                int offset = OffTick == Never
                    ? Never
                    : (int)MathF.Max(OffTick + shift, onset + 100f);
                g.OnTick = onset;
                g.OffTick = offset;
            }
            return g;
        }

        public bool Equals(RegGene other) =>
            Target == other.Target && Region == other.Region &&
            Strength == other.Strength && Dominant == other.Dominant &&
            OnTick == other.OnTick && OffTick == other.OffTick;

        public override bool Equals(object obj) => obj is RegGene g && Equals(g);
        public override int GetHashCode() =>
            unchecked((int)Target * 397 ^ Region ^ Strength.GetHashCode());
    }

    /// Регуляторный геном: фиксированные системные слоты (по одному на
    /// цель) + опциональные per-region уточнения. Детерминирован по сиду.
    public sealed class RegGenome
    {
        /// Системные гены (region = SystemRegion), ровно по одному на цель.
        public readonly RegGene[] Systemic = new RegGene[RegTargets.N];
        /// Per-region уточнения (пустой = только системные).
        public readonly List<RegGene> Regional = new List<RegGene>();

        RegGenome() { }

        /// Дикий тип: всё нейтрально.
        public static RegGenome Wildtype()
        {
            var g = new RegGenome();
            for (int i = 0; i < RegTargets.N; i++)
                g.Systemic[i] = RegGene.Of(RegTargets.All[i]);
            return g;
        }

        /// Итоговый множитель цели в регионе на данном возрасте: база(=1) ×
        /// системный × региональный, только активные в этом возрасте гены.
        /// Эпистаз: умножение, не усреднение.
        public float ExpressionAt(RegTarget target, int region, int age)
        {
            float m = 1f;
            var sys = Systemic[target.Idx()];
            if (sys.ActiveAt(age))
                m *= sys.Strength;
            for (int i = 0; i < Regional.Count; i++)
            {
                var g = Regional[i];
                if (g.Target == target &&
                    (g.Region == region || g.Region == RegGene.SystemRegion) &&
                    g.ActiveAt(age))
                    m *= g.Strength;
            }
            return Math.Clamp(m, 0f, 4f);
        }

        /// Множитель без учёта возраста (возраст = 0, все окна открыты).
        public float Expression(RegTarget target, int region) =>
            ExpressionAt(target, region, 0);

        /// Мутация всего генома. Порядок ГСЧ как в Rust.
        public RegGenome Mutate(XorShift rng, float sigma)
        {
            var g = Wildtype();
            for (int i = 0; i < RegTargets.N; i++)
                g.Systemic[i] = Systemic[i].Mutate(rng, sigma);
            for (int i = 0; i < Regional.Count; i++)
                g.Regional.Add(Regional[i].Mutate(rng, sigma));
            // Редкая дупликация: системный ген получает региональную копию
            // (субфункционализация — копия дивергирует мутацией).
            if (rng.Unit() < 0.03 && g.Regional.Count < 16)
            {
                var src = Systemic[(int)(rng.NextU64() % (ulong)RegTargets.N)];
                var copy = src;
                copy.Region = (int)(rng.NextU64() % 8UL);
                copy.Strength =
                    Math.Clamp(copy.Strength + (float)rng.Gauss() * sigma, 0f, 2f);
                g.Regional.Add(copy);
            }
            return g;
        }

        /// Кроссовер с доминантностью: на каждый слот побеждает доминантный
        /// аллель; при равной доминантности — тай-брейк по сиду (не по
        /// чётности таргета — иначе чётные цели всегда наследовались бы от
        /// одного родителя по всей популяции).
        public RegGenome Crossover(RegGenome other, ulong seed)
        {
            var child = Wildtype();
            for (int i = 0; i < RegTargets.N; i++)
            {
                var a = Systemic[i];
                var b = other.Systemic[i];
                child.Systemic[i] =
                    a.Dominant != b.Dominant ? (a.Dominant ? a : b)
                    : ((seed >> (i % 32)) & 1) == 0 ? a : b;
            }
            child.Regional.Clear();
            MergeRegional(child.Regional, Regional, other.Regional, seed);
            return child;
        }

        /// Региональные: объединение списков, конфликты по (target, region)
        /// решает доминантность, тай-брейк — сид по региону.
        static void MergeRegional(List<RegGene> into, List<RegGene> a, List<RegGene> b,
            ulong seed)
        {
            into.AddRange(a);
            for (int j = 0; j < b.Count; j++)
            {
                var gene = b[j];
                int at = -1;
                for (int i = 0; i < into.Count; i++)
                    if (into[i].Target == gene.Target && into[i].Region == gene.Region)
                    {
                        at = i;
                        break;
                    }
                if (at < 0)
                {
                    into.Add(gene);
                    continue;
                }
                var resident = into[at];
                into[at] =
                    resident.Dominant != gene.Dominant
                        ? (resident.Dominant ? resident : gene)
                        : ((seed >> (gene.Region % 32)) & 1) == 0 ? resident : gene;
            }
        }
    }
}
