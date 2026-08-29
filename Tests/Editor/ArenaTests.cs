// Тесты тестовой арены: существо должно ЕСТЬ (мозг рулит, кровь даёт тягу)
// и не замолкать. Это интеграционный тест всех четырёх модулей.

using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Dioma.Bio;
using Dioma.Body;
using Dioma.Spikes;
using Dioma.World;

public class ArenaTests
{
    static string DataDir => TestPaths.DataDir;

    static (Creature, TestArena) Spawn(string prefix = "dioma")
    {
        var brain = DiomaBrain.FromJson(
            File.ReadAllText(Path.Combine(DataDir, prefix + "_connectome.json")));
        var creature = new Creature(brain);
        return (creature, new TestArena(creature));
    }

    [Test]
    public void DriveComesFromBloodNotSpikes()
    {
        var (creature, arena) = Spawn();
        // Локусы наполняются рецепторами только в Update(): до первого тика
        // тело держит дефолты (MotorTonic = 0.12).
        Assert.AreEqual(0.12f, creature.Blood.MotorTonic(), 1e-3f,
            "до первого тика тело на дефолтах");
        arena.Step();
        // Новорождённый голоден (HUNGER = 120) — рецептор HUNGER→MOTOR_TONIC
        // поднимает тон, и тяга становится ненулевой БЕЗ участия спайков.
        Assert.Greater(creature.Blood.MotorTonic(), 0.12f,
            "голод должен поднимать моторный тон");
        Assert.Greater(arena.LastAction.Drive, 0.2f, "тяга не должна быть нулевой");
    }

    [Test]
    public void HungryCreatureWantsFood()
    {
        var (creature, _) = Spawn();
        Assert.IsTrue(creature.Blood.WantsFood(), "новорождённый с HUNGER=120 должен хотеть есть");
    }

    [Test]
    public void SmellIsGradientNotPresence()
    {
        var (_, arena) = Spawn();
        arena.Berries.Clear();
        arena.X = 10f; arena.Y = 10f;
        arena.Berries.Add((20f, 10f));

        // Ягода строго по курсу: sin-разложение даёт почти ноль в оба канала —
        // запах говорит «в какую сторону», а не «еда есть».
        arena.Heading = 0f;
        var ahead = arena.Observe();
        Assert.Less(ahead.SmellL + ahead.SmellR, 0.05f, "прямо по курсу запаха почти нет");

        // Та же ягода сбоку — сильный сигнал. Какой именно канал загорается,
        // определяет соглашение эталона (rel ≥ 0 → правый канал); важно, что
        // асимметрия есть и она рождает поворот.
        arena.Heading = -MathF.PI / 2f;
        var side = arena.Observe();
        Assert.Greater(MathF.Max(side.SmellL, side.SmellR), 0.3f,
            "сбоку запах должен быть сильным");
        Assert.Less(MathF.Min(side.SmellL, side.SmellR), 0.05f,
            "второй канал должен молчать — это градиент");
    }

    [Test]
    public void WallProbesDetectCorner()
    {
        var (_, arena) = Spawn();
        arena.X = 0.5f; arena.Y = 0.5f; arena.Heading = MathF.PI * 1.25f; // в угол
        var s = arena.Observe();
        Assert.Greater(s.WallL, 0.5f, "стена слева должна ощущаться");
        Assert.Greater(s.WallR, 0.5f, "стена справа должна ощущаться");
    }

    [Test]
    public void CreatureEatsAndStaysAlive()
    {
        var (creature, arena) = Spawn();
        const int ticks = 4000;
        long spikesLastWindow = 0;
        for (int t = 0; t < ticks; t++)
        {
            arena.Step();
            if (t >= ticks - 500) spikesLastWindow += creature.LastSpikes;
        }
        Debug.Log($"[arena] {ticks} тиков: съедено {creature.FoodEaten}, " +
                  $"ударов {creature.WallHits}, урон {creature.Blood.Organ.Damage:F3}, " +
                  $"глюкоза {creature.Blood.Conc[Chem.Glucose]:F0}, " +
                  $"голод {creature.Blood.Conc[Chem.Hunger]:F0}, " +
                  $"спайков в последнем окне {spikesLastWindow}");

        Assert.Greater(creature.FoodEaten, 0, "существо должно съесть хотя бы одну ягоду");
        Assert.Greater(spikesLastWindow, 0, "мозг не должен замолкать");
        Assert.Less(creature.Blood.Organ.Damage, 1.0f, "тело не должно погибнуть");
    }

    [Test]
    public void CreatureSleepsWhenTired()
    {
        var (creature, arena) = Spawn();
        int sleepTicks = 0;
        for (int t = 0; t < 1000; t++)
            if (arena.Step().Asleep)
                sleepTicks++;
        // Сон обязан наступать: по утомлению или по счётчику бодрствования
        // (MaxAwakeTicks = 400) — иначе тихие особи не спят никогда.
        Assert.Greater(sleepTicks, 0, "существо должно засыпать");
    }
}
