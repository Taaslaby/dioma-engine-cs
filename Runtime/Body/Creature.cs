// Существо: единственное место, где встречаются спайки и биохимия.
// Порт `dioma_engine::creature` (упрощённый: без гиппокампа и зеркала).
//
// Тик: сенсоры × чувствительность крови → внешний ток мозга → моторные суммы
// → тело (утомление, глюкоза, боль, дофамин) → действие. Мозг определяет
// ТОЛЬКО поворот; тягу и решение «есть» даёт кровь.
//
// Жизненный цикл: рождение (наследство весов + прожигание врождённых
// рефлексов + снимок Болдуина) → день (обучение дофамином) → ночь
// (консолидация) → размножение (`SpawnGenome`: нажитое становится
// врождённым у потомка).

using System;
using System.Collections.Generic;
using Dioma.Bio;
using Dioma.Spikes;

namespace Dioma.Body
{
    public sealed class Creature
    {
        // ── Константы контракта (эталон: creature/mod.rs) ──
        public const float BasalExt = 0.12f;          // фоновый тонус (подпорог)
        public const float SmellCurrent = 4.0f;
        public const float WallCurrent = 4.0f;
        public const float VisionCurrent = 0.7f * SmellCurrent;
        public const float SteerScale = 0.25f;
        public const float TonicDriveNorm = 0.5f;
        public const float FoodPortion = 60.0f;
        public const float EatDopamine = 1.0f;
        public const float WallDopamine = -0.5f;      // боль не платит глюкозой
        public const float ReliefDopamine = 0.4f;
        public const float ReliefMin = 0.05f;
        /// Голод новорождённого: с нулём существо сотни тиков ждёт распада
        /// стартовой глюкозы и не успевает начать охотиться.
        public const float NewbornHunger = 120.0f;
        /// Сон: порог утомления и страховка «долго не спал» (тихие особи
        /// химической усталости не копят и без счётчика не спят никогда).
        public const float SleepAtFatigue = 200.0f;
        public const int MaxAwakeTicks = 400;
        public const int SleepTicks = 12;

        public readonly DiomaBrain Brain;
        public readonly Blood Blood;
        /// Геном: наследуемые веса, метаболизм, врождённые рефлексы.
        public readonly Genome Genome;

        readonly float[] _ext;
        readonly int _rSl, _rSr, _rWl, _rWr, _rVl, _rVr, _rMl, _rMr;
        float _prevWall;

        public int FoodEaten { get; private set; }
        public int WallHits { get; private set; }
        public int LastMl { get; private set; }
        public int LastMr { get; private set; }
        public long LastSpikes { get; private set; }
        public int AwakeTicks { get; private set; }
        public int SleepLeft { get; private set; }
        public int Nights { get; private set; }
        /// Диагностические флаги ночной фазы: отключают закрепление весов и
        /// якорение цели гомеостаза. Нужны, чтобы делить вклад механизмов при
        /// разборе численных спиралей (см. StabilityTests).
        public bool DisableNightConsolidation;
        public bool DisableSleepAnchor;
        public bool Asleep => SleepLeft > 0;

        /// Прожиганий рефлекса при рождении: одного прохода мало — дуга
        /// набирает вес постепенно, три дают устойчивый рефлекс.
        public const int BirthBurnPasses = 3;
        /// Потолок весов при прожигании: выше рабочего, иначе первый же
        /// дофамин срежет врождённые хвосты и ослабит рефлекс.
        public const float BurnWCap = 64f;
        /// Ночная консолидация: что считать «сильным выученным», насколько
        /// усилить его и насколько обрезать шум.
        public const float SleepAnchorAlpha = 0.5f;
        public const float SleepStrongMin = 0.5f;
        public const float SleepReinforce = 0.15f;
        public const float SleepPrune = 0.7f;

