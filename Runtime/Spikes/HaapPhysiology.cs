// Физиология HAAP-нейронов: архетип региона задаёт шаблон 17 колонок, гены
// экспрессии каналов (HCN/KCNQ/para/leak) его модулируют. Порт
// `biology::physiology` (Rust) числа-в-числа.
//
// Это мост между ДНК и спайковым ядром: PhysioGenes наследуются и мутируют,
// и один коэффициент экспрессии канала становится эволюционным признаком —
// нокаут KCNQ (адаптации) даёт runaway-мозг, усиление HCN — нейроны с
// долгими плато. Гены применяются ДО инициализации мембраны: равновесие
// покоя (`HaapModel.RestingState`) считается уже по фактическим колонкам.

using System;

namespace Dioma.Spikes
{
    /// Гены экспрессии каналов (0..1+, wildtype = 1). Наследуются и мутируются.
    public struct PhysioGenes
    {
        /// HCN h-ток: rebound, медленные плато (память).
        public float Hcn;
        /// KCNQ M-ток: адаптация, гашение burst'ов.
        public float Kcnq;
        /// para NaV: возбудимость (сдвиг порога).
        public float Para;
        /// leak K2P: входное сопротивление.
        public float Leak;

        public static PhysioGenes Wildtype() => new PhysioGenes
        {
            Hcn = 1f, Kcnq = 1f, Para = 1f, Leak = 1f,
        };

        /// Мутация: относительный дрейф каждого гена, всё строго
        /// положительное (ноль = нокаут канала — дегенеративный мозг,
        //  который отбор должен увидеть, а не получить случайно).
        public PhysioGenes Mutate(Dioma.Spikes.XorShift rng, float sigma)
        {
            float Drift(float v) =>
                MathF.Max(v * (1f + (float)rng.Gauss() * sigma), 0.05f);
            return new PhysioGenes
            {
                Hcn = Drift(Hcn),
                Kcnq = Drift(Kcnq),
                Para = Drift(Para),
                Leak = Drift(Leak),
            };
        }
    }

    /// Архетип нейрона: роль региона задаёт шаблон физиологии.
    public enum NeuronArchetype
    {
        /// Сенсор: чуткий реле, слабая адаптация.
        Sensory,
        /// Мотор: VNC-профиль — адаптация против runaway.
        Motor,
        /// Тормозной интернейрон: быстрый, звонкий.
        Interneuron,
        /// Память: сильный h-ток + медленный — плато на секунды.
        Memory,
        /// Пейсмекер: низкий порог + адаптация = ритмические burst'ы.
        Pacemaker,
    }

    public static class HaapPhysiology
    {
        /// dt подшага HAAP в мс (DT_MS_HAAP в Rust).
        public const float DtMs = 0.05f;

        /// Размах сдвига порога при экспрессии para от 0 до 2
        /// (PARA_THRESHOLD_SPAN в Rust).
        public const float ParaThresholdSpan = 8f;

        /// База = рабочая точка tripod_cpg (проверена в движке): e_l −60,
        /// g_l 0.1, g_m 0.4, tau_u 100, порог −40. g_h понижен 0.8→0.35:
        /// сильный h-ток на КАЖДОМ нейроне вместе с рекуррентным
        /// торможением даёт post-inhibitory rebound-судороги (PING-петля).
        public static void TemplateInto(float[] row, NeuronArchetype arch)
        {
            row[(int)HaapCol.Dt] = DtMs;
            row[(int)HaapCol.InvCm] = 1f;
            row[(int)HaapCol.GL] = 0.10f;
            row[(int)HaapCol.EL] = -60f;
            row[(int)HaapCol.GM] = 0.4f;
            row[(int)HaapCol.EK] = -80f;
            row[(int)HaapCol.GH] = 0.35f;
            row[(int)HaapCol.EH] = -30f;
            row[(int)HaapCol.InvTauU] = 1f / 100f;
            row[(int)HaapCol.InvTauH] = 1f / 120f;
            row[(int)HaapCol.VHalfU] = -35f;
            row[(int)HaapCol.KU] = 5f;
            row[(int)HaapCol.VHalfH] = -75f;
            row[(int)HaapCol.KH] = 6f;
            row[(int)HaapCol.VThresh] = -40f;
            row[(int)HaapCol.VReset] = -55f;
            row[(int)HaapCol.DeltaU] = 0.02f;

            switch (arch)
            {
                case NeuronArchetype.Sensory:
                case NeuronArchetype.Motor:
                    // Реле/мотор: сильная адаптация ограничивает верх частоты
                    // на сильном сигнале, высокий reset — быстрый перезапуск.
                    row[(int)HaapCol.VThresh] = -42f;
                    row[(int)HaapCol.DeltaU] = 0.03f;
                    row[(int)HaapCol.InvTauU] = 1f / 50f;
                    row[(int)HaapCol.VReset] = -47f;
                    break;
                case NeuronArchetype.Interneuron:
                    row[(int)HaapCol.InvTauU] = 1f / 50f;
                    break;
                case NeuronArchetype.Memory:
                    // Сильный h-ток — плато на секундах; M слаб, чтобы burst
                    // не гас сразу; медленный h — секунды.
                    row[(int)HaapCol.GH] = 0.8f;
                    row[(int)HaapCol.GM] = 0.15f;
                    row[(int)HaapCol.InvTauH] = 1f / 400f;
                    row[(int)HaapCol.VHalfH] = -70f;
                    row[(int)HaapCol.VReset] = -50f;
                    row[(int)HaapCol.DeltaU] = 0.01f;
                    break;
                case NeuronArchetype.Pacemaker:
                    // Эндогенный осциллятор: h-ток с полуактивацией у покоя
                    // тянет к порогу, burst подсыпает delta_u=0.09, очень
                    // медленный M-ток держит длинную паузу.
                    row[(int)HaapCol.VThresh] = -47f;
                    row[(int)HaapCol.GH] = 1.6f;
                    row[(int)HaapCol.VHalfH] = -56f;
                    row[(int)HaapCol.KH] = 7f;
                    row[(int)HaapCol.GM] = 0.30f;
                    row[(int)HaapCol.DeltaU] = 0.09f;
                    row[(int)HaapCol.InvTauU] = 1f / 700f;
                    break;
            }
        }

