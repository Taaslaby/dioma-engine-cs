// Тесты обучения: мозг должен УЧИТЬСЯ, а не просто совпадать с эталоном.
// Ящик Скиннера даёт проверяемое утверждение: награждаемый путь усиливается,
// ненаграждаемые остаются на месте.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Dioma.Spikes;

public class LearningTests
{
    static string DataDir => TestPaths.DataDir;

    [Test]
    public void Skinner_RewardedPathGrows()
    {
        var box = SkinnerBox.Load(DataDir, out var golden);
        float w0 = box.W0;

        foreach (var g in golden)
            box.Step(g.Target);

        var w = box.Brain.CsrValues();
        double selectivity = box.Selectivity();
        Debug.Log($"[skinner] {box.TrialsRun} проб: награждаемый {w0:F3} → {w[0]:F3}, " +
                  $"ненаграждаемые [{w[1]:F3} {w[2]:F3} {w[3]:F3}], " +
                  $"избирательность ×{selectivity:F2}");

        Assert.Greater(w[0], w0 * 2f, "награждаемый путь должен вырасти минимум вдвое");
        Assert.Greater(selectivity, 3.0,
            "обучение должно быть избирательным: награждаемый путь сильнее прочих");
    }

    [Test]
    public void Skinner_ParityWithReference()
    {
        var box = SkinnerBox.Load(DataDir, out var golden);
        int firedMismatch = 0, rewardMismatch = 0, firstMismatch = -1;
        double maxWeightErr = 0;

        for (int t = 0; t < golden.Count; t++)
        {
            var mine = box.Step(golden[t].Target);
            var g = golden[t];
            if (mine.Fired != g.Fired) firedMismatch++;
            if (mine.Rewarded != g.Rewarded) rewardMismatch++;
            for (int i = 0; i < g.W.Length; i++)
                maxWeightErr = Math.Max(maxWeightErr,
                    Math.Abs(mine.W[i] - g.W[i]) / Math.Max(1.0, Math.Abs(g.W[i])));
            if (firstMismatch < 0 && (mine.Fired != g.Fired || mine.Rewarded != g.Rewarded))
                firstMismatch = t;
        }

        Debug.Log($"[skinner-parity] {golden.Count} проб: расхождений спайков {firedMismatch}, " +
                  $"наград {rewardMismatch}, max rel вес={maxWeightErr:E2}, first={firstMismatch}");

        Assert.AreEqual(0, firedMismatch, "спайки должны совпадать с эталоном ТОЧНО");
        Assert.AreEqual(0, rewardMismatch, "события награды должны совпадать");
        Assert.LessOrEqual(maxWeightErr, 1e-3, "веса разошлись с эталоном");
    }

    [Test]
    public void Skinner_NoRewardNoLearning()
    {
        // Контроль: без награды веса не двигаются. Если этот тест падает —
        // веса растут сами, и «обучение» в других тестах ничего не значит.
        var box = SkinnerBox.Load(DataDir, out var golden);
        float[] before = box.Brain.CsrValues();

        foreach (var g in golden)
            box.Step(g.Target, rewardedSensor: -1); // награждаемого сенсора нет

        float[] after = box.Brain.CsrValues();
        double maxDrift = 0;
        for (int i = 0; i < before.Length; i++)
            maxDrift = Math.Max(maxDrift, Math.Abs(after[i] - before[i]));

        Debug.Log($"[skinner-control] без награды дрейф весов {maxDrift:E2}");
        Assert.LessOrEqual(maxDrift, 1e-6, "без дофамина веса меняться не должны");
    }

    [Test]
    public void EligibilityDecaysBetweenTrials()
    {
        // Зазор между пробами обязателен: eligibility (tau_e = 1000 мс ≈ 20
        // тиков) должен затухать, иначе награда кредитует пути предыдущих проб
        // и обучение теряет избирательность (замерено на эталоне: без зазора
        // все четыре веса растут до 4.700).
        var box = SkinnerBox.Load(DataDir, out _);
        Assert.GreaterOrEqual(box.GapTicks, 20,
            "зазор должен покрывать хотя бы одну tau_e");

        box.Step(0);
        double afterTrial = Math.Abs(box.Brain.EligibilitySum());
        var quiet = new float[box.Brain.N];
        for (int i = 0; i < quiet.Length; i++) quiet[i] = box.Basal;
        for (int t = 0; t < 200; t++) box.Brain.Tick(quiet);
        double afterQuiet = Math.Abs(box.Brain.EligibilitySum());

        Debug.Log($"[eligibility] после пробы {afterTrial:E3} → после 200 тишины {afterQuiet:E3}");
        Assert.Less(afterQuiet, afterTrial, "eligibility должен затухать в тишине");
    }
}
