// Вид целиком: спецификация + валидация + разрешение тегов на конкретном
// коннектоме.
//
//   var species = SpeciesBuilder.Create("agent")
//       .Sense("smell.left", "SL", 4f, required: true)
//       .Motor("left", "ML").Motor("right", "MR")
//       .Actuate(Actuator.Difference("steer", "left", "right", 0.25f))
//       .Reward(RewardRule.ForEating(1f))
//       .Reflex("SL", "ML")
//       .Build();
//
// Validate() ловит ошибки ДО первого тика: неизвестный пул у актуатора,
// дубликат имени, награда на несуществующий канал. Ошибка спецификации не
// должна выглядеть как «существо странно себя ведёт».

using System;
using System.Collections.Generic;
using System.Linq;
using Dioma.Spikes;

namespace Dioma.Body
{
    public sealed class SpeciesSpec
    {
        public readonly string Name;
        public readonly IReadOnlyList<SenseChannel> SenseChannels;
        public readonly IReadOnlyList<MotorPool> MotorPools;
        public readonly IReadOnlyList<Actuator> Actuators;
        public readonly IReadOnlyList<RewardRule> Rewards;
        /// Дуги, которые вид может сделать врождёнными (эффект Болдуина).
        public readonly IReadOnlyList<(string Pre, string Post)> ReflexPairs;

        /// Наборы имён — по ним мир и движок работают индексами.
        public readonly ChannelSet Sense;
        public readonly ChannelSet Action;

        public SpeciesSpec(string name,
            IReadOnlyList<SenseChannel> senseChannels,
            IReadOnlyList<MotorPool> motorPools,
            IReadOnlyList<Actuator> actuators,
            IReadOnlyList<RewardRule> rewards,
            IReadOnlyList<(string Pre, string Post)> reflexPairs)
        {
            Name = name;
            SenseChannels = senseChannels ?? Array.Empty<SenseChannel>();
            MotorPools = motorPools ?? Array.Empty<MotorPool>();
            Actuators = actuators ?? Array.Empty<Actuator>();
            Rewards = rewards ?? Array.Empty<RewardRule>();
            ReflexPairs = reflexPairs ?? Array.Empty<(string, string)>();

            Validate();
            Sense = new ChannelSet(SenseChannels.Select(c => c.Name).ToList());
            Action = new ChannelSet(Actuators.Select(a => a.Name).ToList());
        }

        /// Проверка спецификации: всё, что можно поймать без коннектома.
        public void Validate()
        {
            if (string.IsNullOrEmpty(Name))
                throw new ArgumentException("вид без имени");
            if (MotorPools.Count == 0)
                throw new ArgumentException($"вид '{Name}': нет ни одного моторного пула");
            if (Actuators.Count == 0)
                throw new ArgumentException($"вид '{Name}': нет ни одного актуатора — " +
                    "существо не сможет ничего сделать миром");

            // Дубликаты имён: ChannelSet тоже их поймает, но здесь ошибка
            // назовёт вид и вид ошибки.
            Unique(SenseChannels.Select(c => c.Name), "сенсорного канала");
            Unique(MotorPools.Select(p => p.Name), "моторного пула");
            Unique(Actuators.Select(a => a.Name), "актуатора");
            Unique(Rewards.Select(r => r.Name), "правила награды");

            var pools = new HashSet<string>(MotorPools.Select(p => p.Name), StringComparer.Ordinal);
            var channels = new HashSet<string>(SenseChannels.Select(c => c.Name), StringComparer.Ordinal);

            foreach (var a in Actuators)
            {
                foreach (var pool in a.Pools)
                    if (!pools.Contains(pool))
                        throw new ArgumentException(
                            $"вид '{Name}': актуатор '{a.Name}' ссылается на пул '{pool}', " +
                            $"которого нет; есть: {string.Join(", ", pools)}");

                int need = a.Kind == ActuatorKind.Difference ? 2
                         : a.Kind == ActuatorKind.Threshold ? 1
                         : a.Kind == ActuatorKind.FromBody ? 0
                         : a.Kind == ActuatorKind.Ingest ? 0
                         : -1;   // Sum: любое положительное число
                if (need >= 0 && a.Pools.Length != need)
                    throw new ArgumentException(
                        $"вид '{Name}': актуатор '{a.Name}' типа {a.Kind} требует " +
                        $"{need} пул(ов), передано {a.Pools.Length}");
                if (a.Kind == ActuatorKind.Sum && a.Pools.Length == 0)
                    throw new ArgumentException(
                        $"вид '{Name}': актуатор '{a.Name}' типа Sum без пулов");
                if (a.Kind == ActuatorKind.Difference && a.Pools[0] == a.Pools[1])
                    throw new ArgumentException(
                        $"вид '{Name}': актуатор '{a.Name}' вычитает пул '{a.Pools[0]}' " +
                        "из самого себя — выход всегда ноль");
                if (a.Kind == ActuatorKind.Ingest)
                {
                    if (a.SenseChannel == null)
                        throw new ArgumentException(
                            $"вид '{Name}': актуатор '{a.Name}' типа Ingest без сенсорного канала");
                    if (!channels.Contains(a.SenseChannel))
                        throw new ArgumentException(
                            $"вид '{Name}': актуатор '{a.Name}' смотрит на канал " +
                            $"'{a.SenseChannel}', которого нет; есть: {string.Join(", ", channels)}");
                }
            }

            foreach (var r in Rewards)
            {
                bool needsChannel = r.Trigger == RewardTrigger.ChannelDropped
                                 || r.Trigger == RewardTrigger.ChannelRose
                                 || r.Trigger == RewardTrigger.ChannelAbove;
                if (needsChannel)
                {
                    if (r.Channels.Length == 0)
                        throw new ArgumentException(
                            $"вид '{Name}': правило '{r.Name}' типа {r.Trigger} без каналов");
                    foreach (var ch in r.Channels)
                        if (!channels.Contains(ch))
                            throw new ArgumentException(
                                $"вид '{Name}': правило '{r.Name}' смотрит на канал " +
                                $"'{ch}', которого нет; есть: {string.Join(", ", channels)}");
                }
                else if (r.Channels.Length > 0)
                {
                    throw new ArgumentException(
                        $"вид '{Name}': правило '{r.Name}' типа {r.Trigger} не смотрит " +
                        $"на каналы, но каналы заданы: {string.Join(", ", r.Channels)}");
                }
            }
        }

