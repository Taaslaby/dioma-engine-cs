// Вид из JSON = вид из builder'а. Если загрузчик и код разойдутся, «тот же
// вид» из файла будет вести себя иначе — самая коварная ошибка в
// data-driven движке, потому что она не падает, а тихо меняет поведение.

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Dioma.Body;
using Dioma.Spikes;
using Dioma.World;

public class SpeciesJsonTests
{
    static string FishJson() =>
        File.ReadAllText(Path.Combine(TestPaths.DataDir, "dioma_fish_species.json"));

    [Test]
    public void Json_Species_Equals_Builder_Species()
    {
        var fromCode = Species.Fish();
        var fromJson = SpeciesJson.Parse(FishJson());

        Assert.AreEqual(fromCode.Name, fromJson.Name);
        Assert.AreEqual(fromCode.Sense.Names, fromJson.Sense.Names, "каналы и их ПОРЯДОК");
        Assert.AreEqual(fromCode.Action.Names, fromJson.Action.Names, "актуаторы и их порядок");

        for (int i = 0; i < fromCode.SenseChannels.Count; i++)
        {
            var a = fromCode.SenseChannels[i];
            var b = fromJson.SenseChannels[i];
            Assert.AreEqual(a.Region, b.Region, $"канал {a.Name}: регион");
            Assert.AreEqual(a.Current, b.Current, $"канал {a.Name}: ток");
            Assert.AreEqual(a.Required, b.Required, $"канал {a.Name}: обязательность");
        }

        for (int i = 0; i < fromCode.MotorPools.Count; i++)
        {
            Assert.AreEqual(fromCode.MotorPools[i].Name, fromJson.MotorPools[i].Name);
            Assert.AreEqual(fromCode.MotorPools[i].Region, fromJson.MotorPools[i].Region);
        }

        for (int i = 0; i < fromCode.Actuators.Count; i++)
        {
            var a = fromCode.Actuators[i];
            var b = fromJson.Actuators[i];
            Assert.AreEqual(a.Kind, b.Kind, $"актуатор {a.Name}: тип");
            Assert.AreEqual(a.Pools, b.Pools, $"актуатор {a.Name}: пулы");
            Assert.AreEqual(a.Gain, b.Gain, $"актуатор {a.Name}: гейн тела");
            Assert.AreEqual(a.SenseChannel, b.SenseChannel, $"актуатор {a.Name}: канал");
            // Масштаб — float из десятичной записи, поэтому допуск в ulp'ах.
            Assert.AreEqual(a.Scale, b.Scale, 1e-7f, $"актуатор {a.Name}: масштаб");
        }

        for (int i = 0; i < fromCode.Rewards.Count; i++)
        {
            var a = fromCode.Rewards[i];
            var b = fromJson.Rewards[i];
            Assert.AreEqual(a.Name, b.Name);
            Assert.AreEqual(a.Trigger, b.Trigger, $"правило {a.Name}: триггер");
            Assert.AreEqual(a.Channels, b.Channels, $"правило {a.Name}: каналы");
            Assert.AreEqual(a.Dopamine, b.Dopamine, 1e-7f, $"правило {a.Name}: дофамин");
            Assert.AreEqual(a.Threshold, b.Threshold, 1e-7f, $"правило {a.Name}: порог");
            Assert.AreEqual(a.Proportional, b.Proportional, $"правило {a.Name}: пропорциональность");
            Assert.AreEqual(a.CostsGlucose, b.CostsGlucose, $"правило {a.Name}: оплата глюкозой");
            Assert.AreEqual(a.Delayed, b.Delayed, $"правило {a.Name}: отложенность");
            Assert.AreEqual(a.Pathed, b.Pathed, $"правило {a.Name}: путь D1/D2");
        }

        Assert.AreEqual(fromCode.ReflexPairs, fromJson.ReflexPairs);
    }

