// Сторож численной устойчивости: ночная консолидация не должна уводить
// веса в минус или разгонять их без границ.
//
// Вес синапса — магнитуда: знак связи несёт нейротрансмиттер пресинапса
// (`nt` = ±1). Отрицательный вес переворачивает знак — возбуждающий синапс
// начинает тормозить. Rust-эталон полов и потолков в `consolidate_sleep`
// не имеет, и это его баг: за 60 ночей веса уезжают до −4.9 млн, суммарный
// вес мозга 180 тыс. → −20 млн (воспроизведено на обеих реализациях).

using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Dioma.Body;
using Dioma.Spikes;
using Dioma.World;

public class StabilityTests
{
    static string DataDir => TestPaths.DataDir;

    static DiomaBrain LoadBrain(string prefix = "dioma_c") =>
        DiomaBrain.FromJson(File.ReadAllText(
            Path.Combine(DataDir, prefix + "_connectome.json")));

    /// Прогнать N «ночей»: активность → награда → ночная обработка.
    static void RunNights(DiomaBrain brain, int nights)
    {
        var ext = new float[brain.N];
        for (int i = 0; i < brain.N; i++) ext[i] = Creature.BasalExt;
        var sl = brain.Regions[brain.FindRegion("SL")];
        for (int i = sl.Start; i < sl.End; i++) ext[i] += 18f;
        brain.MemorySnapshot();

        for (int n = 0; n < nights; n++)
        {
            for (int t = 0; t < 20; t++) brain.Tick(ext);
            brain.RewardNow(1.0f);
            brain.ClearEligibility();
            brain.SleepConsolidate(Creature.SleepAnchorAlpha);
            brain.ConsolidateSleep(Creature.SleepStrongMin, Creature.SleepReinforce,
                                   Creature.SleepPrune);
        }
    }

    [Test]
    public void NightConsolidationKeepsWeightsNonNegative()
    {
        using var brain = LoadBrain();
        float birthMax = 0;
        foreach (float w in brain.BirthWeights()) birthMax = MathF.Max(birthMax, w);

        RunNights(brain, 60);

        var v = brain.CsrValues();
        float min = float.MaxValue, max = 0;
        int negative = 0;
        foreach (float w in v)
        {
            if (w < 0f) negative++;
            min = MathF.Min(min, w);
            max = MathF.Max(max, w);
        }
        Debug.Log($"[stability] 60 ночей: вес ∈ [{min:F2}, {max:F0}], отрицательных {negative}, " +
                  $"wsum={brain.WeightSum():F0} (врождённый максимум {birthMax:F0})");

        Assert.AreEqual(0, negative,
            "вес — магнитуда: отрицательный перевернул бы знак синапса");
        Assert.Greater(brain.WeightSum(), 0.0, "суммарный вес мозга не может быть отрицательным");
    }

    [Test]
    public void NightConsolidationDoesNotInflateWeights()
    {
        using var brain = LoadBrain();
        double wsumBefore = brain.WeightSum();
        float birthMax = 0;
        foreach (float w in brain.BirthWeights()) birthMax = MathF.Max(birthMax, w);

        RunNights(brain, 60);

        float max = 0;
        foreach (float w in brain.CsrValues()) max = MathF.Max(max, w);
        double growth = brain.WeightSum() / wsumBefore;
        Debug.Log($"[stability] рост суммарного веса за 60 ночей: ×{growth:F2}, " +
                  $"max вес {max:F0} (врождённый {birthMax:F0})");

        // Потолок консолидации — max(w_max, врождённый вес синапса), поэтому
        // отдельный вес не может превысить самый тяжёлый врождённый хвост.
        Assert.LessOrEqual(max, birthMax * 1.01f,
            "консолидация не должна разгонять вес выше врождённого хвоста");
        Assert.Less(growth, 10.0,
            "суммарный вес не должен расти на порядки — это спираль, а не обучение");
    }

    [Test]
    public void ArenaCreatureStaysNumericallySane()
    {
        // Интеграционный сторож: в живой арене (награды, боль, сон) веса
        // остаются в разумных границах на длинной дистанции.
        using var brain = LoadBrain();
        var creature = new Creature(brain);
        var arena = new TestArena(creature);
        double wsumBefore = brain.WeightSum();
        for (int t = 0; t < 2000; t++)
            arena.Step();

        float min = float.MaxValue, max = 0;
        foreach (float w in brain.CsrValues())
        {
            min = MathF.Min(min, w);
            max = MathF.Max(max, w);
        }
        double growth = brain.WeightSum() / wsumBefore;
        Debug.Log($"[stability] арена 2000 тиков: вес ∈ [{min:F2}, {max:F0}], " +
                  $"рост суммы ×{growth:F1}, съедено {creature.FoodEaten}, ночей {creature.Nights}");

        Assert.GreaterOrEqual(min, 0f, "отрицательных весов быть не должно");
        Assert.Less(growth, 50.0, "суммарный вес не должен расти на порядки");
        Assert.IsFalse(float.IsNaN(max) || float.IsInfinity(max), "вес ушёл в NaN/∞");
    }
}
