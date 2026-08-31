// Существо по спецификации вида: тот же тик, что в Creature, но каналы,
// моторные пулы, актуаторы и награды берутся из SpeciesSpec, а не из кода.
//
// Тик: каналы × чувствительность крови → токи мозга → суммы пулов → тело
// (моторная работа, глюкоза, боль) → награды по правилам вида → действие.
//
// Что осталось видо-нейтральным и просто перенесено: сон, ночная
// консолидация, наследственность, эффект Болдуина. Мозг и кровь про виды не
// знают — мозг адресует регионы тегами, кровь считает один скаляр моторной
// работы независимо от числа пулов.

using System;
using System.Collections.Generic;
using Dioma.Bio;
using Dioma.Spikes;

namespace Dioma.Body
{
    public sealed class SpeciesCreature
    {
        public readonly SpeciesBinding Binding;
        public readonly DiomaBrain Brain;
        public readonly Blood Blood;
        public readonly Genome Genome;

        readonly float[] _ext;
        readonly int[] _poolStart, _poolEnd, _liveSums, _poolSum;
        readonly float[] _prevSense;
        readonly ActionFrame _action;

        public SpeciesSpec Species => Binding.Species;
        /// Суммы спайков по пулам за последний тик (диагностика и HUD).
        public IReadOnlyList<int> PoolSums => _poolSum;
        public ActionFrame LastAction => _action;

        public int FoodEaten { get; private set; }
        public int Collisions { get; private set; }
        public long LastSpikes { get; private set; }
        /// Веса генома легли на топологию мозга. Ложь у потомков Прометея:
        /// своя топология → своя длина CSR → наследство отброшено.
        public bool WeightsInherited { get; }
        public int AwakeTicks { get; private set; }
        public int SleepLeft { get; private set; }
        public int Nights { get; private set; }
        public bool Asleep => SleepLeft > 0;

        public bool DisableNightConsolidation;
        public bool DisableSleepAnchor;

        public SpeciesCreature(SpeciesBinding binding, Genome genome = null, Blood blood = null)
        {
            Binding = binding ?? throw new ArgumentNullException(nameof(binding));
            Brain = binding.Brain;
            Genome = genome ?? Genome.Newborn();
            Genome.Metabolism.ApplyTo(Blood = blood ?? Bio.Blood.Creature());
            if (blood == null)
                Blood.Conc[Chem.Hunger] = Creature.NewbornHunger;

            _ext = new float[Brain.N];
            _prevSense = new float[Species.Sense.Count];
            _action = new ActionFrame(Species.Action);

            // Живые пулы: у необязательного пула может не быть региона, и
            // тогда его сумма всегда ноль — актуатор это увидит сам.
            _poolSum = new int[Species.MotorPools.Count];
            int live = binding.LivePools.Length;
            _poolStart = new int[live];
            _poolEnd = new int[live];
            _liveSums = new int[live];
            for (int k = 0; k < live; k++)
            {
                int p = binding.LivePools[k];
                _poolStart[k] = binding.PoolStart[p];
                _poolEnd[k] = binding.PoolEnd[p];
            }

            // Наследство: выученные родителем веса поверх выращенной топологии.
            // У выращенного потомка топология своя, и длины не совпадают —
            // тогда веса теряются. Это осознанная цена морфогенеза (геном
            // тела видо-нейтрален и не знает про рёбра), но она обязана быть
            // ВИДИМОЙ: инстинкты Болдуина наследуются, веса — нет.
            WeightsInherited = Genome.Weights != null;
            if (WeightsInherited && Genome.Weights.Length == Brain.SynapseCount)
                Brain.SetCsrValues(Genome.Weights);
            // Эталон Болдуина: снимок «того, с чем родился».
            Brain.MemorySnapshot();
            foreach (var instinct in Genome.Instincts)
                for (int pass = 0; pass < Creature.BirthBurnPasses; pass++)
                    instinct.BurnInto(Brain, Creature.BasalExt, Creature.BurnWCap);
        }

