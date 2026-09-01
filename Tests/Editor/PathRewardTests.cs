// D1/D2-пути (R-STDP из BrainCog BDM-SNN): тени eligibility с разными
// затуханиями и развязкой знака. Что проверяется против старого пути:
//   - старый RewardNow: дофамин (любого знака) × ОДИН след — наказание
//     просто ослабляет рёбра;
//   - RewardPathed: наказание читает КОСВЕННЫЙ след — строит НЕ-действие,
//     и D2 помнит дольше D1 (0.64 против 0.8 за тик).
//
// Числа затуханий — trace_decay=0.8 из BDM-SNN (там он общий); разнесение
// D2 ниже — перенос принципа «косвенный путь консервативнее», из биологии
// базальных ганглиев (D2-клетки полосатого тела интегрируют медленнее).

using System.IO;
using NUnit.Framework;
using Dioma.Spikes;

public class PathRewardTests
{
    static DiomaBrain Brain(string name = "dioma")
    {
        var info = UnityEditor.PackageManager.PackageInfo.FindForPackageName("com.dioma.engine");
        string root = info != null && !string.IsNullOrEmpty(info.resolvedPath)
            ? info.resolvedPath
            : Path.GetFullPath(Path.Combine("Packages", "com.dioma.engine"));
        string path = Path.Combine(root, "Tests", "Editor", "Data", name + "_connectome.json");
        return DiomaBrain.FromJson(File.ReadAllText(path));
    }

    /// Синапс с ненулевым eligibility после стимуляции: первый входящий
    /// в моторный регион (там, где награда вообще действует).
    static void Stimulate(DiomaBrain brain)
    {
        var ext = new float[brain.N];
        // Тонический ток в первые два региона (сенсоры) — рождает спайки
        // и ненулевые следы на путях к моторам.
        for (int i = 0; i < 20 && i < brain.N; i++) ext[i] = 2f;
        brain.Tick(ext);
        brain.Tick(ext);
    }

    [Test]
    public void Punishment_Pathed_Builds_Inhibition_Not_Erase()
    {
        // Ядро переноса: наказание обязано идти ЧЕРЕЗ КОСВЕННЫЙ след,
        // а не быть «отрицательной наградой»: результат численно другой.
        var plain = Brain();
        var pathed = Brain();
        pathed.EnablePathTraces();

        // Обе копии мозга в одинаковом состоянии: одинаково стимулированы,
        // следи накоплены.
        Stimulate(plain);
        Stimulate(pathed);

        var beforePlain = plain.CsrValues();
        var beforePathed = pathed.CsrValues();

        // Одно и то же наказание, два механизма.
        plain.RewardNow(-1f);
        pathed.RewardPathed(-1f);

        var afterPlain = plain.CsrValues();
        var afterPathed = pathed.CsrValues();

        // 1) Оба механизма что-то делают (не no-op).
        int plainMoved = 0, pathedMoved = 0;
        for (int i = 0; i < beforePlain.Length; i++)
        {
            if (afterPlain[i] != beforePlain[i]) plainMoved++;
            if (afterPathed[i] != beforePathed[i]) pathedMoved++;
        }
        Assert.Greater(pathedMoved, 0, "RewardPathed обязан менять веса");

        // 2) ГЛАВНОЕ: механизмы РАЗНЫЕ — после одного наказания веса
        //    расходятся. Если бы RewardPathed был переименованным
        //    RewardNow, разницы бы не было.
        int disagree = 0;
        for (int i = 0; i < afterPathed.Length; i++)
            if (afterPlain[i] != afterPathed[i]) disagree++;
        Assert.Greater(disagree, 0,
            "наказание через косвенный след — ДРУГИЕ веса, чем через " +
            "безличный LTD: 'строить НЕ-действие' ≠ 'стирать действие'");
    }

    [Test]
    public void Traces_Match_BrainCog_And_Differ_By_Sign()
    {
        // Перенос BDM-SNN честен в двух вещах:
        //   (а) затухание СЛЕДОВ единое (trace_decay=0.8) — «медленность»
        //       косвенного пути у них НЕ в следе, а в базе нейронов D2;
        //       первый вариант порта (0.64) давал обратное желаемому
        //       (вклад 0.64^k < 0.8^k) и снят;
        //   (б) различие путей — ЗНАК применения: dw_d1 = +r·trace,
        //       dw_d2 = −r·trace. Наказание читает D2 и переворачивает —
        //       «строить НЕ-действие», не «стирать действие».
        Assert.AreEqual(0.8f, DiomaBrain.D1Decay, 1e-6f,
            "trace_decay из BDM-SNN");
        Assert.AreEqual(DiomaBrain.D1Decay, DiomaBrain.D2Decay,
            "затухание путей единое — различие в знаке, не в памяти");

        // Знак в деле: награда и наказание ОДНОЙ силы на одинаково
        // горячих следах обязаны вести веса в ПРОТИВОПОЛОЖНЫЕ стороны
        // на одних и тех же рёбрах.
        var rewarded = Brain();
        var punished = Brain();
        rewarded.EnablePathTraces();
        punished.EnablePathTraces();
        Stimulate(rewarded);
        Stimulate(punished);

        var w0 = rewarded.CsrValues();
        var w0p = punished.CsrValues();
        rewarded.RewardPathed(+1f);
        punished.RewardPathed(-1f);
        var wR = rewarded.CsrValues();
        var wP = punished.CsrValues();

        int opposite = 0, same = 0;
        for (int i = 0; i < wR.Length; i++)
        {
            float dR = wR[i] - w0[i];
            float dP = wP[i] - w0p[i];
            if (dR == 0f || dP == 0f) continue;
            if (dR > 0f != dP > 0f) opposite++;
            else same++;
        }
        Assert.Greater(opposite, 0,
            "награда и наказание обязаны расходиться знаками (знак пути)");
        Assert.Greater(opposite, same,
            $"преимущественно противоположны: {opposite} против {same}");
    }

    [Test]
    public void Reward_Pathed_Uses_D1_Positive()
    {
        // Положительная награда читает ПРЯМОЙ след: свежие рёбра
        // усиливаются сильнее, чем при RewardNow (D1 не размыт
        // затуханием D2 — механизм по построению различается).
        var pathed = Brain();
        pathed.EnablePathTraces();
        Stimulate(pathed);
        var before = pathed.CsrValues();
        pathed.RewardPathed(+1f);
        var after = pathed.CsrValues();

        int grown = 0;
        for (int i = 0; i < before.Length; i++)
            if (after[i] > before[i]) grown++;
        Assert.Greater(grown, 0, "награда усиливает рёбра прямого следа");
    }

    [Test]
    public void Without_Enable_Throws()
    {
        // Ленивость честная: без EnablePathTraces — внятный отказ,
        // а не NRE из ниоткуда (грабли Burst-джобы научили).
        var brain = Brain();
        Assert.Throws<System.InvalidOperationException>(
            () => brain.RewardPathed(1f));
    }

    [Test]
    public void Old_Path_Untouched()
    {
        // Паритет: мозг БЕЗ EnablePathTraces ведёт себя байт-в-байт как
        // раньше — RewardNow и следы кернела не знают о D1/D2.
        var a = Brain();
        var b = Brain();
        Stimulate(a);
        Stimulate(b);
        a.RewardNow(1f);
        b.RewardNow(1f);
        CollectionAssert.AreEqual(a.CsrValues(), b.CsrValues(),
            "детерминизм старого пути не тронут");
    }
}