    [Test]
    public void Json_Species_Behaves_Identically()
    {
        // Главная проверка: два вида дают одну траекторию. Сравнение полей
        // могло бы пропустить, например, перепутанный порядок пулов.
        string path = Path.Combine(TestPaths.DataDir, "dioma_fish_connectome.json");
        if (!File.Exists(path))
            Assert.Ignore("нет коннектома рыбки — прогони dioma/scripts/goldens.sh");
        string connectome = File.ReadAllText(path);

        var a = new SpeciesArena(new SpeciesCreature(
            Species.Fish().Bind(DiomaBrain.FromJson(connectome))));
        var codeSpecies = a.Creature.Species;
        var b = new SpeciesArena(new SpeciesCreature(
            SpeciesJson.Parse(FishJson()).Bind(DiomaBrain.FromJson(connectome))));

        int steer = codeSpecies.Action.Index(Species.Steer);
        int drive = codeSpecies.Action.Index(Species.Drive);
        for (int t = 0; t < 600; t++)
        {
            var ra = a.Step();
            var rb = b.Step();
            Assert.AreEqual(ra.Values[steer], rb.Values[steer], $"тик {t}: руль");
            Assert.AreEqual(ra.Values[drive], rb.Values[drive], $"тик {t}: тяга");
            Assert.AreEqual(a.X, b.X, $"тик {t}: X");
            Assert.AreEqual(a.Y, b.Y, $"тик {t}: Y");
        }
        Assert.AreEqual(a.Creature.FoodEaten, b.Creature.FoodEaten);
        Debug.Log($"[species-json] 600 тиков: съедено {b.Creature.FoodEaten}, " +
                  $"вид '{b.Creature.Species.Name}' из файла ведёт себя как из кода");
    }

    [Test]
    public void Unknown_Actuator_Kind_Is_Rejected()
    {
        var ex = Assert.Throws<FormatException>(() => SpeciesJson.Parse(@"{
            ""name"": ""битый"",
            ""sense"":  [ { ""name"": ""s"", ""region"": ""SL"", ""current"": 4 } ],
            ""motors"": [ { ""name"": ""m"", ""region"": ""ML"" } ],
            ""actuators"": [ { ""name"": ""x"", ""kind"": ""телепортация"" } ]
        }"));
        Assert.That(ex.Message, Does.Contain("телепортация"));
        Assert.That(ex.Message, Does.Contain("difference"), "ошибка должна перечислить типы");
    }

    [Test]
    public void Unknown_Reward_Trigger_Is_Rejected()
    {
        var ex = Assert.Throws<FormatException>(() => SpeciesJson.Parse(@"{
            ""name"": ""битый"",
            ""sense"":  [ { ""name"": ""s"", ""region"": ""SL"", ""current"": 4 } ],
            ""motors"": [ { ""name"": ""a"", ""region"": ""ML"" }, { ""name"": ""b"", ""region"": ""MR"" } ],
            ""actuators"": [ { ""name"": ""steer"", ""kind"": ""difference"",
                               ""pools"": [""a"", ""b""], ""scale"": 0.25 } ],
            ""rewards"": [ { ""name"": ""r"", ""trigger"": ""случилось-хорошее"", ""dopamine"": 1 } ]
        }"));
        Assert.That(ex.Message, Does.Contain("случилось-хорошее"));
    }

    [Test]
    public void Json_Validation_Reuses_Spec_Rules()
    {
        // Загрузчик не дублирует правила: ошибка ссылки на несуществующий пул
        // приходит из SpeciesSpec.Validate, а не из парсера.
        var ex = Assert.Throws<ArgumentException>(() => SpeciesJson.Parse(@"{
            ""name"": ""битый"",
            ""sense"":  [ { ""name"": ""s"", ""region"": ""SL"", ""current"": 4 } ],
            ""motors"": [ { ""name"": ""a"", ""region"": ""ML"" } ],
            ""actuators"": [ { ""name"": ""steer"", ""kind"": ""difference"",
                               ""pools"": [""a"", ""нет-пула""], ""scale"": 0.25 } ]
        }"));
        Assert.That(ex.Message, Does.Contain("нет-пула"));
    }

    [Test]
    public void Single_Channel_May_Be_Written_Without_Array()
    {
        var species = SpeciesJson.Parse(@"{
            ""name"": ""один-канал"",
            ""sense"":  [ { ""name"": ""wall"", ""region"": ""SL"", ""current"": 4 } ],
            ""motors"": [ { ""name"": ""a"", ""region"": ""ML"" }, { ""name"": ""b"", ""region"": ""MR"" } ],
            ""actuators"": [ { ""name"": ""steer"", ""kind"": ""difference"",
                               ""pools"": [""a"", ""b""], ""scale"": 0.25 } ],
            ""rewards"": [ { ""name"": ""relief"", ""trigger"": ""dropped"", ""dopamine"": 0.4,
                             ""channel"": ""wall"", ""threshold"": 0.05, ""proportional"": true } ]
        }");
        Assert.AreEqual(new[] { "wall" }, species.Rewards.Single().Channels);
    }
}
