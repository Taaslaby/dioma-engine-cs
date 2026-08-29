// Тесты наследственности: потомок должен получать выученное родителем.
// Это то, что делает проект игрой про выращивание, а не демо про нейросеть.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Dioma.Bio;
using Dioma.Body;
using Dioma.Spikes;

public class HeredityTests
{
    static string DataDir => TestPaths.DataDir;

    static DiomaBrain LoadBrain(string prefix = "dioma_c") =>
        DiomaBrain.FromJson(File.ReadAllText(
            Path.Combine(DataDir, prefix + "_connectome.json")));

    /// Обучить существо на скиннер-манер: стимул слева + награда, чтобы дуга
    /// SL→ML нажила вес относительно снимка рождения.
    static Creature TrainHoming(Genome genome, int ticks = 200)
    {
        var brain = LoadBrain();
        var creature = new Creature(brain, genome);
        var sense = new Sense { SmellL = 0.9f, FoodNear = true };
        for (int t = 0; t < ticks; t++)
            creature.Tick(sense);
        return creature;
    }

    [Test]
    public void InheritedWiringDriftsFromParentBirth()
    {
        // Наследуется проводка РОЖДЕНИЯ родителя (не выученное — это был бы
        // ламаркизм). Проверяем: дрейф есть, но выученное не стирается.
        var parent = TrainHoming(Genome.Newborn(), 300);
        float[] birth = parent.Brain.BirthWeights();

        var childGenome = parent.SpawnGenome(childSeed: 1234);
        Assert.IsNotNull(childGenome.Weights, "геном потомка должен несть веса");
        Assert.AreEqual(birth.Length, childGenome.Weights.Length);

        double maxRel = 0, meanRel = 0;
        for (int i = 0; i < birth.Length; i++)
        {
            double rel = Math.Abs(childGenome.Weights[i] - birth[i]) / Math.Max(0.1, birth[i]);
            maxRel = Math.Max(maxRel, rel);
            meanRel += rel;
        }
        meanRel /= birth.Length;
        Debug.Log($"[heredity] дрейф от проводки рождения: средний {meanRel:P2}, " +
                  $"максимум {maxRel:P1}");

        Assert.Greater(maxRel, 1e-4, "веса должны дрейфовать, а не копироваться");
        Assert.Less(meanRel, 0.15, "дрейф не должен ломать анатомию");
    }

    [Test]
    public void LearnedPathBecomesChildInstinct()
    {
        // Эффект Болдуина: путь, нажитый родителем, становится ГЕНОМ ребёнка,
        // и тот отстраивает рефлекс сам при рождении.
        var parent = TrainHoming(Genome.Newborn(), 300);
        var learned = parent.LearnedInstincts();
        Debug.Log($"[baldwin] родитель свернул {learned.Count} рефлексов " +
                  $"(порог {Genome.LearnedMinGain})");
        Assert.Greater(learned.Count, 0,
            "обученный родитель должен передать хотя бы один рефлекс");

        var childGenome = parent.SpawnGenome(childSeed: 555);
        Assert.Greater(childGenome.Instincts.Count, 0, "геном потомка должен нести инстинкты");

        // Ребёнок с инстинктом рождается с более сильной дугой, чем без него.
        int sl, ml;
        var plainBrain = LoadBrain();
        sl = plainBrain.FindRegion("SL");
        ml = plainBrain.FindRegion("ML");
        var plainGenome = childGenome.Clone();
        plainGenome.Instincts.Clear();
        var withoutInstinct = new Creature(plainBrain, plainGenome);
        double plainArc = plainBrain.RegionPairWeight(sl, ml);

        var instinctBrain = LoadBrain();
        var withInstinct = new Creature(instinctBrain, childGenome);
        double instinctArc = instinctBrain.RegionPairWeight(sl, ml);

        Debug.Log($"[baldwin] дуга SL→ML у потомка: без инстинкта {plainArc:F1}, " +
                  $"с инстинктом {instinctArc:F1}");
        Assert.Greater(instinctArc, plainArc,
            "врождённый рефлекс должен дать потомку более сильную дугу");
    }

    [Test]
    public void MetabolismGenesMutate()
    {
        var parent = TrainHoming(Genome.Newborn(), 50);
        var child = parent.SpawnGenome(childSeed: 777);
        var pm = parent.Genome.Metabolism;
        var cm = child.Metabolism;
        Debug.Log($"[heredity] метаболизм: cost {pm.CostPerActivity:E3} → {cm.CostPerActivity:E3}, " +
                  $"starve {pm.StarveDamage:E3} → {cm.StarveDamage:E3}");

        Assert.AreNotEqual(pm.CostPerActivity, cm.CostPerActivity, "гены должны дрейфовать");
        Assert.Greater(cm.CostPerActivity, 0f, "гены обязаны остаться положительными");
        Assert.Greater(cm.StarveDamage, 0f);
        Assert.Greater(cm.CostPerDopamine, 0f);
        Assert.Greater(cm.HealRate, 0f);
    }

