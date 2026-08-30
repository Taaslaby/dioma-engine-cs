// Спецификация вида: чем существо чувствует, чем действует и за что учится.
//
// Это ДАННЫЕ. Раньше всё это было записано кодом в Creature.Tick — восемь
// слотов регионов, шесть операторов инъекции, два моторных пула, три правила
// награды. Из-за этого «другое существо» означало правку движка.
//
// Вид = сенсорные каналы + моторные пулы + актуаторы + награды + рефлексы.
// Мозг и биохимия видо-нейтральны: мозг адресует регионы тегами, кровь не
// знает ни про лево-право, ни про число моторов.

using System;
using System.Collections.Generic;
using Dioma.Bio;
using Dioma.Spikes;

namespace Dioma.Body
{
    /// Сенсорный канал: что мир измеряет и куда это льётся в мозгу.
    public sealed class SenseChannel
    {
        /// Имя канала для мира: "smell.left", "wall.right", "touch.head".
        public readonly string Name;
        /// Тег региона-приёмника в коннектоме ("SL", "WL", …).
        public readonly string Region;
        /// Ток на единицу сигнала. Умножается на чувствительность крови.
        public readonly float Current;
        /// Обязателен ли регион. Необязательный канал без региона в
        /// коннектоме молча не подаётся — так вид беднее своей схемы
        /// остаётся живым (мозг без WL/WR узнаёт о стене только через боль).
        public readonly bool Required;

        public SenseChannel(string name, string region, float current, bool required = false)
        {
            Name = name;
            Region = region;
            Current = current;
            Required = required;
        }
    }

    /// Моторный пул: регион, спайки которого считаются отдельной суммой.
    /// Пулов может быть любое число — два это частный случай.
    public sealed class MotorPool
    {
        public readonly string Name;
        public readonly string Region;
        public readonly bool Required;

        public MotorPool(string name, string region, bool required = true)
        {
            Name = name;
            Region = region;
            Required = required;
        }
    }

    /// Как из моторных сумм и состояния тела получается действие.
    public enum ActuatorKind
    {
        /// Разность двух пулов: (b − a) · scale · гейн тела. Руль.
        Difference,
        /// Сумма пулов, нормированная делителем. Тяга из спайков.
        Sum,
        /// Значение из крови (тонус, скорость): тяга без участия спайков.
        FromBody,
        /// Дискретное действие при превышении порога суммой пула.
        Threshold,
        /// Поглощение: named сенсорный канал взведён И тело этого хочет.
        /// Решает ТЕЛО, а не мозг — голодный ест, сытый проходит мимо.
        Ingest,
    }

    /// Модулятор тела: какой гейн крови домножает выход актуатора.
    public enum BodyGain
    {
        None,
        /// TurnGain — маневренность.
        Turn,
        /// Speed — скорость.
        Speed,
        /// MotorTonic — моторный тонус (голодный рвётся, сытый ленится).
        MotorTonic,
    }

    /// Правило актуатора: имя выходного канала + как он считается.
    public sealed class Actuator
    {
        public readonly string Name;
        public readonly ActuatorKind Kind;
        /// Пулы-источники: два для Difference, любое число для Sum,
        /// один для Threshold, ноль для FromBody.
        public readonly string[] Pools;
        public readonly float Scale;
        public readonly BodyGain Gain;
        /// Границы выхода. Руль двусторонний (−1..1), тяга нет (0..1).
        public readonly float Min, Max;
        /// Порог для Threshold: сумма пула выше него взводит канал.
        public readonly float Level;
        /// Сенсорный канал-условие для Ingest ("food.near").
        public readonly string SenseChannel;

        Actuator(string name, ActuatorKind kind, string[] pools, float scale,
                 BodyGain gain, float min, float max, float level, string senseChannel)
        {
            Name = name;
            Kind = kind;
            Pools = pools ?? Array.Empty<string>();
            Scale = scale;
            Gain = gain;
            Min = min;
            Max = max;
            Level = level;
            SenseChannel = senseChannel;
        }

        /// Руль: асимметрия двух пулов. `(right − left) · scale · гейн`.
        public static Actuator Difference(string name, string left, string right,
            float scale, BodyGain gain = BodyGain.Turn) =>
            new Actuator(name, ActuatorKind.Difference, new[] { left, right },
                scale, gain, -1f, 1f, 0f, null);

        /// Тяга из спайков: сумма пулов × scale, ограничена 0..1.
        public static Actuator Sum(string name, string[] pools, float perSpike,
            BodyGain gain = BodyGain.Speed) =>
            new Actuator(name, ActuatorKind.Sum, pools, perSpike, gain, 0f, 1f, 0f, null);

        /// Тяга из крови: спайки не участвуют (голодный рвётся, раненый ползёт).
        public static Actuator FromBody(string name, BodyGain gain, float scale) =>
            new Actuator(name, ActuatorKind.FromBody, Array.Empty<string>(),
                scale, gain, 0f, 1f, 0f, null);

        /// Дискретное действие: сумма пула выше порога.
        public static Actuator Threshold(string name, string pool, float level) =>
            new Actuator(name, ActuatorKind.Threshold, new[] { pool },
                1f, BodyGain.None, 0f, 1f, level, null);

        /// Поглощение: канал взведён И тело хочет. Мозг сюда не влияет —
        /// решение принимает биохимия (`Blood.WantsFood`).
        public static Actuator Ingest(string name, string senseChannel) =>
            new Actuator(name, ActuatorKind.Ingest, Array.Empty<string>(),
                1f, BodyGain.None, 0f, 1f, 0.5f, senseChannel);
    }
}
