// Биохимия существа: порт `dioma_engine::biology::metabolism` (Rust) —
// стехиометрические реакции в «крови», рецепторы (химия → локус) и эмиттеры
// (локус → химия) поверх них, орган «мозг» с метаболической ценой активности.
//
// Модуль НЕ знает ни о спайках, ни о мире: хост пишет драйв-локусы
// (RATE_MOTOR, GLUCOSE_DEFICIT) и события (IngestFood/BumpWall), зовёт
// Update(), затем читает параметры тела (SensorGain, MotorTonic, Speed,
// TurnGain, WantsFood, PlasticityBudget).
//
// Числа — один-в-один с Rust-эталоном; порядок фаз внутри тика значим
// (реакции → распад → эмиттеры → сброс+рецепторы → орган → пины).

using System;
using System.Collections.Generic;

namespace Dioma.Bio
{
    /// Индексы химий крови.
    public static class Chem
    {
        public const int Glucose = 0;       // питание
        public const int Food = 1;          // содержимое желудка
        public const int Hunger = 2;        // драйв
        public const int Pain = 3;          // ноцицепция
        public const int Reward = 4;        // дофамин (событие еды)
        public const int Fatigue = 5;       // утомление от моторики
        public const int Serotonin = 6;     // медленный фон благополучия
        public const int Cortisol = 7;      // медленный стресс
        public const int Acetylcholine = 8; // посредник внимания
        public const int N = 9;

        /// Доля, остающаяся за тик 50 мс.
        public const float HormoneSlow = 0.99900025f;

        public static float[] DecayTable()
        {
            var d = new float[N];
            for (int i = 0; i < N; i++) d[i] = 0.998001f;
            d[Pain] = 0.81f;
            d[Reward] = 0.8836f;
            d[Hunger] = 0.990025f;
            d[Fatigue] = 0.990025f;
            d[Serotonin] = HormoneSlow;
            d[Cortisol] = HormoneSlow;
            d[Acetylcholine] = 0.996004f;
            return d;
        }
    }

    /// Локусы — именованные точки «чего угодно» существа.
    public static class Locus
    {
        public const int SensorGain = 0;
        public const int MotorTonic = 1;
        public const int Speed = 2;
        public const int TurnGain = 3;
        public const int GainGlobal = 4;      // множитель от повреждения органа
        public const int RateMotor = 5;       // спайки моторов за тик (пишет тело)
        public const int EventAte = 6;        // событие еды (эмиттер гасит)
        public const int EventWall = 7;       // удар о стену
        public const int GlucoseDeficit = 8;  // max(0, 150 − глюкоза)
        public const int N = 9;

        public static float[] Defaults()
        {
            var v = new float[N];
            v[SensorGain] = 3.5f;
            v[MotorTonic] = 0.12f;
            v[Speed] = 2.0f;
            v[TurnGain] = 0.06f;
            v[GainGlobal] = 1.0f;
            return v;
        }
    }

    /// `prop_r1·R1 + prop_r2·R2 --rate--> prop_p1·P1 + prop_p2·P2`.
    /// Отсутствующий реагент/продукт — `Reaction.None`.
    public struct Reaction
    {
        public const int None = -1;
        public float PropR1; public int R1;
        public float PropR2; public int R2;
        public float Rate;
        public float PropP1; public int P1;
        public float PropP2; public int P2;
    }

    /// Химия → локус (аддитивно поверх дефолта).
    public struct Receptor
    {
        public int Chem;
        public float Threshold;
        public float Gain;
        public bool Reduce;   // вычитать вместо прибавления
        public bool Digital;  // фиксированный гейн при любом сигнале > порога
        public int Locus;
    }

    /// Локус → химия.
    public struct Emitter
    {
        public int Locus;
        public float Threshold;
        public float Gain;
        public bool Remove;   // гасить локус после отклика (событийные всплески)
        public bool Digital;
        public int Chem;
    }

