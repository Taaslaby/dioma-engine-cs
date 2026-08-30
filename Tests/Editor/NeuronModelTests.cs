// Зоопарк моделей мембраны: alpha-LIF, плоский LIF, SRM0.
//
// Что здесь проверяется:
//   - модель развязана с реализацией: alpha-LIF через новый параметризованный
//     кернел даёт БИТ-В-БИТ тот же след, что раньше (иначе паритет с Rust
//     рассыпался бы, а это главный инвариант пакета);
//   - каждая модель живёт: спайкует, существо ест, тело не умирает;
//   - модели РАЗЛИЧАЮТСЯ — иначе зоопарк был бы декорацией;
//   - неизвестное имя модели падает громко, а её отсутствие значит alpha-LIF
//     (так читаются коннектомы, выпущенные до зоопарка).

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Dioma.Bio;
using Dioma.Body;
using Dioma.Spikes;
using Dioma.World;

public class NeuronModelTests
{
    static string ConnectomeJson(string prefix) =>
        File.ReadAllText(Path.Combine(TestPaths.DataDir, prefix + "_connectome.json"));

    /// Вставить поле `model` в коннектом. Ассеты не трогаем: модель — выбор
    /// вида, а не свойство файла проводки.
    static string WithModel(string json, string model)
    {
        int at = json.IndexOf("\"n\":", StringComparison.Ordinal);
        Assert.Greater(at, 0, "в коннектоме должно быть поле n");
        return json.Substring(0, at) + $"\"model\":\"{model}\", " + json.Substring(at);
    }

    [Test]
    public void Missing_Model_Field_Means_AlphaLif()
    {
        // Совместимость: все выпущенные коннектомы поля `model` не имеют.
        var brain = DiomaBrain.FromJson(ConnectomeJson("dioma"));
        Assert.AreEqual(NeuronModel.AlphaLif, brain.Model);
    }

    [TestCase("alpha-lif", NeuronModel.AlphaLif)]
    [TestCase("alphaLif", NeuronModel.AlphaLif)]
    [TestCase("lif", NeuronModel.Lif)]
    [TestCase("if", NeuronModel.Lif)]
    [TestCase("srm0", NeuronModel.Srm0)]
    [TestCase("SRM", NeuronModel.Srm0)]
    public void Model_Name_Is_Parsed(string name, NeuronModel expected)
    {
        var brain = DiomaBrain.FromJson(WithModel(ConnectomeJson("dioma"), name));
        Assert.AreEqual(expected, brain.Model);
    }

    [Test]
    public void Unknown_Model_Fails_Loudly()
    {
        // Молчаливый откат дал бы другое существо на том же файле, и это
        // выглядело бы как «мозг разучился», а не как ошибка описания.
        var ex = Assert.Throws<FormatException>(() =>
            DiomaBrain.FromJson(WithModel(ConnectomeJson("dioma"), "хопфилд")));
        Assert.That(ex.Message, Does.Contain("хопфилд"));
        Assert.That(ex.Message, Does.Contain("alpha-lif"), "ошибка должна перечислить модели");
    }

    [Test]
    public void AlphaLif_Through_Model_Layer_Is_Bit_Identical()
    {
        // Главный тест развязки: вынос alpha-LIF в INeuronModel не имел права
        // изменить ни один спайк. Сверяем с золотым следом Rust напрямую.
        var runner = GoldenRunner.Load(TestPaths.DataDir, "dioma_c");
        int exact = 0;
        foreach (var row in runner.Rows)
        {
            runner.Step();
            if (row.Ml == row.MlRef && row.Mr == row.MrRef) exact++;
        }
        Assert.AreEqual(runner.Rows.Count, exact,
            "модельный слой не имеет права менять динамику alpha-LIF");
    }