        public Creature(DiomaBrain brain, Genome genome = null, Blood blood = null)
        {
            Brain = brain;
            Genome = genome ?? Genome.Newborn();
            Genome.Metabolism.ApplyTo(Blood = blood ?? Bio.Blood.Creature());
            // Голод новорождённого ставим ТОЛЬКО когда тело создаём сами:
            // переданная кровь — это либо загруженное сохранение, либо
            // подготовленное состояние, и перетирать его нельзя.
            if (blood == null)
                Blood.Conc[Chem.Hunger] = NewbornHunger;
            _ext = new float[brain.N];
            _rSl = brain.FindRegion("SL");
            _rSr = brain.FindRegion("SR");
            _rWl = brain.FindRegion("WL");
            _rWr = brain.FindRegion("WR");
            _rVl = brain.FindRegion("VL");
            _rVr = brain.FindRegion("VR");
            _rMl = brain.FindRegion("ML");
            _rMr = brain.FindRegion("MR");
            if (_rSl < 0 || _rSr < 0 || _rMl < 0 || _rMr < 0)
                throw new InvalidOperationException("коннектому нужны регионы SL, SR, ML, MR");

            // Наследство: выученные родителем веса поверх выращенной топологии.
            if (Genome.Weights != null && Genome.Weights.Length == brain.SynapseCount)
                brain.SetCsrValues(Genome.Weights);

            // Эталон Болдуина: снимок «того, с чем родился». От него считается
            // всё нажитое за жизнь — и снимок НЕ обновляется ночным сном,
            // иначе прирост обнулялся бы каждую ночь.
            brain.MemorySnapshot();

            // Врождённые рефлексы прожигаются при рождении; каждое прожигание
            // обновляет снимок, так что рефлекс не считается «нажитым».
            foreach (var instinct in Genome.Instincts)
                for (int pass = 0; pass < BirthBurnPasses; pass++)
                    instinct.BurnInto(brain, BasalExt, BurnWCap);
        }

        void Inject(int region, float current)
        {
            if (region < 0 || current == 0f)
                return;
            var r = Brain.Regions[region];
            for (int i = r.Start; i < r.End; i++)
                _ext[i] += current;
        }

        /// Один тик мира (50 мс).
        public MotorAction Tick(in Sense sense)
        {
            // ── Сон: мозг и мир отдыхают, тело восстанавливается ──
            if (SleepLeft > 0)
            {
                SleepLeft--;
                return new MotorAction { Asleep = true };
            }

            // Облегчение: стена отодвинулась за прошлый тик — награждаем
            // ИМЕННО тот отворот (его eligibility ещё горячая).
            float prox = MathF.Max(sense.WallL, sense.WallR);
            float relief = MathF.Max(_prevWall - prox, 0f);

            // ── 1. Сенсорные токи × чувствительность тела ──
            // Полный пересчёт каждый тик: инкрементальный давал дрейф s·Δg,
            // когда гейн двигался (голод/урон), и ломал охоту.
            float gain = Blood.SensorGain();
            for (int i = 0; i < _ext.Length; i++)
                _ext[i] = BasalExt;
            Inject(_rSl, sense.SmellL * gain * SmellCurrent);
            Inject(_rSr, sense.SmellR * gain * SmellCurrent);
            // Стену получают только мозги со стенными регионами. Лить её в
            // обоняние нельзя — это ложный запах, существо рулило бы В стену
            // (замерено: 974 удара на 1000 тиков у agent-мозга). Мозг без
            // WL/WR узнаёт о стене исключительно через боль удара.
            Inject(_rWl, sense.WallL * gain * WallCurrent);
            Inject(_rWr, sense.WallR * gain * WallCurrent);
            Inject(_rVl, sense.VisL * gain * VisionCurrent);
            Inject(_rVr, sense.VisR * gain * VisionCurrent);

            // ── 2. Мозг ──
            var ml = Brain.Regions[_rMl];
            var mr = Brain.Regions[_rMr];
            var (mlSum, mrSum, spikes) = Brain.TickMotor(_ext, ml.Start, ml.End, mr.Start, mr.End);
            LastMl = mlSum;
            LastMr = mrSum;
            LastSpikes = spikes;

            // ── 3. Тело: моторная работа и дефицит глюкозы в кровь ──
            Blood.Loci[Locus.RateMotor] = mlSum + mrSum;
            Blood.Loci[Locus.GlucoseDeficit] =
                MathF.Max(Bio.Blood.GlucoseAbundant - Blood.Conc[Chem.Glucose], 0f);

            if (sense.WallHit)
                HitWall();

            bool eat = sense.FoodNear && Blood.WantsFood();
            if (eat)
            {
                Blood.IngestFood(FoodPortion);
                FoodEaten++;
            }
            Blood.Update();

            // ── 4. Награды (обучение стоит глюкозы) ──
            if (eat && Blood.PayForPlasticity(EatDopamine))
                Brain.ScheduleReward(EatDopamine * Blood.PlasticityBudget());
            if (relief > ReliefMin)
                Brain.RewardNow(relief * ReliefDopamine);
            _prevWall = prox;

            // ── 5. Действие: мозг рулит, кровь даёт тягу ──
            var action = new MotorAction
            {
                Steer = Math.Clamp((mrSum - mlSum) * SteerScale * Blood.TurnGain(), -1f, 1f),
                Drive = Math.Clamp(Blood.MotorTonic() / TonicDriveNorm, 0f, 1f),
                Eat = eat,
            };

            // ── 6. Сон по утомлению или по счётчику бодрствования ──
            AwakeTicks++;
            if (Blood.Fatigue() >= SleepAtFatigue || AwakeTicks >= MaxAwakeTicks)
                GoToSleep();

            return action;
        }

