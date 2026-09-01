// Готовые виды. Это ДАННЫЕ, записанные кодом — тот же объект собирает
// JSON-загрузчик. Добавление вида не требует правок в движке.
//
// Agent — нынешнее существо dioma, переписанное спецификацией: те же
// каналы, токи, пулы и награды. Он существует, чтобы доказать, что
// абстракция ничего не потеряла (см. SpeciesParityTests).

namespace Dioma.Body
{
    public static class Species
    {
        // Имена каналов — контракт с миром. Точка в имени группирует
        // модальность: мир может заполнять "smell.*" одним проходом.
        public const string SmellLeft = "smell.left";
        public const string SmellRight = "smell.right";
        public const string WallLeft = "wall.left";
        public const string WallRight = "wall.right";
        public const string VisionLeft = "vision.left";
        public const string VisionRight = "vision.right";
        public const string FoodNear = "food.near";

        public const string Steer = "steer";
        public const string Drive = "drive";
        public const string Eat = "eat";
        /// Нынешнее существо dioma: обоняние, стены, зрение; руль из
        /// асимметрии ML/MR, тяга из моторного тонуса крови.
        ///
        /// Стены и зрение НЕ обязательны: мозг без WL/WR остаётся живым и
        /// узнаёт о стене только через боль удара. Лить стену в обоняние
        /// нельзя — это ложный запах, существо рулило бы В стену (замерено:
        /// 974 удара на 1000 тиков).
        public static SpeciesSpec Agent() => SpeciesBuilder.Create("agent")
            .Sense(SmellLeft, "SL", Creature.SmellCurrent, required: true)
            .Sense(SmellRight, "SR", Creature.SmellCurrent, required: true)
            .Sense(WallLeft, "WL", Creature.WallCurrent)
            .Sense(WallRight, "WR", Creature.WallCurrent)
            .Sense(VisionLeft, "VL", Creature.VisionCurrent)
            .Sense(VisionRight, "VR", Creature.VisionCurrent)
            // Канал без региона: мир сообщает «еда в радиусе укуса», в мозг
            // это не подаётся — решение принимает тело.
            .Sense(FoodNear, null, 0f)
            .Motor("left", "ML")
            .Motor("right", "MR")
            .Actuate(Actuator.Difference(Steer, "left", "right", Creature.SteerScale))
            // Тяга из КРОВИ, а не из спайков: голодный рвётся, сытый ленится,
            // раненый едва ползёт. Мозг определяет только поворот.
            // `Speed()` мир домножает сам, поэтому здесь только тонус.
            .Actuate(Actuator.FromBody(Drive, BodyGain.MotorTonic,
                1f / Creature.TonicDriveNorm))
            .Actuate(Actuator.Ingest(Eat, FoodNear))
            .Reward(RewardRule.ForEating(Creature.EatDopamine))
            .Reward(RewardRule.ForCollision(Creature.WallDopamine))
            // Облегчение: стена отодвинулась — награждаем ИМЕННО тот отворот,
            // его eligibility ещё горячая. Максимум по двум стенным каналам:
            // «насколько близко хоть с одной стороны» — одно ощущение.
            .Reward(RewardRule.ForRelief(Creature.ReliefDopamine, Creature.ReliefMin,
                WallLeft, WallRight))
            .Reflex("SL", "ML")
            .Reflex("SR", "MR")
            .Reflex("WL", "MR")
            .Reflex("WR", "ML")
            .Build();

        /// Рыбка — ДРУГОЙ ВИД, а не стадия агента. Три моторных пула вместо
        /// двух: ML/MR рулят, TAIL даёт тягу СПАЙКАМИ. Ни строки движка под
        /// неё не правилось — только эти данные и раскладка в Прометее.
        ///
        /// Ключевое отличие от агента: тяга приходит из мозга, а кровь лишь
        /// модулирует её (`Speed()` домножает шаг в мире). Значит хвост можно
        /// НАУЧИТЬ — у него своя награда, «приблизился к еде».
        public static SpeciesSpec Fish() => SpeciesBuilder.Create("fish")
            .Sense(SmellLeft, "SL", Creature.SmellCurrent, required: true)
            .Sense(SmellRight, "SR", Creature.SmellCurrent, required: true)
            .Sense(FoodNear, null, 0f)
            .Motor("left", "ML")
            .Motor("right", "MR")
            .Motor("tail", "TAIL")
            .Actuate(Actuator.Difference(Steer, "left", "right", Creature.SteerScale))
            // Тяга из спайков хвоста. Делитель — 1/600, и он НЕ подогнан «на
            // глаз»: замер по шести значениям (1/40…1/600) показал, что мелкий
            // делитель даёт мгновенную сатурацию тяги (0.15 средней при 4.5
            // спайках/тик) — рыбка врубает полный газ на любой шорох и живёт в
            // стене: 792 удара против 48. Крупный делитель оставляет тягу
            // пропорциональной, и хвост становится управляемым: 22 съеденных
            // против 7. Порог 0.5 в actuator'е тут не при чём — тяга
            // непрерывная, важен именно масштаб.
            .Actuate(Actuator.Sum(Drive, new[] { "tail" }, 1f / 600f))
            .Actuate(Actuator.Ingest(Eat, FoodNear))
            .Reward(RewardRule.ForEating(Creature.EatDopamine))
            // Стена — через КОСВЕННЫЙ путь (D2): строит «не плыви в стену»,
            // не стирая «плыви к еде». A/B-замер в EvolveSmoke держит.
            .Reward(RewardRule.ForCollisionPathed(Creature.WallDopamine))
            // Награда хвосту: запах усилился — значит подплыли. Без неё новый
            // орган остался бы шумом, который STDP нечем закрепить.
            .Reward(RewardRule.ForApproach(0.3f, 0.02f, SmellLeft, SmellRight))
            .Reflex("SL", "ML")
            .Reflex("SR", "MR")
            .Reflex("SL", "TAIL")
            .Reflex("SR", "TAIL")
            .Build();
    }
}