        static void Unique(IEnumerable<string> names, string what)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in names)
                if (!seen.Add(n))
                    throw new ArgumentException($"дубликат имени {what}: '{n}'");
        }

        /// Разрешить теги на конкретном мозге. Обязательный регион,
        /// которого нет, — ошибка вида, а не тихая потеря канала.
        public SpeciesBinding Bind(DiomaBrain brain) => new SpeciesBinding(this, brain);
    }

    /// Вид, привязанный к мозгу: теги разрешены в индексы регионов один раз.
    public sealed class SpeciesBinding
    {
        public readonly SpeciesSpec Species;
        public readonly DiomaBrain Brain;

        /// Индекс региона на канал; −1 = канала в этом мозге нет.
        public readonly int[] SenseRegion;
        /// Индекс региона на пул; −1 = пула нет (необязательный).
        public readonly int[] MotorRegion;
        /// Границы пулов для TickMotor: [start, end) подряд.
        public readonly int[] PoolStart, PoolEnd;
        /// Индексы пулов, у которых есть регион (остальные дают 0).
        public readonly int[] LivePools;
        /// Индекс пула по имени, для актуаторов.
        public readonly int[][] ActuatorPools;
        /// Индекс сенсорного канала-условия на актуатор (Ingest); иначе −1.
        public readonly int[] ActuatorSense;

        /// Индексы правил награды: каналы, на которые смотрит правило (пусто,
        /// если правило не про каналы). Разрешаются здесь, не в тике.
        public readonly int[][] RewardChannels;

        public SpeciesBinding(SpeciesSpec species, DiomaBrain brain)
        {
            Species = species ?? throw new ArgumentNullException(nameof(species));
            Brain = brain ?? throw new ArgumentNullException(nameof(brain));

            int nc = species.SenseChannels.Count;
            SenseRegion = new int[nc];
            for (int i = 0; i < nc; i++)
            {
                var ch = species.SenseChannels[i];
                int r = brain.FindRegion(ch.Region);
                if (r < 0 && ch.Required)
                    throw new InvalidOperationException(
                        $"вид '{species.Name}': каналу '{ch.Name}' нужен регион " +
                        $"'{ch.Region}', в коннектоме его нет");
                SenseRegion[i] = r;
            }

            int np = species.MotorPools.Count;
            MotorRegion = new int[np];
            PoolStart = new int[np];
            PoolEnd = new int[np];
            var live = new List<int>(np);
            for (int i = 0; i < np; i++)
            {
                var pool = species.MotorPools[i];
                int r = brain.FindRegion(pool.Region);
                if (r < 0)
                {
                    if (pool.Required)
                        throw new InvalidOperationException(
                            $"вид '{species.Name}': пулу '{pool.Name}' нужен регион " +
                            $"'{pool.Region}', в коннектоме его нет");
                    MotorRegion[i] = -1;
                    continue;
                }
                MotorRegion[i] = r;
                PoolStart[i] = brain.Regions[r].Start;
                PoolEnd[i] = brain.Regions[r].End;
                live.Add(i);
            }
            LivePools = live.ToArray();

            // Актуаторы: имена пулов и каналов → индексы, тоже один раз.
            ActuatorPools = new int[species.Actuators.Count][];
            ActuatorSense = new int[species.Actuators.Count];
            for (int a = 0; a < species.Actuators.Count; a++)
            {
                var act = species.Actuators[a];
                var idx = new int[act.Pools.Length];
                for (int p = 0; p < act.Pools.Length; p++)
                    idx[p] = IndexOfPool(species, act.Pools[p]);
                ActuatorPools[a] = idx;
                ActuatorSense[a] = act.SenseChannel != null
                    ? species.Sense.Index(act.SenseChannel)
                    : -1;
            }

            RewardChannels = new int[species.Rewards.Count][];
            for (int r = 0; r < species.Rewards.Count; r++)
            {
                var chans = species.Rewards[r].Channels;
                var idx = new int[chans.Length];
                for (int c = 0; c < chans.Length; c++)
                    idx[c] = species.Sense.Index(chans[c]);
                RewardChannels[r] = idx;
            }
        }

        static int IndexOfPool(SpeciesSpec species, string name)
        {
            for (int i = 0; i < species.MotorPools.Count; i++)
                if (species.MotorPools[i].Name == name)
                    return i;
            throw new ArgumentException($"нет пула '{name}'");   // Validate уже проверил
        }

        public SenseFrame NewSenseFrame() => new SenseFrame(Species.Sense);
        public ActionFrame NewActionFrame() => new ActionFrame(Species.Action);
    }
}