        /// Удар о стену: боль в кровь + отрицательный дофамин по горячей
        /// eligibility (LTD того пути, что гнал в стену) + гомеостаз.
        public void HitWall()
        {
            WallHits++;
            Blood.BumpWall();
            Brain.RewardNow(WallDopamine);
        }

        void GoToSleep()
        {
            SleepLeft = SleepTicks;
            AwakeTicks = 0;
            Nights++;
            // Ночь: окно обучения закрывается (след ненаграждённой пробы не
            // должен дожить до утра), гомеостаз якорится на выученном, сильные
            // пути дня закрепляются и слегка усиливаются, шум обрезается,
            // тело восстанавливается. Снимок Болдуина НЕ трогается — прирост
            // за жизнь копится до размножения.
            Brain.ClearEligibility();
            if (!DisableSleepAnchor)
                Brain.SleepConsolidate(SleepAnchorAlpha);
            if (!DisableNightConsolidation)
                Brain.ConsolidateSleep(SleepStrongMin, SleepReinforce, SleepPrune);
            Blood.Sleep();
        }

        // ── Наследственность ──

        /// Рефлексы, нажитые за жизнь: дуги из `Genome.ReflexPairs`, где
        /// прирост веса относительно снимка рождения выше порога. Это эффект
        /// Болдуина — навык, выученный этим существом, станет врождённым у
        /// потомка (без изменения топологии).
        public List<Instinct> LearnedInstincts()
        {
            var scored = new List<(float Gain, int Pre, int Post)>();
            foreach (var (preTag, postTag) in Genome.ReflexPairs)
            {
                int pre = Brain.FindRegion(preTag), post = Brain.FindRegion(postTag);
                if (pre < 0 || post < 0)
                    continue;
                float gain = Brain.LearnedGain(pre, post);
                if (gain >= Genome.LearnedMinGain)
                    scored.Add((gain, pre, post));
            }
            scored.Sort((a, b) => b.Gain.CompareTo(a.Gain));

            var result = new List<Instinct>();
            for (int i = 0; i < scored.Count && i < Genome.LearnedTopN; i++)
            {
                var (gain, pre, post) = scored[i];
                var inputs = new List<(int, float)>();
                var rPre = Brain.Regions[pre];
                var rPost = Brain.Regions[post];
                for (int n = rPre.Start; n < rPre.End; n++)
                    inputs.Add((n, Genome.LearnedPreDrive));
                for (int n = rPost.Start; n < rPost.End; n++)
                    inputs.Add((n, Genome.LearnedPostDrive));
                float dopamine = Math.Clamp(gain / Genome.LearnedDopamineScale,
                                            Instinct.DopamineMin, Instinct.DopamineMax);
                result.Add(new Instinct(inputs, dopamine));
            }
            return result;
        }

        /// Геном потомка. Наследуется НЕ выученное напрямую (это был бы
        /// ламаркизм), а проводка, с которой родитель РОДИЛСЯ, — с дрейфом
        /// весов и мутацией метаболизма. Нажитое за жизнь передаётся иначе:
        /// сильные пути сворачиваются в гены инстинктов, и ребёнок отстраивает
        /// рефлекс сам при рождении (эффект Болдуина). Топология при этом не
        /// меняется — девелопментальный рост не нужен.
        public Genome SpawnGenome(ulong childSeed)
        {
            var rng = new XorShift(childSeed ^ 0x9E3779B97F4A7C15UL);
            var parent = Genome.Clone();
            parent.Weights = Brain.BirthWeights();
            var child = parent.Mutate(rng);
            child.AddLearnedInstincts(LearnedInstincts(), rng);
            return child;
        }
    }
}
