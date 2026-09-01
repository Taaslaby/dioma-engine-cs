// Геном существа и наследование. Порт `dioma_engine::creature::genome` +
// `spawn_child` (упрощённо: без пола, кроссовера и Пифии).
//
// Ключевой факт: рост коннектома при размножении НЕ запускается. Топология
// пересаживается от родителя как есть, а наследуется:
//   - выученные веса синапсов (с дрейфом — «печать» ребёнка),
//   - гены метаболизма (цена активности, урон от голода, цена обучения),
//   - инстинкты: пути, нажитые родителем за жизнь (эффект Болдуина).
// Поэтому эволюция работает без порта девелопментального роста.

using System;
using System.Collections.Generic;
using Dioma.Bio;
using Dioma.Spikes;

namespace Dioma.Body
{
    /// Гены метаболизма: те же четыре ручки, что у органа «мозг».
    public struct MetabolismGene
    {
        public float CostPerActivity;
        public float StarveDamage;
        public float CostPerDopamine;
        public float HealRate;

        public static MetabolismGene Default() => new MetabolismGene
        {
            CostPerActivity = 0.0015f,
            StarveDamage = 0.004f,
            CostPerDopamine = 0.4f,
            HealRate = 0.0001f,
        };

        public MetabolismGene Mutate(XorShift rng, float sigma)
        {
            // Относительный дрейф, всё строго положительное: ноль означал бы
            // «активность бесплатна» или «голод не калечит» — вырожденные тела,
            // которые отбор никогда не отсеет.
            float Drift(float v) => MathF.Max(v * (1f + (float)rng.Gauss() * sigma), 1e-6f);
            return new MetabolismGene
            {
                CostPerActivity = Drift(CostPerActivity),
                StarveDamage = Drift(StarveDamage),
                CostPerDopamine = Drift(CostPerDopamine),
                HealRate = Drift(HealRate),
            };
        }

        public void ApplyTo(Blood blood)
        {
            blood.Organ.CostPerActivity = CostPerActivity;
            blood.Organ.StarveDamage = StarveDamage;
            blood.Organ.CostPerDopamine = CostPerDopamine;
            blood.Organ.HealRate = HealRate;
        }
    }

    public sealed class Genome
    {
        /// Дрейф наследуемых весов синапсов.
        public const float InheritWeightSigma = 0.05f;
        /// Дрейф генов метаболизма.
        public const float MetabolismSigma = 0.05f;
        /// Дрейф инстинктов.
        public const float InstinctSigma = 0.08f;
        /// Минимальный нажитый вес на дуге, чтобы она стала инстинктом.
        public const float LearnedMinGain = 100f;
        /// Сколько рефлексов максимум сворачивается в геном.
        public const int LearnedTopN = 3;
        /// Нажитый вес → дофамин инстинкта.
        public const float LearnedDopamineScale = 100f;
        /// Токи ко-активации при прожигании: пост сильнее, чтобы дуга
        /// «сенсор → мотор» получила спайк на обоих концах.
        public const float LearnedPreDrive = 4f;
        public const float LearnedPostDrive = 8f;
        /// Дрейф регуляторных генов (REG_SIGMA в Rust dna.rs).
        public const float RegulatorySigma = 0.05f;
        /// Пары регионов, которые могут стать врождёнными рефлексами.
        /// Зрение здесь намеренно отсутствует: оно учится, а не прошивается —
        /// иначе охота теряет гибкость.
        public static readonly (string Pre, string Post)[] ReflexPairs =
        {
            ("SL", "ML"), ("SR", "MR"), ("WL", "MR"), ("WR", "ML"),
        };

        /// Веса синапсов CSR (наследуются от родителя; null у первого поколения).
        public float[] Weights;
        public MetabolismGene Metabolism = MetabolismGene.Default();
        public readonly List<Instinct> Instincts = new List<Instinct>();
        /// Регуляторный геном: сила пластичности/инстинктов/перестройки/
        /// сытости по возрасту. Дикий тип нейтрален — поведение не меняется,
        /// пока гены не занесли руками или мутацией.
        public RegGenome Reg = RegGenome.Wildtype();

        public static Genome Newborn() => new Genome();

        public Genome Clone()
        {
            var g = new Genome
            {
                Weights = Weights == null ? null : (float[])Weights.Clone(),
                Metabolism = Metabolism,
            };
            CopyReg(g.Reg, Reg);
            foreach (var i in Instincts)
                g.Instincts.Add(i.Clone());
            return g;
        }

        static void CopyReg(RegGenome into, RegGenome from)
        {
            for (int i = 0; i < RegTargets.N; i++)
                into.Systemic[i] = from.Systemic[i];
            foreach (var gene in from.Regional)
                into.Regional.Add(gene);
        }

        /// Потомок генома: веса дрейфуют, метаболизм, инстинкты и
        /// регуляторные гены мутируют. Детерминированно от сида — один сид
        /// даёт одного потомка.
        public Genome Mutate(XorShift rng)
        {
            var child = new Genome { Metabolism = Metabolism.Mutate(rng, MetabolismSigma) };
            if (Weights != null)
            {
                child.Weights = new float[Weights.Length];
                for (int i = 0; i < Weights.Length; i++)
                    // Пол 0.1: нулевой вес — мёртвый синапс, который уже никогда
                    // не оживёт (Δw = lr·D·e, а e требует спайка).
                    child.Weights[i] = MathF.Max(
                        Weights[i] * (1f + (float)rng.Gauss() * InheritWeightSigma), 0.1f);
            }
            foreach (var i in Instincts)
                child.Instincts.Add(i.Mutate(rng, InstinctSigma));
            child.Reg = Reg.Mutate(rng, RegulatorySigma);
            return child;
        }

        /// Добавить в геном рефлексы, нажитые родителем за жизнь. Дедупликация
        /// по паре регионов и потолок `LearnedTopN`: у ребёнка не больше
        /// врождённых рефлексов, чем у одного родителя после свёртки.
        public void AddLearnedInstincts(List<Instinct> learned, XorShift rng)
        {
            foreach (var i in learned)
            {
                if (Instincts.Count >= LearnedTopN)
                    break;
                Instincts.Add(i.Mutate(rng, InstinctSigma));
            }
        }
    }
}