        /// Один тик мира (50 мс).
        public ActionFrame Tick(SenseFrame sense)
        {
            if (sense == null) throw new ArgumentNullException(nameof(sense));
            if (sense.Channels != Species.Sense)
                throw new ArgumentException(
                    $"кадр ощущений от другого вида: ожидались каналы '{Species.Name}'");
            _action.Clear();

            if (SleepLeft > 0)
            {
                SleepLeft--;
                _action.Asleep = true;
                return _action;
            }

            // ── 1. Каналы → токи мозга (× чувствительность тела) ──
            // Полный пересчёт каждый тик: инкрементальный давал дрейф s·Δg,
            // когда гейн двигался от голода или урона.
            float gain = Blood.SensorGain();
            for (int i = 0; i < _ext.Length; i++)
                _ext[i] = Creature.BasalExt;
            for (int c = 0; c < Species.SenseChannels.Count; c++)
            {
                int region = Binding.SenseRegion[c];
                if (region < 0) continue;          // канала нет в этом мозге
                float current = sense.Values[c] * gain * Species.SenseChannels[c].Current;
                if (current == 0f) continue;
                var r = Brain.Regions[region];
                for (int i = r.Start; i < r.End; i++)
                    _ext[i] += current;
            }

            // ── 2. Мозг: суммы по всем пулам за один прогон ──
            LastSpikes = Brain.TickMotorPools(_ext, _poolStart, _poolEnd, _liveSums);
            Array.Clear(_poolSum, 0, _poolSum.Length);
            for (int k = 0; k < Binding.LivePools.Length; k++)
                _poolSum[Binding.LivePools[k]] = _liveSums[k];

            // ── 3. Тело: моторная работа и дефицит глюкозы в кровь ──
            int motorWork = 0;
            for (int p = 0; p < _poolSum.Length; p++) motorWork += _poolSum[p];
            Blood.Loci[Locus.RateMotor] = motorWork;
            Blood.Loci[Locus.GlucoseDeficit] =
                MathF.Max(Bio.Blood.GlucoseAbundant - Blood.Conc[Chem.Glucose], 0f);

            if (sense.Blocked)
                Collide();

            // Поглощение решает ТЕЛО: канал говорит «еда рядом», кровь —
            // «хочу ли». Актуатор Ingest только сообщает миру результат.
            bool ate = false;
            for (int a = 0; a < Species.Actuators.Count && !ate; a++)
            {
                if (Species.Actuators[a].Kind != ActuatorKind.Ingest) continue;
                int ch = Binding.ActuatorSense[a];
                ate = sense.Values[ch] > Species.Actuators[a].Level && Blood.WantsFood();
            }
            if (ate)
            {
                Blood.IngestFood(Creature.FoodPortion);
                FoodEaten++;
            }
            Blood.Update();

            // ── 4. Награды по правилам вида ──
            ApplyRewards(sense, ate);
            Array.Copy(sense.Values, _prevSense, _prevSense.Length);

            // ── 5. Действие: актуаторы вида ──
            for (int a = 0; a < Species.Actuators.Count; a++)
                _action.Values[a] = Evaluate(a, ate);

            // ── 6. Сон по утомлению или по счётчику бодрствования ──
            AwakeTicks++;
            if (Blood.Fatigue() >= Creature.SleepAtFatigue || AwakeTicks >= Creature.MaxAwakeTicks)
                GoToSleep();

            return _action;
        }

        /// Значение актуатора по его правилу.
        float Evaluate(int index, bool ate)
        {
            var a = Species.Actuators[index];
            var pools = Binding.ActuatorPools[index];
            float raw;
            switch (a.Kind)
            {
                case ActuatorKind.Difference:
                    raw = (_poolSum[pools[1]] - _poolSum[pools[0]]) * a.Scale;
                    break;
                case ActuatorKind.Sum:
                {
                    int sum = 0;
                    for (int p = 0; p < pools.Length; p++) sum += _poolSum[pools[p]];
                    raw = sum * a.Scale;
                    break;
                }
                case ActuatorKind.FromBody:
                    raw = BodyValue(a.Gain) * a.Scale;
                    return Math.Clamp(raw, a.Min, a.Max);   // гейн УЖЕ применён
                case ActuatorKind.Threshold:
                    return _poolSum[pools[0]] >= a.Level ? 1f : 0f;
                case ActuatorKind.Ingest:
                    return ate ? 1f : 0f;
                default:
                    throw new InvalidOperationException($"неизвестный тип актуатора {a.Kind}");
            }
            if (a.Gain != BodyGain.None)
                raw *= BodyValue(a.Gain);
            return Math.Clamp(raw, a.Min, a.Max);
        }

        /// Модулятор тела по имени: единственное место, где актуаторы видят
        /// биохимию. Новый гейн добавляется здесь, а не в каждом виде.
        float BodyValue(BodyGain gain)
        {
            switch (gain)
            {
                case BodyGain.None: return 1f;
                case BodyGain.Turn: return Blood.TurnGain();
                case BodyGain.Speed: return Blood.Speed();
                case BodyGain.MotorTonic: return Blood.MotorTonic();
                default: throw new InvalidOperationException($"неизвестный гейн {gain}");
            }
        }

