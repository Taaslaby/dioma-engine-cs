// Тесты формата `.taas` и сохранения существа.
//
// Два уровня: (1) совместимость с эталонным писателем Rust — читаем файл,
// который написал `taas-format`, и сверяем числа; (2) круговой обход
// существа — сохранили, загрузили, продолжает жить с тем же опытом.

using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Dioma.Bio;
using Dioma.Body;
using Dioma.Data;
using Dioma.Spikes;

public class TaasTests
{
    static string DataDir => TestPaths.DataDir;

    static DiomaBrain LoadBrain(string prefix = "dioma_c") =>
        DiomaBrain.FromJson(File.ReadAllText(
            Path.Combine(DataDir, prefix + "_connectome.json")));

    // ── Совместимость с эталонным писателем ──

    [Test]
    public void ReadsReferenceFileFromRust()
    {
        string path = Path.Combine(DataDir, "dioma_reference.taas");
        var r = TaasReader.FromFile(path);
        var expect = Json.Obj(Json.Parse(File.ReadAllText(
            Path.Combine(DataDir, "dioma_reference_taas_expect.json"))));

        Debug.Log($"[taas] {r.Describe()}");

        Assert.AreEqual("2.0.0", r.Version);
        Assert.AreEqual(Json.Str(expect["game"]), r.Game);
        Assert.AreEqual((int)Json.Num(expect["sections"]), r.Sections.Count);

        var floats = r.F32("brain.weights");
        var expectFloats = Json.Arr(expect["floats"]);
        Assert.AreEqual(expectFloats.Count, floats.Length);
        for (int i = 0; i < floats.Length; i++)
            Assert.AreEqual(Json.F(expectFloats[i]), floats[i], 0f,
                $"float[{i}] разошёлся — вероятно порядок байтов");

        var ints = r.I32("regions");
        var expectInts = Json.Arr(expect["ints"]);
        for (int i = 0; i < ints.Length; i++)
            Assert.AreEqual((int)Json.Num(expectInts[i]), ints[i]);

        var longs = r.I64("counters");
        var expectLongs = Json.Arr(expect["longs"]);
        Assert.AreEqual(expectLongs.Count, longs.Length);
        for (int i = 0; i < longs.Length; i++)
            // Эталон отдаёт i64 строками: ±2^63 не проходят через double
            // без потери точности, а это именно те значения, что ловят
            // ошибки порядка байтов.
            Assert.AreEqual(long.Parse(Json.Str(expectLongs[i])), longs[i],
                $"long[{i}] разошёлся");

        var palette = r.U8("palette");
        Assert.AreEqual((int)Json.Num(expect["byteCount"]), palette.Length);
        for (int i = 0; i < palette.Length; i++)
            Assert.AreEqual(i, palette[i], "байтовая секция должна читаться как есть");

        Assert.AreEqual(Json.Str(expect["note"]), r.Text("note"),
            "UTF-8 с кириллицей и emoji должен выживать");

        // JSON-секция — обычный текст, парсится нашим же парсером.
        var genome = Json.Obj(Json.Parse(r.Text("genome")));
        var metabolism = Json.Obj(genome["metabolism"]);
        Assert.AreEqual(0.0015f, Json.F(metabolism["costPerActivity"]), 1e-9f);
        Assert.AreEqual(1, Json.Arr(genome["instincts"]).Count);
    }

    [Test]
    public void MetadataSurvives()
    {
        var r = TaasReader.FromFile(Path.Combine(DataDir, "dioma_reference.taas"));
        Assert.AreEqual("dioma_c", r.Metadata["connectome"]);
        Assert.AreEqual("32583", r.Metadata["synapses"]);
    }

    // ── Наш писатель ──

    [Test]
    public void WriterRoundTrip()
    {
        var floats = new[] { 1.5f, -0.25f, 0f, 3.4028235e38f };
        var bytes = new TaasWriter()
            .Game("dioma")
            .Metadata("kind", "проверка")
            .F32("w", floats)
            .I32("idx", new[] { -1, 0, 7 })
            .Str("имя", "Кит 🐋")
            .ToBytes();

        var r = new TaasReader(bytes);
        Assert.AreEqual("dioma", r.Game);
        Assert.AreEqual("проверка", r.Metadata["kind"]);
        Assert.AreEqual(floats, r.F32("w"));
        Assert.AreEqual(new[] { -1, 0, 7 }, r.I32("idx"));
        Assert.AreEqual("Кит 🐋", r.Text("имя"));
    }

    [Test]
    public void CorruptedSectionIsCaught()
    {
        var bytes = new TaasWriter().F32("w", new[] { 1f, 2f, 3f }).ToBytes();
        // Портим последний байт данных — CRC обязан это поймать. Иначе
        // повреждённое сохранение проявилось бы как «странное поведение».
        bytes[bytes.Length - 1] ^= 0xFF;
        var r = new TaasReader(bytes);
        Assert.Throws<InvalidDataException>(() => r.F32("w"),
            "порча данных должна ловиться CRC32");
    }

