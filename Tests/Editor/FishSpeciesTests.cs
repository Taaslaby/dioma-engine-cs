// Рыбка — доказательство, что движок несёт ДРУГОЙ вид, а не только агента.
//
// Что она проверяет такого, чего не проверяет паритет агента:
//   - три моторных пула вместо двух (TickMotorPools);
//   - тяга из СПАЙКОВ, а не из моторного тонуса крови;
//   - своя награда новому органу — без неё хвост остался бы шумом;
//   - вид без стен и зрения (каналов меньше, чем у агента).
//
// Схема выращивается Прометеем и приходит через мост — то есть цепочка
// «раскладка → рост → JSON → мозг → вид → мир» проверяется целиком.

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Dioma.Body;
using Dioma.Spikes;
using Dioma.World;

public class FishSpeciesTests
{
    /// Рыбий мозг лежит рядом с эталонами: его пишет тот же экспортёр
    /// (`dioma/scripts/goldens.sh`), что и мозги агента.
    static DiomaBrain FishBrain()
    {
        string path = Path.Combine(TestPaths.DataDir, "dioma_fish_connectome.json");
        if (!File.Exists(path))
            Assert.Ignore($"нет коннектома рыбки ({path}) — прогони dioma/scripts/goldens.sh");
        return DiomaBrain.FromJson(File.ReadAllText(path));
    }

    [Test]
    public void Fish_Has_Three_Motor_Pools_And_No_Walls()
    {
        var species = Species.Fish();
        Assert.AreEqual(3, species.MotorPools.Count, "ML, MR и TAIL");
        Assert.IsFalse(species.Sense.Has(Species.WallLeft), "рыбка стен не чувствует");
        Assert.IsFalse(species.Sense.Has(Species.VisionLeft), "и не видит");

        // Тяга — из спайков, а не из крови: это главное отличие вида.
        var drive = species.Actuators.First(a => a.Name == Species.Drive);
        Assert.AreEqual(ActuatorKind.Sum, drive.Kind);
        Assert.AreEqual("tail", drive.Pools.Single());

        var steer = species.Actuators.First(a => a.Name == Species.Steer);
        Assert.AreEqual(ActuatorKind.Difference, steer.Kind);
    }

    [Test]
    public void Fish_Lives_And_Eats_With_Tail_Drive()
    {
        var brain = FishBrain();
        var species = Species.Fish();
        var binding = species.Bind(brain);
        var fish = new SpeciesCreature(binding);
        var arena = new SpeciesArena(fish);

        int tailPool = 2;
        int driveIdx = species.Action.Index(Species.Drive);
        double driveSum = 0, tailSum = 0;
        int awake = 0;
        for (int t = 0; t < 4000; t++)
        {
            var action = arena.Step();
            if (action.Asleep) continue;
            driveSum += action.Values[driveIdx];
            tailSum += fish.PoolSums[tailPool];
            awake++;
        }

        Debug.Log($"[fish] 4000 тиков: съедено {fish.FoodEaten}, ударов {fish.Collisions}, " +
                  $"ночей {fish.Nights}, тяга {driveSum / awake:F3}, " +
                  $"хвост {tailSum / awake:F1} спайков/тик, спайков/тик {fish.LastSpikes}");

        Assert.Greater(fish.FoodEaten, 0, "рыбка должна есть");
        Assert.Greater(fish.LastSpikes, 0, "мозг не должен замолкать");
        Assert.Greater(tailSum, 0, "хвост должен работать — иначе тяги нет вообще");
        Assert.Greater(driveSum, 0, "тяга должна приходить из спайков хвоста");
        Assert.Less(fish.Blood.Organ.Damage, 1f, "тело не должно погибнуть");
    }

    [Test]
    public void Silent_Tail_Means_No_Drive()
    {
        // Тяга рыбки целиком зависит от мозга: если хвост молчит, существо
        // стоит. У агента это не так — там тягу даёт кровь, и он ползёт даже
        // с молчащим мозгом. Тест ловит подмену одного другим.
        var brain = FishBrain();
        var species = Species.Fish();
        var fish = new SpeciesCreature(species.Bind(brain));

        var sense = new SenseFrame(species.Sense);   // все каналы нулевые
        int driveIdx = species.Action.Index(Species.Drive);

        float maxDrive = 0f;
        for (int t = 0; t < 30; t++)
        {
            var action = fish.Tick(sense);
            if (action.Asleep) continue;
            maxDrive = MathF.Max(maxDrive, action.Values[driveIdx]);
        }
        Assert.AreEqual(0, fish.PoolSums[2], "без стимула хвост не должен стрелять");
        Assert.AreEqual(0f, maxDrive, "и тяги быть не должно");
    }

    [Test]
    public void Agent_Drive_Comes_From_Blood_Not_Spikes()
    {
        // Обратная проверка того же различия: агент ползёт на моторном тонусе
        // крови даже когда сенсоры молчат. Так задумано — голод гонит вперёд.
        var brain = DiomaBrain.FromJson(File.ReadAllText(
            Path.Combine(TestPaths.DataDir, "dioma_connectome.json")));
        var species = Species.Agent();
        var agent = new SpeciesCreature(species.Bind(brain));
        var sense = new SenseFrame(species.Sense);
        int driveIdx = species.Action.Index(Species.Drive);

        float maxDrive = 0f;
        for (int t = 0; t < 30; t++)
        {
            var action = agent.Tick(sense);
            if (action.Asleep) continue;
            maxDrive = MathF.Max(maxDrive, action.Values[driveIdx]);
        }
        Assert.Greater(maxDrive, 0f, "тяга агента приходит из крови, а не из спайков");
    }
}
