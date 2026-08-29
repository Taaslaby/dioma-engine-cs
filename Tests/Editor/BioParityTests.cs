// Паритет биохимии с Rust-эталоном: реплей того же сценария, что писал
// `export_bio.rs`, и сверка всех химий/локусов/производных параметров.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Dioma.Bio;
using Dioma.Spikes;

public class BioParityTests
{
    static string DataDir => TestPaths.DataDir;

    sealed class Row
    {
        public float RateMotor, Pay;
        public bool Ate, Wall, Sleep, Paid, WantsFood;
        public float[] Conc, Loci;
        public float Damage, SensorGain, MotorTonic, Speed, TurnGain, Budget;
    }

    static List<Row> LoadGolden()
    {
        var root = Json.Obj(Json.Parse(
            File.ReadAllText(Path.Combine(DataDir, "dioma_bio_golden.json"))));
        var rows = new List<Row>();
        foreach (var t in Json.Arr(root["ticks"]))
        {
            var o = Json.Obj(t);
            var conc = Json.Arr(o["conc"]);
            var loci = Json.Arr(o["loci"]);
            var r = new Row
            {
                RateMotor = Json.F(o["rateMotor"]),
                Pay = Json.F(o["pay"]),
                Ate = Json.Bool(o["ate"]),
                Wall = Json.Bool(o["wall"]),
                Sleep = Json.Bool(o["sleep"]),
                Paid = Json.Bool(o["paid"]),
                WantsFood = Json.Bool(o["wantsFood"]),
                Damage = Json.F(o["damage"]),
                SensorGain = Json.F(o["sensorGain"]),
                MotorTonic = Json.F(o["motorTonic"]),
                Speed = Json.F(o["speed"]),
                TurnGain = Json.F(o["turnGain"]),
                Budget = Json.F(o["budget"]),
                Conc = new float[conc.Count],
                Loci = new float[loci.Count],
            };
            for (int i = 0; i < conc.Count; i++) r.Conc[i] = Json.F(conc[i]);
            for (int i = 0; i < loci.Count; i++) r.Loci[i] = Json.F(loci[i]);
            rows.Add(r);
        }
        return rows;
    }

    [Test]
    public void Parity_BioGoldenTrace()
    {
        var golden = LoadGolden();
        Assert.Greater(golden.Count, 0, "след биохимии пуст");

        var blood = Blood.Creature();
        blood.Conc[Chem.Hunger] = 120.0f; // ARENA_NEWBORN_HUNGER, как в эталоне

        double maxConcErr = 0, maxLociErr = 0, maxParamErr = 0;
        int firstMismatch = -1, badPaid = 0, badWants = 0;
        string[] chemNames =
        {
            "глюкоза", "еда", "голод", "боль", "дофамин",
            "усталость", "серотонин", "кортизол", "ACh",
        };
        int worstChem = -1;

        for (int t = 0; t < golden.Count; t++)
        {
            var g = golden[t];

            blood.Loci[Locus.RateMotor] = g.RateMotor;
            blood.Loci[Locus.GlucoseDeficit] =
                MathF.Max(Blood.GlucoseAbundant - blood.Conc[Chem.Glucose], 0f);
            if (g.Wall) blood.BumpWall();
            if (g.Ate) blood.IngestFood(60.0f);
            blood.Update();
            bool paid = g.Pay > 0f && blood.PayForPlasticity(g.Pay);
            if (g.Sleep) blood.Sleep();

            for (int i = 0; i < Chem.N; i++)
            {
                double err = Math.Abs(blood.Conc[i] - g.Conc[i]) / Math.Max(1.0, Math.Abs(g.Conc[i]));
                if (err > maxConcErr) { maxConcErr = err; worstChem = i; }
            }
            for (int i = 0; i < Locus.N; i++)
                maxLociErr = Math.Max(maxLociErr,
                    Math.Abs(blood.Loci[i] - g.Loci[i]) / Math.Max(1.0, Math.Abs(g.Loci[i])));
            maxParamErr = Math.Max(maxParamErr, Math.Abs(blood.SensorGain() - g.SensorGain));
            maxParamErr = Math.Max(maxParamErr, Math.Abs(blood.MotorTonic() - g.MotorTonic));
            maxParamErr = Math.Max(maxParamErr, Math.Abs(blood.Speed() - g.Speed));
            maxParamErr = Math.Max(maxParamErr, Math.Abs(blood.TurnGain() - g.TurnGain));
            maxParamErr = Math.Max(maxParamErr, Math.Abs(blood.PlasticityBudget() - g.Budget));
            if (paid != g.Paid) badPaid++;
            if (blood.WantsFood() != g.WantsFood) badWants++;
            if (firstMismatch < 0 && (maxConcErr > 1e-4 || paid != g.Paid))
                firstMismatch = t;
        }

        Debug.Log($"[bio-parity] {golden.Count} тиков: max rel conc={maxConcErr:E2} " +
                  $"(худшая химия: {(worstChem >= 0 ? chemNames[worstChem] : "—")}), " +
                  $"max rel loci={maxLociErr:E2}, max abs params={maxParamErr:E2}, " +
                  $"расхождений paid={badPaid}, wantsFood={badWants}, " +
                  $"first mismatch={firstMismatch}");

        Assert.LessOrEqual(maxConcErr, 1e-4, "концентрации разошлись с эталоном");
        Assert.LessOrEqual(maxLociErr, 1e-4, "локусы разошлись с эталоном");
        Assert.LessOrEqual(maxParamErr, 1e-4, "параметры тела разошлись с эталоном");
        Assert.AreEqual(0, badPaid, "оплата пластичности расходится с эталоном");
        Assert.AreEqual(0, badWants, "WantsFood расходится с эталоном");
    }