    [TestCase("alpha-lif")]
    [TestCase("lif")]
    [TestCase("srm0")]
    public void Every_Model_Lives_In_Arena(string model)
    {
        var brain = DiomaBrain.FromJson(WithModel(ConnectomeJson("dioma"), model));
        var creature = new Creature(brain);
        var arena = new TestArena(creature);

        double spikes = 0;
        int awake = 0;
        for (int t = 0; t < 1500; t++)
        {
            var action = arena.Step();
            if (action.Asleep) continue;
            spikes += creature.LastSpikes;
            awake++;
        }

        Debug.Log($"[zoo:{model}] кернел {brain.KernelName}: спайков/тик {spikes / awake:F0}, " +
                  $"съедено {creature.FoodEaten}, ударов {creature.WallHits}, " +
                  $"ночей {creature.Nights}");

        Assert.Greater(awake, 0, "существо не должно спать все 1500 тиков");
        Assert.Greater(spikes, 0, "мозг должен спайковать");
        Assert.Greater(creature.FoodEaten, 0, "существо должно есть");
        Assert.Less(creature.Blood.Organ.Damage, 1f, "тело не должно погибнуть");
        Assert.That(brain.KernelName, Does.Contain(model == "alpha-lif" ? "alpha-lif" : model));
    }

    [Test]
    public void Models_Actually_Differ()
    {
        // Если бы разные модели давали один след, зоопарк был бы декорацией.
        string json = ConnectomeJson("dioma");
        var traces = new System.Collections.Generic.Dictionary<string, long>();
        foreach (var model in new[] { "alpha-lif", "lif", "srm0" })
        {
            var brain = DiomaBrain.FromJson(WithModel(json, model));
            var ext = new float[brain.N];
            for (int i = 0; i < brain.N; i++) ext[i] = 0.12f;
            var sl = brain.Regions[brain.FindRegion("SL")];
            for (int i = sl.Start; i < sl.End; i++) ext[i] += 4f;

            long total = 0;
            for (int t = 0; t < 50; t++) total += brain.Tick(ext);
            traces[model] = total;
        }

        Debug.Log($"[zoo] спайков за 50 тиков: " +
                  string.Join(", ", traces.Select(kv => $"{kv.Key}={kv.Value}")));
        Assert.AreEqual(3, traces.Values.Distinct().Count(),
            "три модели обязаны давать три разных следа");
    }

    [Test]
    public void Cheaper_Model_Saves_Less_Than_Half()
    {
        // Мотивация «дешёвый нейрон → больше существ» работает хуже, чем
        // кажется: время съедает рассылка спайков по синапсам (на stage-C
        // 4.4 рассылки на нейронный шаг), а не арифметика мембраны. Тест
        // фиксирует это как ожидание, чтобы никто не строил планы на
        // десятикратную экономию.
        string json = ConnectomeJson("dioma_c");
        double alpha = MeasureKernel(json, "alpha-lif");
        double lif = MeasureKernel(json, "lif");

        Debug.Log($"[zoo] stage-C мембрана: alpha-lif {alpha:F3} мс, lif {lif:F3} мс " +
                  $"(экономия {(1 - lif / alpha) * 100:F0}%)");
        Assert.Less(lif, alpha, "плоский LIF обязан быть дешевле alpha-LIF");
        Assert.Greater(lif, alpha * 0.5,
            "но не вдвое: доминирует рассылка по синапсам, а не мембрана");
    }

    static double MeasureKernel(string json, string model)
    {
        var brain = DiomaBrain.FromJson(WithModel(json, model));
        var layout = brain.KernelLayout();
        using var kernel = SpikeKernels.Create(KernelMode.Managed, layout, brain.CsrValues());
        var ext = new float[layout.N];
        for (int i = 0; i < layout.N; i++) ext[i] = 0.12f;
        for (int i = layout.N / 4; i < layout.N / 3; i++) ext[i] = 5f;
        var history = new int[brain.StepsPerTick * layout.N];

        for (int t = 0; t < 5; t++) kernel.Run(ext, brain.StepsPerTick, history);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int ticks = 60;
        for (int t = 0; t < ticks; t++) kernel.Run(ext, brain.StepsPerTick, history);
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / ticks;
    }
}