    /// Орган «мозг»: ест глюкозу за активность, голодает — повреждается.
    public sealed class BrainOrgan
    {
        public float CostPerActivity = 0.0015f;
        public float StarveDamage = 0.004f;
        public float Damage;
        public float CostPerDopamine = 0.4f;
        public float HealRate = 0.0001f;
    }

    /// Читаемая извне шкала крови/локусов. Индексатор на запись доступен
    /// только сборке биохимии: игровой слой не может менять состояние в
    /// обход событий Blood.
    public sealed class BloodValues : IReadOnlyList<float>
    {
        readonly float[] _values;

        internal BloodValues(float[] values) => _values = values;
        public int Count => _values.Length;
        public float this[int index]
        {
            get => _values[index];
            internal set => _values[index] = value;
        }
        public IEnumerator<float> GetEnumerator() => ((IEnumerable<float>)_values).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public float[] Snapshot() => (float[])_values.Clone();
        internal void CopyFrom(float[] source) => Array.Copy(source, _values, _values.Length);
    }

    public sealed class Blood
    {
        /// Потолок глюкозы после сна: сон не кормит, а лишь не даёт умереть
        /// (ниже порога дефицита 150 — иначе голод не синтезируется и
        /// новорождённый не охотится).
        public const float SleepGlucoseCap = 120.0f;
        public const float ConcMax = 255.0f;
        /// Порог «изобилия»: выше него орган заживает, ниже — растёт дефицит
        /// (хост пишет `Locus.GlucoseDeficit = max(0, GlucoseAbundant − глюкоза)`).
        public const float GlucoseAbundant = 150.0f;

        public int Tick;
        public readonly BloodValues Conc = new BloodValues(new float[Chem.N]);
        public readonly BloodValues Loci = new BloodValues(Locus.Defaults());
        public readonly BrainOrgan Organ = new BrainOrgan();
        public readonly List<KeyValuePair<int, float>> Pins = new List<KeyValuePair<int, float>>();

        readonly float[] _decay = Chem.DecayTable();
        readonly List<Reaction> _reactions = new List<Reaction>();
        readonly List<Receptor> _receptors = new List<Receptor>();
        readonly List<Emitter> _emitters = new List<Emitter>();
        readonly float[] _locusEffects = new float[Locus.N];

        Blood() { }

