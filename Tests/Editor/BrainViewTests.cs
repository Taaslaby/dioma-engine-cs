// Наблюдение за мозгом для визуализации: ReadMembrane, SpikeHistory,
// LastDopamine, MembraneRange. Главное требование — чтение НЕ влияет на
// динамику: панель мониторинга не имеет права менять поведение существа.

using System.IO;
using System.Linq;
using NUnit.Framework;
using Dioma.Body;
using Dioma.Spikes;

public class BrainViewTests
{
    static DiomaBrain Brain(string prefix = "dioma") =>
        DiomaBrain.FromJson(File.ReadAllText(
            Path.Combine(TestPaths.DataDir, prefix + "_connectome.json")));

    [Test]
    public void ReadMembrane_Fills_All_Neurons()
    {
        var brain = Brain();
        var ext = new float[brain.N];
        for (int i = 0; i < brain.N; i++) ext[i] = 0.12f;
        brain.Tick(ext);

        var v = new float[brain.N];
        brain.ReadMembrane(v);

        var (vRest, vThresh, vReset) = brain.MembraneRange;
        Assert.Less(vReset, vThresh, "сброс ниже порога — иначе нейрон залипнет");
        // Мембрана обязана лежать в разумном коридоре модели, а не быть NaN.
        foreach (var value in v)
        {
            Assert.IsFalse(float.IsNaN(value), "мембрана не должна быть NaN");
            Assert.Greater(value, vReset - 50f);
            Assert.Less(value, vThresh + 50f);
        }
    }

    [Test]
    public void SpikeHistory_Matches_Reported_Spike_Count()
    {
        // История спайков — тот же буфер, что суммирует TickMotor. Их сумма
        // обязана совпасть с возвращённым числом спайков.
        var brain = Brain();
        int ml = brain.FindRegion("ML"), mr = brain.FindRegion("MR");
        var ext = new float[brain.N];
        for (int i = 0; i < brain.N; i++) ext[i] = 0.12f;
        var sl = brain.Regions[brain.FindRegion("SL")];
        for (int i = sl.Start; i < sl.End; i++) ext[i] += 4f;

        var (_, _, reported) = brain.TickMotor(ext,
            brain.Regions[ml].Start, brain.Regions[ml].End,
            brain.Regions[mr].Start, brain.Regions[mr].End);

        long fromHistory = brain.SpikeHistory.Sum(x => (long)x);
        Assert.AreEqual(reported, fromHistory,
            "сумма истории спайков должна совпасть с числом из TickMotor");
    }

    [Test]
    public void LastDopamine_Reports_Reward_And_Resets()
    {
        var brain = Brain();
        var ext = new float[brain.N];
        for (int i = 0; i < brain.N; i++) ext[i] = 0.12f;

        brain.Tick(ext);
        Assert.AreEqual(0f, brain.LastDopamine, "без награды дофамин за тик — ноль");

        brain.RewardNow(0.5f);
        Assert.AreEqual(0.5f, brain.LastDopamine, 1e-6f, "RewardNow отражается сразу");

        brain.Tick(ext);
        Assert.AreEqual(0f, brain.LastDopamine, "следующий тик обнуляет счётчик");
    }

    [Test]
    public void Observation_Does_Not_Change_Behavior()
    {
        // Мониторинг читает состояние — и не имеет права его менять. Гоняем
        // два мозга одним входом: один просто тикает, другой на каждом тике
        // ещё и вычитывает мембрану, историю, дофамин. Веса и спайки должны
        // совпасть бит-в-бит.
        var quiet = Brain("dioma_c");
        var watched = Brain("dioma_c");
        var ext = new float[quiet.N];
        for (int i = 0; i < quiet.N; i++) ext[i] = 0.12f;
        var membrane = new float[watched.N];

        for (int t = 0; t < 240; t++)
        {
            long a = quiet.Tick(ext);

            long b = watched.Tick(ext);
            watched.ReadMembrane(membrane);
            _ = watched.SpikeHistory;
            _ = watched.LastDopamine;
            _ = watched.MembraneRange;

            Assert.AreEqual(a, b, $"тик {t}: наблюдение изменило число спайков");
        }
        Assert.AreEqual(quiet.CsrValues(), watched.CsrValues(),
            "наблюдение изменило веса — оно не read-only");
    }
}
