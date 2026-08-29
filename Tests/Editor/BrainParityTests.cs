// Тесты паритета C#-ядра с Rust-эталоном (золотой след из export_connectome)
// + базовые проверки LIF-динамики и обучения.

using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Dioma.Spikes;

public class BrainParityTests
{
    // Пакет может быть embedded или подключён по file: — путь берём у Unity.
    static string DataDir
    {
        get
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForPackageName("com.dioma.spikes");
            string root = info != null && !string.IsNullOrEmpty(info.resolvedPath)
                ? info.resolvedPath
                : Path.GetFullPath("Packages/com.dioma.spikes");
            return Path.Combine(root, "Tests", "Editor", "Data");
        }
    }

    GoldenRunner _runner;

    [OneTimeSetUp]
    public void Load()
    {
        _runner = GoldenRunner.Load(DataDir);
        Assert.Greater(_runner.Rows.Count, 0, "золотой след пуст");
    }

    [Test]
    public void NeuronFiresWithStrongInput()
    {
        var brain = DiomaBrain.FromJson(
            File.ReadAllText(Path.Combine(DataDir, "dioma_connectome.json")));
        var sl = brain.Regions[0];
        var ml = brain.Regions[3];
        var ext = new float[brain.N];
        for (int i = sl.Start; i < sl.End; i++) ext[i] = 8f;
        var (mlSum, _, total) = brain.TickMotor(ext, ml.Start, ml.End, ml.Start, ml.End);
        Assert.Greater(mlSum, 0, "сильный вход в SL должен вызвать спайки ML");
        Assert.Greater(total, 0);
    }

    [Test]
    public void SilentBrainStaysNearRest()
    {
        var brain = DiomaBrain.FromJson(
            File.ReadAllText(Path.Combine(DataDir, "dioma_connectome.json")));
        var ext = new float[brain.N];
        for (int i = 0; i < brain.N; i++) ext[i] = 0.12f;
        var (_, _, total) = brain.TickMotor(ext, 0, brain.N, 0, brain.N);
        // Фон 0.12 — тишина (v_inf ≈ -49, порог -45): спайков быть не должно.
        Assert.AreEqual(0, total, "фоновый ток не должен вызывать спайков");
    }

    [Test]
    public void Parity_GoldenTrace()
    {
        int exact = 0, total = _runner.Rows.Count;
        int maxDml = 0, maxDmr = 0;
        double maxWsumRel = 0;
        int firstMismatch = -1;

        foreach (var row in _runner.Rows)
        {
            _runner.Step();

            int dml = Math.Abs(row.Ml - row.MlRef);
            int dmr = Math.Abs(row.Mr - row.MrRef);
            maxDml = Math.Max(maxDml, dml);
            maxDmr = Math.Max(maxDmr, dmr);
            if (dml == 0 && dmr == 0) exact++;

            double rel = Math.Abs(row.Wsum - row.WsumRef) / Math.Max(1.0, Math.Abs(row.WsumRef));
            maxWsumRel = Math.Max(maxWsumRel, rel);

            if (firstMismatch < 0 && (dml > 0 || dmr > 0))
                firstMismatch = _runner.Cursor - 1;
        }

        Debug.Log($"[parity] точных тиков {exact}/{total}, max|dML|={maxDml}, " +
                  $"max|dMR|={maxDmr}, max rel wsum={maxWsumRel:E2}, first mismatch={firstMismatch}");

        Assert.GreaterOrEqual(exact, total * 9 / 10,
            $"слишком много расхождений моторных сумм (первое в тике {firstMismatch})");
        Assert.LessOrEqual(maxDml, 8, "моторная сумма ML разошлась с эталоном");
        Assert.LessOrEqual(maxDmr, 8, "моторная сумма MR разошлась с эталоном");
        Assert.LessOrEqual(maxWsumRel, 2e-3, "сумма весов разошлась с эталоном");
    }

    [Test]
    public void Learning_ChangesWeights()
    {
        // Свежий мозг + сценарий с наградами: дофамин должен изменить веса.
        var runner = GoldenRunner.Load(DataDir);
        double before = runner.Brain.WeightSum();
        int ticks = Math.Min(130, runner.Rows.Count);
        for (int i = 0; i < ticks; i++)
            runner.Step();
        double after = runner.Brain.WeightSum();
        Debug.Log($"[learning] wsum {before:F3} → {after:F3} за {ticks} тиков");
        Assert.AreNotEqual(before, after, "дофамин не изменил ни одного веса");
        Assert.GreaterOrEqual(runner.Brain.EligibilitySum(), 0.0);
    }
}