        /// Архетип × гены экспрессии → колонки нейрона (в строку row).
        /// Порядок применения как в Rust: сначала шаблон, потом гены.
        public static void ColumnsInto(float[] row, NeuronArchetype arch,
            in PhysioGenes genes)
        {
            TemplateInto(row, arch);
            // HCN: масштабирует h-ток (нокаут = нет rebound/плато).
            row[(int)HaapCol.GH] *= MathF.Max(genes.Hcn, 0f);
            // KCNQ: масштабирует M-ток (нокаут = нет адаптации → runaway).
            row[(int)HaapCol.GM] *= MathF.Max(genes.Kcnq, 0f);
            // para: сдвиг порога (низкая экспрессия → труднее возбудить).
            row[(int)HaapCol.VThresh] += (genes.Para - 1f) * ParaThresholdSpan;
            // leak: масштабирует проводимость утечки (больше = тише).
            row[(int)HaapCol.GL] *= MathF.Max(genes.Leak, 0.05f);
        }

        /// Колонки всего мозга по регионам: каждому нейрону — архетип его
        /// региона. Пейсмекерные регионы разрежаются (≈1/3 нейронов,
        /// детерминированный хэш): сплошной пейсмекер рекуррентная связь
        /// синхронизирует в постоянный стрёп — контраст ignition пропадает.
        public static float[] BrainProfile(int[] regionOf,
            Func<int, NeuronArchetype> archetypeOf, in PhysioGenes genes)
        {
            const double pacemakerFraction = 0.34;
            var profile = new float[regionOf.Length * HaapCols.N];
            var counters = new System.Collections.Generic.Dictionary<int, int>();
            var row = new float[HaapCols.N];
            for (int i = 0; i < regionOf.Length; i++)
            {
                var arch = archetypeOf(regionOf[i]);
                if (arch == NeuronArchetype.Pacemaker)
                {
                    counters.TryGetValue(regionOf[i], out int c);
                    counters[regionOf[i]] = c + 1;
                    double h = XorShift.Hash01(i);
                    arch = h < pacemakerFraction
                        ? NeuronArchetype.Pacemaker
                        : NeuronArchetype.Interneuron;
                }
                ColumnsInto(row, arch, genes);
                Array.Copy(row, 0, profile, i * HaapCols.N, HaapCols.N);
            }
            return profile;
        }

        /// Архетип региона по ИНДЕКСУ (порядок регионов коннектома) для
        /// арены dioma: SL/SR — сенсоры, I — интернейрон, остальное —
        /// мотор. Индексы = позиции регионов в файле (arena_archetype).
        public static NeuronArchetype ArenaArchetype(int region) => region switch
        {
            0 or 1 => NeuronArchetype.Sensory,
            2 => NeuronArchetype.Interneuron,
            _ => NeuronArchetype.Motor,
        };

        /// Когнитивная схема (9 регионов): workspace — пейсмекеры
        /// (burst-гейтинг ignition), WM — память (плато).
        public static NeuronArchetype CognitiveArchetype(int region) => region switch
        {
            0 or 1 => NeuronArchetype.Sensory,
            2 => NeuronArchetype.Interneuron,
            3 or 4 => NeuronArchetype.Motor,
            5 or 6 => NeuronArchetype.Pacemaker,
            _ => NeuronArchetype.Memory,
        };

        /// Рефлекторная схема (3 региона).
        public static NeuronArchetype ReflexArchetype(int region) => region switch
        {
            0 => NeuronArchetype.Sensory,
            1 => NeuronArchetype.Interneuron,
            _ => NeuronArchetype.Motor,
        };
    }
}