        /// Биохимия существа: гомеостаз голода через стехиометрию, утомление
        /// от моторики, дофамин от еды, боль от стен. `robot` пиннит голод
        /// на максимум (управляемая «машина» без гомеостаза).
        public static Blood Creature(bool robot = false)
        {
            var b = new Blood();

            // ── Реакции (порядок значим: каждая видит conc после предыдущей) ──
            // Пищеварение: 1 FOOD → 1 GLUCOSE + 0.3 ACh (поел — мозг получил
            // посредник для обучения).
            b._reactions.Add(new Reaction
            {
                PropR1 = 1.0f, R1 = Chem.Food, PropR2 = 0.0f, R2 = Reaction.None,
                Rate = 0.20f,
                PropP1 = 1.0f, P1 = Chem.Glucose, PropP2 = 0.3f, P2 = Chem.Acetylcholine,
            });
            // Ферментативное утоление: 1 HUNGER + 1 GLUCOSE → 1 GLUCOSE.
            // Глюкоза — фермент: нужна (входит в лимитирующий реагент), но
            // возвращается. Упрощать до «вычесть HUNGER» нельзя.
            b._reactions.Add(new Reaction
            {
                PropR1 = 1.0f, R1 = Chem.Hunger, PropR2 = 1.0f, R2 = Chem.Glucose,
                Rate = 0.020f,
                PropP1 = 1.0f, P1 = Chem.Glucose, PropP2 = 0.0f, P2 = Reaction.None,
            });
            // Серотонин из сытости: медленный каскад «сыт сегодня — спокоен часами».
            b._reactions.Add(new Reaction
            {
                PropR1 = 1.0f, R1 = Chem.Glucose, PropR2 = 0.0f, R2 = Reaction.None,
                Rate = 0.0008f,
                PropP1 = 1.0f, P1 = Chem.Serotonin, PropP2 = 0.0f, P2 = Reaction.None,
            });

            // ── Рецепторы: химия → локус ──
            b.Rec(Chem.Hunger, 10.0f, 4.0f, false, false, Locus.SensorGain);    // голод обостряет обоняние
            b.Rec(Chem.Hunger, 10.0f, 0.10f, false, false, Locus.MotorTonic);   // и поднимает тон
            b.Rec(Chem.Pain, 5.0f, 0.04f, false, true, Locus.TurnGain);         // боль → тревожный поворот
            b.Rec(Chem.Fatigue, 20.0f, 1.2f, true, false, Locus.Speed);         // утомление замедляет
            b.Rec(Chem.Glucose, 150.0f, 0.4f, true, false, Locus.Speed);        // сытость замедляет
            b.Rec(Chem.Hunger, 240.0f, 1.5f, false, true, Locus.MotorTonic);    // паника при смертельном голоде
            b.Rec(Chem.Serotonin, 10.0f, 0.03f, true, false, Locus.TurnGain);   // серотонин успокаивает
            b.Rec(Chem.Cortisol, 20.0f, 1.5f, false, false, Locus.SensorGain);  // стрессовая бдительность
            b.Rec(Chem.Cortisol, 60.0f, 0.5f, true, false, Locus.MotorTonic);   // замороженность при стрессе

            // ── Эмиттеры: локус → химия ──
            b.Emit(Locus.EventAte, 0.5f, 60.0f, true, true, Chem.Reward);
            b.Emit(Locus.EventWall, 0.5f, 40.0f, true, true, Chem.Pain);
            b.Emit(Locus.RateMotor, 6.0f, 0.033f, false, false, Chem.Fatigue);
            b.Emit(Locus.GlucoseDeficit, 15.0f, 0.08f, false, false, Chem.Hunger);
            b.Emit(Locus.GlucoseDeficit, 40.0f, 0.008f, false, false, Chem.Cortisol);
            b.Emit(Locus.EventWall, 0.5f, 8.0f, true, true, Chem.Cortisol);

            b.Conc[Chem.Glucose] = 128.0f;
            b.Conc[Chem.Serotonin] = 30.0f;
            if (robot)
                b.Pins.Add(new KeyValuePair<int, float>(Chem.Hunger, 255.0f));
            return b;
        }

        void Rec(int chem, float threshold, float gain, bool reduce, bool digital, int locus) =>
            _receptors.Add(new Receptor
            {
                Chem = chem, Threshold = threshold, Gain = gain,
                Reduce = reduce, Digital = digital, Locus = locus,
            });

        void Emit(int locus, float threshold, float gain, bool remove, bool digital, int chem) =>
            _emitters.Add(new Emitter
            {
                Locus = locus, Threshold = threshold, Gain = gain,
                Remove = remove, Digital = digital, Chem = chem,
            });

        // ── Тик крови ──

