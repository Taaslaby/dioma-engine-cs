// Валидация спецификации вида: ошибка описания должна выглядеть как ошибка
// описания, а не как «существо странно себя ведёт».
//
// Каждый тест здесь — класс ошибки, который иначе проявился бы через сотни
// тиков молчаливой неправильности.

using System;
using NUnit.Framework;
using Dioma.Body;

public class SpeciesSpecTests
{
    static SpeciesBuilder Minimal(string name = "тест") =>
        SpeciesBuilder.Create(name)
            .Sense("smell.left", "SL", 4f)
            .Motor("left", "ML")
            .Motor("right", "MR");

    [Test]
    public void Actuator_On_Unknown_Pool_Is_Rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Minimal().Actuate(Actuator.Difference("steer", "left", "хвост", 0.25f)).Build());
        Assert.That(ex.Message, Does.Contain("хвост"));
        Assert.That(ex.Message, Does.Contain("left"), "ошибка должна перечислить доступные пулы");
    }

    [Test]
    public void Reward_On_Unknown_Channel_Is_Rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Minimal()
                .Actuate(Actuator.Difference("steer", "left", "right", 0.25f))
                .Reward(RewardRule.ForRelief(0.4f, 0.05f, "нет.канала"))
                .Build());
        Assert.That(ex.Message, Does.Contain("нет.канала"));
    }

    [Test]
    public void Difference_Of_Pool_With_Itself_Is_Rejected()
    {
        // Выход всегда ноль — существо не смогло бы повернуть, и это
        // выглядело бы как «мозг не учится рулить».
        var ex = Assert.Throws<ArgumentException>(() =>
            Minimal().Actuate(Actuator.Difference("steer", "left", "left", 0.25f)).Build());
        Assert.That(ex.Message, Does.Contain("самого себя"));
    }

    [Test]
    public void Duplicate_Channel_Name_Is_Rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SpeciesBuilder.Create("дубль")
                .Sense("smell.left", "SL", 4f)
                .Sense("smell.left", "SR", 4f)
                .Motor("left", "ML")
                .Actuate(Actuator.FromBody("drive", BodyGain.MotorTonic, 2f))
                .Build());
        Assert.That(ex.Message, Does.Contain("smell.left"));
    }

    [Test]
    public void Species_Without_Actuators_Is_Rejected()
    {
        // Существо, которое ничего не может сделать миром, — не вид.
        var ex = Assert.Throws<ArgumentException>(() => Minimal().Build());
        Assert.That(ex.Message, Does.Contain("актуатора"));
    }

    [Test]
    public void Species_Without_Motor_Pools_Is_Rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SpeciesBuilder.Create("без-моторов")
                .Sense("smell.left", "SL", 4f)
                .Actuate(Actuator.FromBody("drive", BodyGain.MotorTonic, 2f))
                .Build());
        Assert.That(ex.Message, Does.Contain("моторного пула"));
    }

    [Test]
    public void Ingest_On_Unknown_Channel_Is_Rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Minimal()
                .Actuate(Actuator.Difference("steer", "left", "right", 0.25f))
                .Actuate(Actuator.Ingest("eat", "нет.еды"))
                .Build());
        Assert.That(ex.Message, Does.Contain("нет.еды"));
    }

    [Test]
    public void Channel_Trigger_Without_Channel_Is_Rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Minimal()
                .Actuate(Actuator.Difference("steer", "left", "right", 0.25f))
                .Reward(new RewardRule("голое", RewardTrigger.ChannelDropped, 0.4f))
                .Build());
        Assert.That(ex.Message, Does.Contain("без каналов"));
    }

    [Test]
    public void Channel_Lookup_Is_By_Index_And_Names_Are_Ordered()
    {
        var species = Species.Agent();
        // Порядок объявления = порядок индексов: это часть контракта, по нему
        // мир и сохранения раскладывают массивы.
        Assert.AreEqual(0, species.Sense.Index(Species.SmellLeft));
        Assert.AreEqual(1, species.Sense.Index(Species.SmellRight));
        Assert.AreEqual(Species.SmellLeft, species.Sense.NameOf(0));
        Assert.AreEqual(-1, species.Sense.IndexOf("нет.такого"),
            "IndexOf на неизвестном канале даёт −1, а не исключение");

        var ex = Assert.Throws<ArgumentException>(() => species.Sense.Index("нет.такого"));
        Assert.That(ex.Message, Does.Contain(Species.SmellLeft),
            "ошибка должна перечислить доступные каналы");
    }

    [Test]
    public void Agent_Species_Declares_What_Creature_Hardcoded()
    {
        // Спецификация должна точно повторять то, что было в коде Creature:
        // если кто-то поменяет ток или награду в одном месте, тест поймает.
        var s = Species.Agent();
        Assert.AreEqual(7, s.Sense.Count, "6 аналоговых каналов + еда рядом");
        Assert.AreEqual(2, s.MotorPools.Count);
        Assert.AreEqual(3, s.Actuators.Count, "руль, тяга, поглощение");
        Assert.AreEqual(3, s.Rewards.Count, "еда, удар, облегчение");
        Assert.AreEqual(4, s.ReflexPairs.Count, "SL→ML, SR→MR, WL→MR, WR→ML");

        Assert.AreEqual(Creature.SmellCurrent, s.SenseChannels[0].Current);
        Assert.IsTrue(s.SenseChannels[0].Required, "обоняние обязательно");
        Assert.IsFalse(s.SenseChannels[2].Required, "стены — нет");
    }
}
