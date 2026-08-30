// Главный тест движка видов: существо, собранное из СПЕЦИФИКАЦИИ, обязано
// вести себя ровно как захардкоженный `Creature`.
//
// Это не формальность. Спецификация переносит в данные шесть операторов
// инъекции, два моторных пула и три правила награды — если хоть один порядок
// операций сбился (например награда выдалась до Blood.Update, или каналы
// сложились в другом порядке), поведение разойдётся. В хаотичной сети
// расхождение в ulp за сотню тиков становится другой траекторией, поэтому
// допуска здесь нет: сверяем точным равенством.

using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Dioma.Bio;
using Dioma.Body;
using Dioma.Spikes;
using Dioma.World;

public class SpeciesParityTests
{
    static DiomaBrain Brain(string prefix) =>
        DiomaBrain.FromJson(File.ReadAllText(
            Path.Combine(TestPaths.DataDir, prefix + "_connectome.json")));

    [TestCase("dioma", 1200)]
    [TestCase("dioma_c", 400)]
    public void SpeciesAgent_Behaves_Exactly_Like_Creature(string prefix, int ticks)
    {
        var oldBrain = Brain(prefix);
        var oldCreature = new Creature(oldBrain);
        var oldArena = new TestArena(oldCreature);

        var newBrain = Brain(prefix);
        var species = Species.Agent();
        var newCreature = new SpeciesCreature(species.Bind(newBrain));
        var newArena = new SpeciesArena(newCreature);

        int steer = species.Action.Index(Species.Steer);
        int drive = species.Action.Index(Species.Drive);
        int smellL = species.Sense.Index(Species.SmellLeft);
        int smellR = species.Sense.Index(Species.SmellRight);
        int wallL = species.Sense.Index(Species.WallLeft);
        int wallR = species.Sense.Index(Species.WallRight);
        int visL = species.Sense.Index(Species.VisionLeft);
        int visR = species.Sense.Index(Species.VisionRight);

        for (int t = 0; t < ticks; t++)
        {
            var a1 = oldArena.Step();
            var a2 = newArena.Step();

            Assert.AreEqual(a1.Asleep, a2.Asleep, $"тик {t}: сон разошёлся");
            Assert.AreEqual(a1.Steer, a2.Values[steer], $"тик {t}: руль");
            Assert.AreEqual(a1.Drive, a2.Values[drive], $"тик {t}: тяга");
            Assert.AreEqual(a1.Eat, a2.IsSet(species.Action.Index(Species.Eat)),
                $"тик {t}: поглощение");

            var s1 = oldArena.LastSense;
            var s2 = newArena.LastSense;
            Assert.AreEqual(s1.SmellL, s2[smellL], $"тик {t}: запах слева");
            Assert.AreEqual(s1.SmellR, s2[smellR], $"тик {t}: запах справа");
            Assert.AreEqual(s1.WallL, s2[wallL], $"тик {t}: стена слева");
            Assert.AreEqual(s1.WallR, s2[wallR], $"тик {t}: стена справа");
            Assert.AreEqual(s1.VisL, s2[visL], $"тик {t}: зрение слева");
            Assert.AreEqual(s1.VisR, s2[visR], $"тик {t}: зрение справа");

            Assert.AreEqual(oldArena.X, newArena.X, $"тик {t}: X");
            Assert.AreEqual(oldArena.Y, newArena.Y, $"тик {t}: Y");
            Assert.AreEqual(oldArena.Heading, newArena.Heading, $"тик {t}: курс");
        }

        Debug.Log($"[species:{prefix}] {ticks} тиков: съедено {newCreature.FoodEaten}, " +
                  $"ударов {newCreature.Collisions}, ночей {newCreature.Nights}, " +
                  $"спайков/тик {newCreature.LastSpikes}");

        Assert.AreEqual(oldCreature.FoodEaten, newCreature.FoodEaten, "съедено");
        Assert.AreEqual(oldCreature.WallHits, newCreature.Collisions, "ударов");
        Assert.AreEqual(oldCreature.Nights, newCreature.Nights, "ночей");
        Assert.AreEqual(oldCreature.LastSpikes, newCreature.LastSpikes, "спайков");

        // Веса — бит-в-бит: обучение шло по тем же наградам в том же порядке.
        Assert.AreEqual(oldBrain.CsrValues(), newBrain.CsrValues(),
            "выученные веса должны совпасть точно");
        Assert.AreEqual(oldCreature.Blood.Conc, newCreature.Blood.Conc,
            "биохимия должна совпасть точно");
    }

    [Test]
    public void PoolSums_Match_Legacy_Motor_Sums()
    {
        // Суммы пулов у нового пути должны совпадать с LastMl/LastMr старого:
        // TickMotorPools считает те же спайки, что двухпуловый TickMotor.
        var oldBrain = Brain("dioma");
        var oldCreature = new Creature(oldBrain);
        var oldArena = new TestArena(oldCreature);

        var newBrain = Brain("dioma");
        var species = Species.Agent();
        var newCreature = new SpeciesCreature(species.Bind(newBrain));
        var newArena = new SpeciesArena(newCreature);

        for (int t = 0; t < 200; t++)
        {
            oldArena.Step();
            newArena.Step();
            if (newCreature.Asleep) continue;
            Assert.AreEqual(oldCreature.LastMl, newCreature.PoolSums[0], $"тик {t}: пул ML");
            Assert.AreEqual(oldCreature.LastMr, newCreature.PoolSums[1], $"тик {t}: пул MR");
        }
    }

    [Test]
    public void Species_Survives_Brain_Without_Optional_Regions()
    {
        // 96-нейронный мозг не имеет ни WL/WR, ни VL/VR. Вид с этими
        // каналами обязан остаться живым: необязательный канал без региона
        // просто не подаётся (мозг узнаёт о стене только через боль).
        var species = Species.Agent();
        var brain = Brain("dioma");
        var binding = species.Bind(brain);

        Assert.Less(binding.SenseRegion[species.Sense.Index(Species.WallLeft)], 0,
            "у agent-мозга нет региона WL");
        Assert.GreaterOrEqual(binding.SenseRegion[species.Sense.Index(Species.SmellLeft)], 0,
            "а SL есть");

        var creature = new SpeciesCreature(binding);
        var arena = new SpeciesArena(creature);
        for (int t = 0; t < 400; t++) arena.Step();
        Assert.Greater(creature.LastSpikes, 0, "мозг не должен замолкать");
    }

    [Test]
    public void Missing_Required_Region_Fails_Loudly()
    {
        // Обязательный канал без региона — ошибка вида, а не тихая потеря
        // сигнала: существо без обоняния выглядело бы «сломанным мозгом».
        var species = SpeciesBuilder.Create("нет-такого-региона")
            .Sense("smell.left", "НЕТ_ТАКОГО", 4f, required: true)
            .Motor("left", "ML")
            .Actuate(Actuator.FromBody("drive", BodyGain.MotorTonic, 2f))
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => species.Bind(Brain("dioma")));
        Assert.That(ex.Message, Does.Contain("НЕТ_ТАКОГО"));
    }
}