        /// Один тик (50 мс). Драйв-локусы (RATE_MOTOR, GLUCOSE_DEFICIT) и
        /// события должны быть записаны ДО вызова.
        public void Update()
        {
            // 1. Реакции: списываем лимитирующий реагент, начисляем продукты.
            for (int i = 0; i < _reactions.Count; i++)
            {
                var r = _reactions[i];
                float avail1 = r.R1 == Reaction.None
                    ? float.PositiveInfinity
                    : Conc[r.R1] / MathF.Max(r.PropR1, 1e-9f);
                float avail2 = r.R2 == Reaction.None
                    ? float.PositiveInfinity
                    : Conc[r.R2] / MathF.Max(r.PropR2, 1e-9f);
                float moles = (r.R1 == Reaction.None && r.R2 == Reaction.None)
                    ? r.Rate
                    : r.Rate * MathF.Min(avail1, avail2);
                if (moles <= 0f)
                    continue;
                if (r.R1 != Reaction.None) Conc[r.R1] -= moles * r.PropR1;
                if (r.R2 != Reaction.None) Conc[r.R2] -= moles * r.PropR2;
                if (r.P1 != Reaction.None) Conc[r.P1] += moles * r.PropP1;
                if (r.P2 != Reaction.None) Conc[r.P2] += moles * r.PropP2;
            }

            // 2. Распад + единственный кламп концентраций.
            for (int i = 0; i < Chem.N; i++)
                Conc[i] = Math.Clamp(Conc[i] * _decay[i], 0f, ConcMax);

            // 3. Эмиттеры: сначала ВСЯ эмиссия (EVENT_WALL слушают два
            //    эмиттера — боль и кортизол), затем гашение событийных локусов.
            for (int i = 0; i < _emitters.Count; i++)
            {
                var e = _emitters[i];
                float v = Loci[e.Locus];
                if (v > e.Threshold)
                {
                    float amount = e.Digital ? e.Gain : e.Gain * v;
                    Conc[e.Chem] = MathF.Min(Conc[e.Chem] + amount, ConcMax);
                }
            }
            for (int i = 0; i < _emitters.Count; i++)
            {
                var e = _emitters[i];
                if (e.Remove && Loci[e.Locus] > e.Threshold)
                    Loci[e.Locus] = 0f;
            }

            // 4. Рецепторы: локусы сбрасываются к дефолтам, драйв-локусы
            //    (их пишет хост/эмиттеры) переживают сброс.
            float ate = Loci[Locus.EventAte];
            float wall = Loci[Locus.EventWall];
            float rate = Loci[Locus.RateMotor];
            float deficit = Loci[Locus.GlucoseDeficit];
            var defaults = Locus.Defaults();
            Loci.CopyFrom(defaults);
            Loci[Locus.EventAte] = ate;
            Loci[Locus.EventWall] = wall;
            Loci[Locus.RateMotor] = rate;
            Loci[Locus.GlucoseDeficit] = deficit;

            Array.Clear(_locusEffects, 0, Locus.N);
            for (int i = 0; i < _receptors.Count; i++)
            {
                var r = _receptors[i];
                float signal = MathF.Max(Conc[r.Chem] - r.Threshold, 0f);
                // Аналоговый рецептор нормирует на 255 (эмиттер — нет: асимметрия
                // эталона, воспроизводится буквально).
                float effect = r.Digital
                    ? (signal > 0f ? r.Gain : 0f)
                    : r.Gain * signal / 255.0f;
                _locusEffects[r.Locus] += r.Reduce ? -effect : effect;
            }
            for (int i = 0; i < Locus.N; i++)
                if (_locusEffects[i] != 0f)
                    Loci[i] += _locusEffects[i];

            // 5. Орган «мозг»: активность стоит глюкозы; пустая — урон,
            //    изобилие — заживление (тоже за глюкозу).
            float activity = Loci[Locus.RateMotor];
            float cost = Organ.CostPerActivity * activity;
            if (Conc[Chem.Glucose] >= cost)
            {
                Conc[Chem.Glucose] -= cost;
            }
            else
            {
                Conc[Chem.Glucose] = 0f;
                Organ.Damage = MathF.Min(Organ.Damage + Organ.StarveDamage, 1.0f);
            }
            if (Conc[Chem.Glucose] > GlucoseAbundant && Organ.Damage > 0f)
            {
                Organ.Damage = MathF.Max(Organ.Damage - Organ.HealRate, 0f);
                Conc[Chem.Glucose] -= 0.02f;
            }
            Loci[Locus.GainGlobal] *= 1.0f - Organ.Damage;

            // 6. Пины (контроль «робота») — поверх всего.
            for (int i = 0; i < Pins.Count; i++)
                Conc[Pins[i].Key] = Pins[i].Value;

            Tick++;
        }

        // ── События мира ──

        public void IngestFood(float amount)
        {
            Conc[Chem.Food] = MathF.Min(Conc[Chem.Food] + amount, ConcMax);
            Loci[Locus.EventAte] = 1.0f;
        }