    [Test]
    public void BadMagicIsRejected()
    {
        var bytes = new byte[64];
        Assert.Throws<FormatException>(() => new TaasReader(bytes));
    }

    // ── Сохранение существа ──

    [Test]
    public void CreatureSurvivesSaveLoad()
    {
        var brain = LoadBrain();
        var creature = new Creature(brain);
        var sense = new Sense { SmellL = 0.9f, FoodNear = true };
        for (int t = 0; t < 200; t++)
            creature.Tick(sense);

        int sl = brain.FindRegion("SL"), ml = brain.FindRegion("ML");
        float[] weightsBefore = brain.CsrValues();
        float[] birthBefore = brain.BirthWeights();
        float gainBefore = brain.LearnedGain(sl, ml);
        float hungerBefore = creature.Blood.Conc[Chem.Hunger];
        float glucoseBefore = creature.Blood.Conc[Chem.Glucose];

        byte[] saved = CreatureSave.Save(creature, "dioma_c");
        Debug.Log($"[save] {saved.Length} байт для {brain.SynapseCount} синапсов " +
                  $"({saved.Length / 1024.0:F1} КБ)");

        var brain2 = LoadBrain();
        var restored = CreatureSave.Load(saved, brain2);

        // Веса — бит-в-бит: сохранение не имеет права «примерно» восстановить мозг.
        Assert.AreEqual(weightsBefore, brain2.CsrValues(), "выученные веса должны совпасть");
        Assert.AreEqual(birthBefore, brain2.BirthWeights(),
            "снимок Болдуина должен восстановиться — без него потомкам нечего передать");
        Assert.AreEqual(gainBefore, brain2.LearnedGain(sl, ml), 1e-3f,
            "нажитое за жизнь должно сохраниться");
        Assert.AreEqual(hungerBefore, restored.Blood.Conc[Chem.Hunger], 1e-4f,
            "тело должно продолжить с того же голода");
        Assert.AreEqual(glucoseBefore, restored.Blood.Conc[Chem.Glucose], 1e-4f);
        Assert.AreEqual(creature.Genome.Metabolism.CostPerActivity,
            restored.Genome.Metabolism.CostPerActivity, 1e-9f);
    }

    [Test]
    public void RestoredCreatureKeepsLiving()
    {
        var brain = LoadBrain();
        var creature = new Creature(brain);
        var sense = new Sense { SmellL = 0.8f, FoodNear = true };
        for (int t = 0; t < 100; t++)
            creature.Tick(sense);

        var saved = CreatureSave.Save(creature, "dioma_c");
        var brain2 = LoadBrain();
        var restored = CreatureSave.Load(saved, brain2);

        // Оба продолжают одинаково: динамика мембраны не сохраняется, но она
        // и не должна — за десяток тиков состояние сходится.
        MotorAction a = default, b = default;
        for (int t = 0; t < 50; t++)
        {
            a = creature.Tick(sense);
            b = restored.Tick(sense);
        }
        Debug.Log($"[save] после загрузки: руль {a.Steer:F3} vs {b.Steer:F3}, " +
                  $"тяга {a.Drive:F3} vs {b.Drive:F3}, спайков {creature.LastSpikes} vs {restored.LastSpikes}");

        Assert.Greater(restored.LastSpikes, 0, "восстановленный мозг должен работать");
        Assert.AreEqual(a.Drive, b.Drive, 0.05f, "тело должно вести себя так же");
    }

    [Test]
    public void SaveRejectsWrongConnectome()
    {
        var brain = LoadBrain("dioma_c");
        var creature = new Creature(brain);
        var saved = CreatureSave.Save(creature, "dioma_c");

        // Другой коннектом — другое число синапсов: веса легли бы не на те
        // связи, и существо стало бы чужим себе.
        var smallBrain = LoadBrain("dioma");
        Assert.Throws<InvalidOperationException>(
            () => CreatureSave.Load(saved, smallBrain),
            "загрузка в чужой мозг должна отклоняться");
    }

    [Test]
    public void InstinctsSurviveSaveLoad()
    {
        var brain = LoadBrain();
        int sl = brain.FindRegion("SL"), ml = brain.FindRegion("ML");
        var inputs = new System.Collections.Generic.List<(int, float)>();
        for (int n = brain.Regions[sl].Start; n < brain.Regions[sl].End; n++)
            inputs.Add((n, Genome.LearnedPreDrive));
        for (int n = brain.Regions[ml].Start; n < brain.Regions[ml].End; n++)
            inputs.Add((n, Genome.LearnedPostDrive));

        var genome = Genome.Newborn();
        genome.Instincts.Add(new Instinct(inputs, 2.5f));
        var creature = new Creature(brain, genome);

        var saved = CreatureSave.Save(creature, "dioma_c");
        var restored = CreatureSave.Load(saved, LoadBrain());

        Assert.AreEqual(1, restored.Genome.Instincts.Count, "инстинкт должен сохраниться");
        Assert.AreEqual(2.5f, restored.Genome.Instincts[0].Dopamine, 1e-6f);
        Assert.AreEqual(inputs.Count, restored.Genome.Instincts[0].Inputs.Count);
    }
}
