// Правила подкрепления вида: за что существо получает дофамин.
//
// Раньше их было три и они лежали константами в Creature.Tick: еда +1,
// стена −0.5, облегчение +0.4. Из-за этого новый орган был обречён остаться
// шумом — вырастет, будет спайкать, а STDP нечем закрепить.
//
// Теперь награда — часть вида. Хвост приходит со своим смыслом, а не только
// с проводкой.

using System;

namespace Dioma.Body
{
    /// Что произошло в тике — то, на что могут смотреть правила награды.
    public enum RewardTrigger
    {
        /// Существо поело.
        Ate,
        /// Существо ударилось (мир заблокировал движение).
        Blocked,
        /// Названный сенсорный канал упал за тик (стена отодвинулась,
        /// боль ушла) — награждаем ИМЕННО тот маневр, его след ещё горячий.
        ChannelDropped,
        /// Названный сенсорный канал вырос за тик (цель приблизилась).
        ChannelRose,
        /// Названный канал выше порога прямо сейчас.
        ChannelAbove,
    }

    /// Правило «условие → дофамин».
    public sealed class RewardRule
    {
        public readonly string Name;
        public readonly RewardTrigger Trigger;
        /// Каналы для триггеров по каналам; пусто для Ate/Blocked. Несколько
        /// каналов агрегируются МАКСИМУМОМ: «насколько близко хоть с одной
        /// стороны» — одно ощущение, а не два независимых.
        public readonly string[] Channels;
        /// Величина дофамина. Отрицательная — наказание (LTD горячего пути).
        public readonly float Dopamine;
        /// Минимальная величина изменения/уровня, ниже которой правило молчит.
        public readonly float Threshold;
        /// Пропорционально ли величине изменения. Облегчение тем сильнее,
        /// чем дальше отодвинулась стена.
        public readonly bool Proportional;
        /// Платит ли обучение глюкозой. Боль не платит: наказание должно
        /// работать и у голодного, иначе умирающий не учится не биться.
        public readonly bool CostsGlucose;
        /// Отложить на rewardDelayTicks (ScheduleReward) или выдать сразу
        /// (RewardNow). Отложенная награда нужна там, где результат виден
        /// не в тот же тик.
        public readonly bool Delayed;

        public RewardRule(string name, RewardTrigger trigger, float dopamine,
            string[] channels = null, float threshold = 0f, bool proportional = false,
            bool costsGlucose = false, bool delayed = false)
        {
            Name = name;
            Trigger = trigger;
            Channels = channels ?? Array.Empty<string>();
            Dopamine = dopamine;
            Threshold = threshold;
            Proportional = proportional;
            CostsGlucose = costsGlucose;
            Delayed = delayed;
        }

        /// Награда за еду: платит глюкозой и отложена (эталон Creature).
        public static RewardRule ForEating(float dopamine) =>
            new RewardRule("eat", RewardTrigger.Ate, dopamine,
                costsGlucose: true, delayed: true);

        /// Наказание удара: сразу и без оплаты глюкозой.
        public static RewardRule ForCollision(float dopamine) =>
            new RewardRule("wall", RewardTrigger.Blocked, dopamine);

        /// Облегчение: максимум по каналам упал — награда пропорциональна
        /// падению. Награждается ИМЕННО тот маневр: его след ещё горячий.
        public static RewardRule ForRelief(float dopamine, float minDrop,
            params string[] channels) =>
            new RewardRule("relief", RewardTrigger.ChannelDropped, dopamine,
                channels, minDrop, proportional: true);

        /// Приближение к цели: максимум по каналам вырос.
        public static RewardRule ForApproach(float dopamine, float minRise,
            params string[] channels) =>
            new RewardRule("approach", RewardTrigger.ChannelRose, dopamine,
                channels, minRise, proportional: true);
    }
}