        public void BumpWall() => Loci[Locus.EventWall] = 1.0f;

        /// Явная загрузка или отладочная настройка концентрации. Обычная
        /// игровая динамика должна пользоваться IngestFood/BumpWall/…,
        /// а не записывать химии напрямую.
        public void SetConcentration(int chem, float value) =>
            Conc[chem] = Math.Clamp(value, 0f, ConcMax);

        /// Драйверы тела задают только свои входные локусы до Update().
        public void SetDriverLocus(int locus, float value) => Loci[locus] = value;

        /// Настройка не-драйверного локуса для детерминированных инструментов
        /// и тестовых сценариев; игровой код использует события Blood.
        public void SetLocusForSetup(int locus, float value) => Loci[locus] = value;

        /// Снимок нужен сохранению: наружу не утекает изменяемый массив.
        public float[] ConcentrationSnapshot() => Conc.Snapshot();

        /// Восстановить persistence-состояние без воспроизведения реакций.
        public void RestoreState(float[] concentrations, int tick, float organDamage)
        {
            if (concentrations == null) throw new ArgumentNullException(nameof(concentrations));
            if (concentrations.Length != Chem.N)
                throw new ArgumentException($"концентраций {concentrations.Length}, ожидалось {Chem.N}");
            for (int i = 0; i < Chem.N; i++) SetConcentration(i, concentrations[i]);
            Tick = tick;
            Organ.Damage = Math.Clamp(organDamage, 0f, 1f);
        }

        /// Сон: утомление и стресс спадают, глюкоза подтягивается к капу
        /// (ВНИМАНИЕ: выше капа — усекается, это не защитный кламп),
        /// повреждение органа частично заживает.
        public void Sleep()
        {
            Conc[Chem.Fatigue] = MathF.Min(Conc[Chem.Fatigue] * 0.3f, ConcMax);
            Conc[Chem.Cortisol] = MathF.Min(Conc[Chem.Cortisol] * 0.5f, ConcMax);
            Conc[Chem.Glucose] = MathF.Min(Conc[Chem.Glucose] + 15.0f, SleepGlucoseCap);
            Organ.Damage = MathF.Max(Organ.Damage - 0.02f, 0f);
        }

        // ── Параметры тела (читает хост после Update) ──

        public float SensorGain() => MathF.Max(Loci[Locus.SensorGain], 0.1f) * Loci[Locus.GainGlobal];

        public float MotorTonic() =>
            (Loci[Locus.MotorTonic] + 0.15f * Conc[Chem.Pain] / 255.0f) * Loci[Locus.GainGlobal];

        public float Speed() => MathF.Max(Loci[Locus.Speed], 0.3f) * Loci[Locus.GainGlobal];

        public float TurnGain() => MathF.Max(Loci[Locus.TurnGain], 0.01f) * Loci[Locus.GainGlobal];

        public bool WantsFood() => Conc[Chem.Hunger] > 40.0f;

        public float Fatigue() => Conc[Chem.Fatigue];

        /// Бюджет пластичности [0.28, 1.0]: стресс глушит обучение,
        /// ацетилхолин (после еды) его открывает.
        public float PlasticityBudget()
        {
            float stress = MathF.Min(Conc[Chem.Cortisol] / 120.0f, 1.0f);
            float attention = MathF.Min(Conc[Chem.Acetylcholine] / 60.0f, 1.0f);
            return (1.0f - 0.6f * stress) * (0.7f + 0.3f * attention);
        }

        /// Заплатить глюкозой за фиксацию веса. Частичная выплата тоже
        /// считается успехом (обучение идёт, но обедняет тело).
        public bool PayForPlasticity(float dopamine)
        {
            float cost = Organ.CostPerDopamine * dopamine;
            if (Conc[Chem.Glucose] >= cost)
            {
                Conc[Chem.Glucose] -= cost;
                return true;
            }
            float paid = Conc[Chem.Glucose];
            Conc[Chem.Glucose] = 0f;
            return paid > 0f;
        }
    }
}
