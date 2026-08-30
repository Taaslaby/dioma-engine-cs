// Fluent-сборка вида — единственная модель, к которой сводятся и код, и
// JSON-загрузчик. Правила валидации живут в SpeciesSpec, поэтому у обоих
// входов они одни.

using System.Collections.Generic;

namespace Dioma.Body
{
    public sealed class SpeciesBuilder
    {
        readonly string _name;
        readonly List<SenseChannel> _sense = new List<SenseChannel>();
        readonly List<MotorPool> _motors = new List<MotorPool>();
        readonly List<Actuator> _actuators = new List<Actuator>();
        readonly List<RewardRule> _rewards = new List<RewardRule>();
        readonly List<(string, string)> _reflexes = new List<(string, string)>();

        SpeciesBuilder(string name) => _name = name;

        public static SpeciesBuilder Create(string name) => new SpeciesBuilder(name);

        /// Сенсорный канал: имя для мира, регион-приёмник, ток на единицу.
        public SpeciesBuilder Sense(string name, string region, float current,
            bool required = false)
        {
            _sense.Add(new SenseChannel(name, region, current, required));
            return this;
        }

        /// Моторный пул: имя для актуаторов, регион-источник спайков.
        public SpeciesBuilder Motor(string name, string region, bool required = true)
        {
            _motors.Add(new MotorPool(name, region, required));
            return this;
        }

        public SpeciesBuilder Actuate(Actuator actuator)
        {
            _actuators.Add(actuator);
            return this;
        }

        public SpeciesBuilder Reward(RewardRule rule)
        {
            _rewards.Add(rule);
            return this;
        }

        /// Дуга, которую вид может свернуть в инстинкт (эффект Болдуина).
        public SpeciesBuilder Reflex(string preRegion, string postRegion)
        {
            _reflexes.Add((preRegion, postRegion));
            return this;
        }

        public SpeciesSpec Build() =>
            new SpeciesSpec(_name, _sense, _motors, _actuators, _rewards, _reflexes);
    }
}