        /// Награды по правилам вида. Порядок правил = порядок объявления:
        /// он влияет на очередь оплаты глюкозой, поэтому воспроизводим.
        void ApplyRewards(SenseFrame sense, bool ate)
        {
            for (int r = 0; r < Species.Rewards.Count; r++)
            {
                var rule = Species.Rewards[r];
                var chans = Binding.RewardChannels[r];
                float magnitude;

                switch (rule.Trigger)
                {
                    case RewardTrigger.Ate:
                        if (!ate) continue;
                        magnitude = 1f;
                        break;
                    case RewardTrigger.Blocked:
                        continue;   // выдаётся в Collide, до Blood.Update
                    case RewardTrigger.ChannelDropped:
                    {
                        // Максимум по каналам: «насколько близко хоть с одной
                        // стороны» — одно ощущение, а не два независимых.
                        float now = Max(sense.Values, chans);
                        float before = Max(_prevSense, chans);
                        float drop = before - now;
                        if (drop <= rule.Threshold) continue;
                        magnitude = rule.Proportional ? drop : 1f;
                        break;
                    }
                    case RewardTrigger.ChannelRose:
                    {
                        float rise = Max(sense.Values, chans) - Max(_prevSense, chans);
                        if (rise <= rule.Threshold) continue;
                        magnitude = rule.Proportional ? rise : 1f;
                        break;
                    }
                    case RewardTrigger.ChannelAbove:
                    {
                        float level = Max(sense.Values, chans);
                        if (level <= rule.Threshold) continue;
                        magnitude = rule.Proportional ? level : 1f;
                        break;
                    }
                    default:
                        throw new InvalidOperationException($"неизвестный триггер {rule.Trigger}");
                }

                float dopamine = rule.Dopamine * magnitude;
                if (rule.CostsGlucose)
                {
                    // Обучение стоит глюкозы, и бюджет зависит от кортизола
                    // и ацетилхолина: испуганный и невнимательный учится хуже.
                    if (!Blood.PayForPlasticity(rule.Dopamine))
                        continue;
                    dopamine *= Blood.PlasticityBudget();
                }
                if (rule.Delayed) Brain.ScheduleReward(dopamine);
                else Brain.RewardNow(dopamine);
            }
        }

        static float Max(float[] values, int[] indices)
        {
            float m = 0f;
            for (int i = 0; i < indices.Length; i++)
                m = MathF.Max(m, values[indices[i]]);
            return m;
        }

        /// Мир заблокировал движение: боль в кровь + наказание по горячей
        /// eligibility (LTD того пути, что привёл в стену).
        public void Collide()
        {
            Collisions++;
            Blood.BumpWall();
            for (int r = 0; r < Species.Rewards.Count; r++)
            {
                var rule = Species.Rewards[r];
                if (rule.Trigger != RewardTrigger.Blocked) continue;
                // Боль НЕ платит глюкозой: наказание должно работать и у
                // голодного, иначе умирающий не учится не биться.
                Brain.RewardNow(rule.Dopamine);
            }
        }

        void GoToSleep()
        {
            SleepLeft = Creature.SleepTicks;
            AwakeTicks = 0;
            Nights++;
            // Ночь: окно обучения закрывается, гомеостаз якорится на
            // выученном, сильные пути дня закрепляются, шум обрезается.
            // Снимок Болдуина НЕ трогается — прирост копится до размножения.
            Brain.ClearEligibility();
            if (!DisableSleepAnchor)
                Brain.SleepConsolidate(Creature.SleepAnchorAlpha);
            if (!DisableNightConsolidation)
                Brain.ConsolidateSleep(Creature.SleepStrongMin, Creature.SleepReinforce,
                    Creature.SleepPrune);
            Blood.Sleep();
        }

        // ── Наследственность ──

        /// Рефлексы, нажитые за жизнь: дуги из `Species.ReflexPairs`, где
        /// прирост веса относительно снимка рождения выше порога. Эффект
        /// Болдуина — выученное этим существом станет врождённым у потомка.
        public List<Instinct> LearnedInstincts()
        {
            var scored = new List<(float Gain, int Pre, int Post)>();
            foreach (var (preTag, postTag) in Species.ReflexPairs)
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

        /// Геном потомка: проводка рождения с дрейфом + мутация метаболизма,
        /// а нажитое сворачивается в гены инстинктов (Болдуин, не Ламарк).
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
