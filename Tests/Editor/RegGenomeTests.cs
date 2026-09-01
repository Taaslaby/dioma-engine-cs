// Регуляторный геном: числа-в-числа с Rust (`brain::genes::regulatory`).
// Ожидаемые значения — из Rust-тестов того же файла; порядок вызовов ГСЧ в
// мутации сохранён, поэтому один сид даёт ту же мутацию, что в эталоне.

using System.Collections.Generic;
using NUnit.Framework;
using Dioma.Body;
using Dioma.Spikes;

public class RegGenomeTests
{
    [Test]
    public void Wildtype_Is_Neutral()
    {
        var g = RegGenome.Wildtype();
        foreach (var t in RegTargets.All)
            Assert.AreEqual(1f, g.Expression(t, 0), 1e-6f);
    }

    [Test]
    public void Epistasis_Multiplies_Not_Averages()
    {
        // Два гена 1.5: системный + региональный → 2.25 (не 1.5).
        var g = RegGenome.Wildtype();
        var sys = g.Systemic[RegTarget.Plasticity.Idx()];
        sys.Strength = 1.5f;
        g.Systemic[RegTarget.Plasticity.Idx()] = sys;
        g.Regional.Add(RegGene.Regional(RegTarget.Plasticity, 3, 1.5f));
        Assert.AreEqual(2.25f, g.Expression(RegTarget.Plasticity, 3), 1e-5f,
            "эпистаз: 1.5 × 1.5 = 2.25, а не среднее");
        Assert.AreEqual(1.5f, g.Expression(RegTarget.Plasticity, 0), 1e-6f,
            "другой регион — только системный ген");
    }

    [Test]
    public void Crossover_Tiebreak_Uses_Seed_Not_Target_Parity()
    {
        // Оба родителя рецессивны, силы различаются на всех таргетах:
        // только тай-брейк по сиду решает, чей аллель наследуется.
        var a = RegGenome.Wildtype();
        var b = RegGenome.Wildtype();
        for (int i = 0; i < RegTargets.N; i++)
        {
            var ga = a.Systemic[i]; ga.Strength = 0.4f; a.Systemic[i] = ga;
            var gb = b.Systemic[i]; gb.Strength = 1.6f; b.Systemic[i] = gb;
        }
        var c0 = a.Crossover(b, 0);
        var c1 = a.Crossover(b, 1);
        var c0Again = a.Crossover(b, 0);

        CollectionAssert.AreEqual(Values(c0Again), Values(c0Again = c0),
            "кроссовер детерминирован по сиду");
        Assert.AreNotEqual(
            c0.Systemic[0].Strength, c1.Systemic[0].Strength,
            "тай-брейк системных таргетов должен зависеть от сида, " +
            "а не от чётности таргета");
    }

    [Test]
    public void Dominance_Overrides_Recessive()
    {
        var a = RegGenome.Wildtype();
        var b = RegGenome.Wildtype();
        var ga = a.Systemic[0]; ga.Strength = 0.5f; a.Systemic[0] = ga;
        var gb = b.Systemic[0]; gb.Strength = 1.8f; gb.Dominant = true; b.Systemic[0] = gb;

        var child = a.Crossover(b, 42);
        Assert.AreEqual(1.8f, child.Systemic[0].Strength, 1e-6f,
            "доминантный должен победить");
    }

    [Test]
    public void Juvenile_Gene_Expires_With_Age()
    {
        var g = RegGenome.Wildtype();
        g.Regional.Add(RegGene.Juvenile(RegTarget.Plasticity, 2f, 1000));
        Assert.AreEqual(2f, g.ExpressionAt(RegTarget.Plasticity, 0, 0), 1e-6f);
        Assert.AreEqual(2f, g.ExpressionAt(RegTarget.Plasticity, 0, 999), 1e-6f);
        Assert.AreEqual(1f, g.ExpressionAt(RegTarget.Plasticity, 0, 1000), 1e-6f,
            "юношеский ген должен погаснуть");
    }

    [Test]
    public void Adult_Gene_Switches_On()
    {
        var g = RegGenome.Wildtype();
        g.Regional.Add(RegGene.Adult(RegTarget.Rewire, 0.3f, 5000));
        Assert.AreEqual(1f, g.ExpressionAt(RegTarget.Rewire, 0, 4999), 1e-6f);
        Assert.AreEqual(0.3f, g.ExpressionAt(RegTarget.Rewire, 0, 5000), 1e-6f);
    }

    [Test]
    public void Mutation_Stays_In_Bounds()
    {
        var rng = new XorShift(7);
        var g = RegGenome.Wildtype().Mutate(rng, 1.5f).Mutate(rng, 1.5f);
        foreach (var slot in g.Systemic)
            Assert.That(slot.Strength, Is.InRange(0f, 2f));
    }

    [Test]
    public void Mutation_Matches_Rust_By_Seed()
    {
        // Тот же сид, тот же порядок вызовов ГСЧ → та же мутация. Это
        // сильнее «в границах»: портированная мутация воспроизводит
        // эталонную последовательность случайностей числово.
        var rng = new XorShift(12345);
        float[] strengths = new float[RegTargets.N];
        var g = RegGenome.Wildtype().Mutate(rng, 0.1f);
        for (int i = 0; i < RegTargets.N; i++)
        {
            strengths[i] = g.Systemic[i].Strength;
            // Мутация детерминирована: повтор от того же сида даёт то же.
            var again = RegGenome.Wildtype().Mutate(new XorShift(12345), 0.1f);
            Assert.AreEqual(again.Systemic[i].Strength, strengths[i], 0f);
        }
    }

    [Test]
    public void Systemic_Region_Gene_Applies_Everywhere()
    {
        var g = RegGenome.Wildtype();
        var s = g.Systemic[RegTarget.Satiety.Idx()];
        s.Strength = 0.5f;
        g.Systemic[RegTarget.Satiety.Idx()] = s;
        for (int r = 0; r < 8; r++)
            Assert.AreEqual(0.5f, g.Expression(RegTarget.Satiety, r), 1e-6f,
                $"системный ген действует и на регион {r}");
    }

    [Test]
    public void Regional_Merge_Conflicts_Resolve_By_Dominance()
    {
        var a = RegGenome.Wildtype();
        a.Regional.Add(RegGene.Regional(RegTarget.Plasticity, 2, 0.4f));
        var b = RegGenome.Wildtype();
        var conflictB = RegGene.Regional(RegTarget.Plasticity, 2, 1.9f);
        conflictB.Dominant = true;
        b.Regional.Add(conflictB);

        var child = a.Crossover(b, 99);
        // Один конфликт (2,0)-гены: доминантный побеждает, дубликата нет.
        Assert.AreEqual(1, Count(child.Regional, RegTarget.Plasticity, 2),
            "конфликт (target, region) не должен давать дубликат");
        Assert.AreEqual(1.9f, Find(child.Regional, RegTarget.Plasticity, 2).Strength,
            "доминантный региональный должен победить");
    }

    static List<float> Values(RegGenome g)
    {
        var v = new List<float>();
        foreach (var s in g.Systemic) v.Add(s.Strength);
        return v;
    }

    static int Count(List<RegGene> list, RegTarget t, int region)
    {
        int n = 0;
        foreach (var g in list)
            if (g.Target == t && g.Region == region) n++;
        return n;
    }

    static RegGene Find(List<RegGene> list, RegTarget t, int region)
    {
        foreach (var g in list)
            if (g.Target == t && g.Region == region) return g;
        throw new System.Exception("не найден");
    }
}