    [Test]
    public void SpawnIsDeterministic()
    {
        var parent = TrainHoming(Genome.Newborn(), 100);
        var a = parent.SpawnGenome(childSeed: 42);
        var b = parent.SpawnGenome(childSeed: 42);
        var c = parent.SpawnGenome(childSeed: 43);

        for (int i = 0; i < a.Weights.Length; i++)
            Assert.AreEqual(a.Weights[i], b.Weights[i], 0f,
                "один сид должен давать одного потомка бит-в-бит");
        Assert.AreEqual(a.Metabolism.CostPerActivity, b.Metabolism.CostPerActivity, 0f);

        bool differs = false;
        for (int i = 0; i < a.Weights.Length && !differs; i++)
            if (a.Weights[i] != c.Weights[i])
                differs = true;
        Assert.IsTrue(differs, "разные сиды должны давать разных потомков");
    }

    [Test]
    public void LearnedGainMeasuresLifeNotBirth()
    {
        // Снимок Болдуина берётся при рождении: сразу после него прирост нулевой,
        // а после жизни с наградами — положительный на обученной дуге.
        var brain = LoadBrain();
        var creature = new Creature(brain);
        int sl = brain.FindRegion("SL"), ml = brain.FindRegion("ML");
        Assert.AreEqual(0f, brain.LearnedGain(sl, ml), 1e-6f,
            "новорождённый ничего не нажил");

        var sense = new Sense { SmellL = 0.9f, FoodNear = true };
        for (int t = 0; t < 300; t++)
            creature.Tick(sense);

        float gain = brain.LearnedGain(sl, ml);
        Debug.Log($"[heredity] нажито на SL→ML за 300 тиков: {gain:F2} " +
                  $"(дрейф от снимка RMS {brain.MemoryDriftRms():F3})");
        Assert.Greater(gain, 0f, "обученная дуга должна показать прирост");
    }

    [Test]
    public void InstinctBurnStrengthensArc()
    {
        var brain = LoadBrain();
        var creature = new Creature(brain);
        int sl = brain.FindRegion("SL"), ml = brain.FindRegion("ML");
        double before = brain.RegionPairWeight(sl, ml);

        // Врождённый рефлекс «запах слева → мотор слева»: ко-активируем оба
        // конца дуги и награждаем. Прожигание идёт с потолком выше рабочего.
        var inputs = new List<(int, float)>();
        for (int n = brain.Regions[sl].Start; n < brain.Regions[sl].End; n++)
            inputs.Add((n, Genome.LearnedPreDrive));
        for (int n = brain.Regions[ml].Start; n < brain.Regions[ml].End; n++)
            inputs.Add((n, Genome.LearnedPostDrive));
        var instinct = new Instinct(inputs, dopamine: 2.0f);
        for (int pass = 0; pass < Creature.BirthBurnPasses; pass++)
            instinct.BurnInto(brain, Creature.BasalExt, Creature.BurnWCap);

        double after = brain.RegionPairWeight(sl, ml);
        Debug.Log($"[instinct] дуга SL→ML: {before:F1} → {after:F1}");
        Assert.Greater(after, before, "прожигание должно усилить дугу рефлекса");
        // Прожигание обновляет снимок, поэтому «нажитым за жизнь» не считается.
        Assert.AreEqual(0f, brain.LearnedGain(sl, ml), 1e-3f,
            "врождённое не должно выглядеть как выученное");
    }

    [Test]
    public void SleepConsolidationKeepsStrongDropsNoise()
    {
        var brain = LoadBrain();
        brain.MemorySnapshot();
        var vals = brain.CsrValues();
        // Сильная дельта и шумовая на двух синапсах.
        vals[0] += 2.0f;     // сильное выученное (> SleepStrongMin)
        vals[1] += 0.05f;    // шум
        brain.SetCsrValues(vals);

        brain.ConsolidateSleep(Creature.SleepStrongMin, Creature.SleepReinforce,
                               Creature.SleepPrune);
        var after = brain.CsrValues();
        float strongDelta = after[0] - (vals[0] - 2.0f);
        float noiseDelta = after[1] - (vals[1] - 0.05f);
        Debug.Log($"[sleep] сильная дельта 2.000 → {strongDelta:F3}, " +
                  $"шум 0.050 → {noiseDelta:F4}");

        Assert.Greater(strongDelta, 2.0f, "сильное выученное должно усилиться");
        Assert.Less(noiseDelta, 0.05f, "шум должен обрезаться к снимку");
    }
}