    // ── Порты Rust-тестов метаболизма (контрольные точки поведения) ──

    [Test]
    public void CortisolOutlivesPainByOrdersOfMagnitude()
    {
        var blood = Blood.Creature();
        blood.BumpWall();
        blood.Update();
        float pain0 = blood.Conc[Chem.Pain], cort0 = blood.Conc[Chem.Cortisol];
        Assert.Greater(pain0, 0f);
        Assert.Greater(cort0, 0f);
        for (int i = 0; i < 200; i++) blood.Update();
        // Боль (decay 0.81) исчезает за секунды, кортизол (0.999) живёт минуты.
        Assert.Less(blood.Conc[Chem.Pain], pain0 * 1e-6f, "боль должна почти исчезнуть");
        Assert.Greater(blood.Conc[Chem.Cortisol], cort0 * 0.5f, "кортизол должен пережить боль");
    }

    [Test]
    public void DigestionProducesAcetylcholine()
    {
        var blood = Blood.Creature();
        blood.IngestFood(60f);
        for (int i = 0; i < 50; i++) blood.Update();
        Assert.Greater(blood.Conc[Chem.Acetylcholine], 1f,
            "пищеварение должно выделять посредник внимания");
    }

    [Test]
    public void MaxMotorDriveSaturatesFatigue()
    {
        var blood = Blood.Creature();
        for (int i = 0; i < 60; i++)
        {
            blood.Loci[Locus.RateMotor] = 400f;
            blood.Update();
        }
        Assert.GreaterOrEqual(blood.Conc[Chem.Fatigue], 200f,
            "полный газ должен насытить усталость за 60 тиков (порог сна)");
    }

    [Test]
    public void DriveLociSurviveReceptorReset()
    {
        var blood = Blood.Creature();
        blood.Loci[Locus.RateMotor] = 123f;
        blood.Loci[Locus.GlucoseDeficit] = 45f;
        blood.Update();
        Assert.AreEqual(123f, blood.Loci[Locus.RateMotor], 1e-3f);
        Assert.AreEqual(45f, blood.Loci[Locus.GlucoseDeficit], 1e-3f);
    }

    [Test]
    public void PlasticityCostsGlucoseAndCanBankrupt()
    {
        var blood = Blood.Creature();
        float before = blood.Conc[Chem.Glucose];
        Assert.IsTrue(blood.PayForPlasticity(1.0f));
        Assert.Less(blood.Conc[Chem.Glucose], before, "обучение должно стоить глюкозы");
        blood.Conc[Chem.Glucose] = 0f;
        Assert.IsFalse(blood.PayForPlasticity(1.0f), "с нулевой глюкозой платить нечем");
    }

    [Test]
    public void PlasticityBudgetRespectsHormones()
    {
        var calm = Blood.Creature();
        calm.Conc[Chem.Acetylcholine] = 60f;
        var stressed = Blood.Creature();
        stressed.Conc[Chem.Cortisol] = 120f;
        Assert.Greater(calm.PlasticityBudget(), stressed.PlasticityBudget(),
            "стресс должен глушить пластичность");
        Assert.That(stressed.PlasticityBudget(), Is.InRange(0.27f, 1.01f));
    }

    [Test]
    public void SleepCapsGlucoseBelowAbundance()
    {
        var blood = Blood.Creature();
        blood.Conc[Chem.Glucose] = 200f;
        blood.Sleep();
        // Кап 120 ниже порога 150: сон не кормит, голод продолжит расти.
        Assert.AreEqual(Blood.SleepGlucoseCap, blood.Conc[Chem.Glucose], 1e-3f);
        Assert.Less(blood.Conc[Chem.Glucose], Blood.GlucoseAbundant);
    }
}
