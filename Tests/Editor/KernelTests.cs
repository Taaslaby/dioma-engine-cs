// Паритет вычислителей мембраны и бенчмарк. Burst обязан считать бит-в-бит
// как managed: рекуррентная кора хаотична, и расхождение в ulp через сотню
// тиков превращается в другое поведение. Если Burst-модуль не установлен,
// тесты паритета пропускаются — но бенчмарк managed-кернела остаётся.

using System;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Dioma.Body;
using Dioma.Spikes;

public class KernelTests
{
    static string DataDir => TestPaths.DataDir;

    static DiomaBrain LoadBrain(KernelMode mode, string prefix = "dioma_c") =>
        DiomaBrain.FromJson(
            File.ReadAllText(Path.Combine(DataDir, prefix + "_connectome.json")), mode);

    [Test]
    public void ManagedKernelIsAlwaysAvailable()
    {
        Assert.IsTrue(SpikeKernels.IsAvailable(KernelMode.Managed));
        using var brain = LoadBrain(KernelMode.Managed);
        Assert.AreEqual("managed", brain.KernelName);
    }

    [Test]
    public void UnavailableModeFallsBackToManaged()
    {
        // Билд без Burst-модуля должен запускаться, пусть и медленнее.
        using var brain = LoadBrain(KernelMode.Fastest);
        Assert.IsNotNull(brain.KernelName);
        Debug.Log($"[kernel] Fastest → «{brain.KernelName}»; " +
                  $"Burst {(SpikeKernels.IsAvailable(KernelMode.Burst) ? "есть" : "нет")}");
    }

    [Test]
    public void BurstMatchesManagedBitForBit()
    {
        if (!SpikeKernels.IsAvailable(KernelMode.Burst))
        {
            Assert.Ignore("модуль Dioma.Spikes.Burst не установлен");
            return;
        }

        using var a = LoadBrain(KernelMode.Managed);
        using var b = LoadBrain(KernelMode.Burst);
        var ca = new Creature(a);
        var cb = new Creature(b);
        var sense = new Sense { SmellL = 0.7f, SmellR = 0.2f, WallR = 0.3f, FoodNear = true };

        int mismatchTick = -1;
        for (int t = 0; t < 300; t++)
        {
            var actionA = ca.Tick(sense);
            var actionB = cb.Tick(sense);
            if (mismatchTick < 0 &&
                (ca.LastMl != cb.LastMl || ca.LastMr != cb.LastMr ||
                 ca.LastSpikes != cb.LastSpikes || actionA.Steer != actionB.Steer))
                mismatchTick = t;
        }

        float[] wa = a.CsrValues(), wb = b.CsrValues();
        double maxDiff = 0;
        for (int i = 0; i < wa.Length; i++)
            maxDiff = Math.Max(maxDiff, Math.Abs(wa[i] - wb[i]));

        Debug.Log($"[kernel] 300 тиков: первое расхождение {mismatchTick}, " +
                  $"max |Δw| = {maxDiff:E2}, спайков {a.StepCount} шагов");

        Assert.AreEqual(-1, mismatchTick,
            "Burst и managed обязаны совпадать бит-в-бит — иначе FloatMode не Strict");
        Assert.AreEqual(0.0, maxDiff, "веса разошлись: пластичность видит разные спайки");
    }

    [Test]
    public void KernelBenchmark()
    {
        const int ticks = 200;
        // Раскладка тика: мембрана (кернел) против пластичности и разбора
        // истории спайков. Без неё ускорение кернела нечем интерпретировать —
        // «×1.3 на тике» может значить «×5 на мембране, но она не главная».
        double managedKernel = MeasureKernelOnly(KernelMode.Managed, ticks);
        double managedFull = MeasureFullTick(KernelMode.Managed, ticks);
        Debug.Log($"[bench] managed: тик {managedFull:F2} мс = мембрана " +
                  $"{managedKernel:F2} + пластичность {managedFull - managedKernel:F2} " +
                  $"(760 нейронов, 32.5k синапсов)");

        if (!SpikeKernels.IsAvailable(KernelMode.Burst))
        {
            Debug.Log("[bench] Burst недоступен — сравнение пропущено");
            return;
        }
        double burstKernel = MeasureKernelOnly(KernelMode.Burst, ticks);
        double burstFull = MeasureFullTick(KernelMode.Burst, ticks);
        Debug.Log($"[bench] burst: тик {burstFull:F2} мс = мембрана {burstKernel:F2} " +
                  $"+ пластичность {burstFull - burstKernel:F2}");
        Debug.Log($"[bench] ускорение: мембрана ×{managedKernel / burstKernel:F1}, " +
                  $"тик целиком ×{managedFull / burstFull:F1}");

        Assert.Less(burstKernel, managedKernel,
            "Burst обязан ускорять мембрану — иначе он не нужен");
    }

    /// Только мембрана: кернел гонит тик подшагов, пластичности нет.
    static double MeasureKernelOnly(KernelMode mode, int ticks)
    {
        using var brain = LoadBrain(mode);
        var layout = brain.KernelLayout();
        using var kernel = SpikeKernels.Create(mode, layout, brain.CsrValues());
        var ext = new float[layout.N];
        for (int i = 0; i < layout.N; i++) ext[i] = 0.12f;
        for (int i = layout.N / 4; i < layout.N / 3; i++) ext[i] = 5f;
        var history = new int[brain.StepsPerTick * layout.N];

        kernel.Run(ext, brain.StepsPerTick, history); // прогрев (Burst компилируется)
        var sw = Stopwatch.StartNew();
        for (int t = 0; t < ticks; t++)
            kernel.Run(ext, brain.StepsPerTick, history);
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / ticks;
    }

    /// Полный тик существа: мембрана + следы STDP + гомеостаз + тело.
    static double MeasureFullTick(KernelMode mode, int ticks)
    {
        using var brain = LoadBrain(mode);
        var creature = new Creature(brain);
        var sense = new Sense { SmellL = 0.7f, FoodNear = true };
        for (int t = 0; t < 5; t++) creature.Tick(sense);
        var sw = Stopwatch.StartNew();
        for (int t = 0; t < ticks; t++) creature.Tick(sense);
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / ticks;
    }
}
