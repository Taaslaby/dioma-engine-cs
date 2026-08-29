// Существо: единственное место, где встречаются спайки и биохимия.
// Порт `dioma_engine::creature` (упрощённый: без гиппокампа/зеркала/генома).
//
// Тик: сенсоры × чувствительность крови → внешний ток мозга → моторные суммы
// → тело (утомление, глюкоза, боль, дофамин) → действие. Мозг определяет
// ТОЛЬКО поворот; тягу и решение «есть» даёт кровь.

using System;
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
        public bool Asleep => SleepLeft > 0;

        public Creature(DiomaBrain brain, Blood blood = null)
        {
            Brain = brain;
            Blood = blood ?? Bio.Blood.Creature();
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
            // Ночь: окно обучения закрывается, гомеостаз якорится на выученном,
            // тело восстанавливается.
            Brain.ClearEligibility();
            Brain.SleepConsolidate(0.5f);
            Blood.Sleep();
        }
    }
}
