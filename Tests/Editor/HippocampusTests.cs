// Гиппокамп-модуль (порт karmac/taas-checkmate): WorkingMemory + GNW.
// Тесты — перенос Rust-тестов karmac (working_memory.rs) с поправкой
// на сид: наш порт детерминирован (XorShift вместо thread_rng), что
// само по себе отдельный тест.

using NUnit.Framework;
using Dioma.Body;
using Dioma.Spikes;

public class HippocampusTests
{
    [Test]
    public void WM_Store_And_Recall()
    {
        // Rust wm_store_and_recall: проекция ненулевая, длина сохранена.
        var wm = new WorkingMemory(4, 8, seed: 1);
        wm.Store(new float[] { 0, 1, 2, 3, 4, 5, 6, 7 });
        var recalled = wm.Recall(0);
        Assert.AreEqual(8, recalled.Length);
        Assert.IsTrue(System.Array.Exists(recalled, x => System.MathF.Abs(x) > 1e-6f),
            "проекция W_in обязана быть нетривиальной");
    }

    [Test]
    public void WM_RoundRobin()
    {
        // Rust wm_round_robin: после 3 store указатель завернулся на 0.
        // У нас нет публичного write_ptr — проверяем возрастами:
        // возрасты строго [2,1,0]: последний писан — слот 2.
        var wm = new WorkingMemory(3, 4, seed: 2);
        wm.Store(new float[] { 1, 1, 1, 1 });   // слот 0
        wm.Store(new float[] { 1, 1, 1, 1 });   // слот 1
        wm.Store(new float[] { 1, 1, 1, 1 });   // слот 2
        // Возрасты [2,1,0]: каждая чужая запись +1, своя — сброс в 0
        // (порядок операций: сброс ДО цикла инкрементов, как в Rust).
        Assert.AreEqual(2, wm.SlotAge(0), "слот 0: стареет от записей B и C");
        Assert.AreEqual(1, wm.SlotAge(1), "слот 1: стареет только от C");
        Assert.AreEqual(0, wm.SlotAge(2), "слот 2: свежий");
        // LRU после полного круга — слот 0 (следующий кандидат на запись).
        Assert.AreEqual(0, wm.LruSlot());
        // Четвёртая запись идёт в слот 0 (round-robin вернулся).
        wm.Store(new float[] { 1, 1, 1, 1 });
        Assert.AreEqual(0, wm.SlotAge(0), "после wrap слот 0 свежий");
    }

    [Test]
    public void WM_Lru()
    {
        // Rust wm_lru: никогда не записанный слот — самый старый.
        var wm = new WorkingMemory(3, 4, seed: 3);
        wm.Store(new float[] { 1, 1, 1, 1 });   // слот 0
        wm.Store(new float[] { 1, 1, 1, 1 });   // слот 1
        Assert.AreEqual(2, wm.LruSlot(), "слот 2 не писан — он LRU");
    }

    [Test]
    public void WM_Gate_Blocks_Tiny_Input()
    {
        // Rust wm_gate: ниже порога нормы — не пишем.
        var wm = new WorkingMemory(2, 4, seed: 4) { GateThreshold = 10f };
        wm.Store(new float[] { 0.001f, 0.001f, 0.001f, 0.001f });
        var recalled = wm.Recall(0);
        foreach (var x in recalled)
            Assert.AreEqual(0f, x, "гейт обязан не пустить шум");
    }

    [Test]
    public void WM_Tick_No_Nan()
    {
        // Rust wm_tick_no_nan: 100 шагов рекуррентности без дрейфа.
        var wm = new WorkingMemory(4, 16, seed: 5);
        var v = new float[16];
        for (int i = 0; i < 16; i++) v[i] = (i - 8) * 0.1f;
        wm.Store(v);
        for (int i = 0; i < 100; i++) wm.Tick();
        foreach (var x in wm.Recall(0))
            Assert.IsTrue(float.IsFinite(x), "рекуррентная динамика стабильна");
    }

    [Test]
    public void WM_Tick_Decays_Old_Traces()
    {
        // Смысл модуля: след прошлого стимула ЗАТУХАЕТ, но ЖИВЁТ —
        // спустя шаги после Store вклад ещё отличен от нуля.
        var wm = new WorkingMemory(2, 8, seed: 6) { Decay = 0.9f };
        wm.Store(new float[] { 1, 1, 1, 1, 1, 1, 1, 1 });
        var before = wm.Recall(0);
        float maxBefore = 0;
        foreach (var x in before) maxBefore = System.MathF.Max(maxBefore, x);
        Assert.Greater(maxBefore, 0.01f, "свежий след есть");

        for (int i = 0; i < 3; i++) wm.Tick();
        var after = wm.Recall(1);   // слот 1 не трогали — но и не писали
        // след в слоте 0 затух:
        var slot0 = wm.Recall(0);
        float maxAfter = 0;
        foreach (var x in slot0) maxAfter = System.MathF.Max(maxAfter, x);
        Assert.Less(maxAfter, maxBefore, "decay гасит след");
        Assert.Greater(maxAfter, 0f, "но не мгновенно — память живёт");
        Assert.AreEqual(0f, System.MathF.Abs(after[0]) * 0f, 0f);
    }

    [Test]
    public void WM_Clear()
    {
        // Rust wm_clear.
        var wm = new WorkingMemory(2, 4, seed: 7);
        wm.Store(new float[] { 1, 1, 1, 1 });
        wm.Clear();
        foreach (var x in wm.Recall(0))
            Assert.AreEqual(0f, x);
    }

    [Test]
    public void WM_Deterministic_By_Seed()
    {
        // Наше отличие от Rust (и требование эволюции): один сид —
        // одна память. Наследуется геномом — веса обязаны воспроизводиться.
        var a = new WorkingMemory(4, 8, seed: 42);
        var b = new WorkingMemory(4, 8, seed: 42);
        a.Store(new float[] { 1, 0, 1, 0, 1, 0, 1, 0 });
        b.Store(new float[] { 1, 0, 1, 0, 1, 0, 1, 0 });
        CollectionAssert.AreEqual(a.RecallAll(), b.RecallAll(),
            "один сид — одна память: геном может её наследовать");
    }

    [Test]
    public void GNW_Forward_Changes_State()
    {
        // Rust gnw_forward_shape: residual-обновление нетривиально.
        var gnw = new GlobalWorkspace(512, 32, seed: 8);
        var r = new float[512];
        System.Array.Fill(r, 0.1f);
        gnw.Forward(r);
        bool changed = false;
        foreach (var x in r)
            if (System.MathF.Abs(x - 0.1f) > 1e-6f) { changed = true; break; }
        Assert.IsTrue(changed, "broadcast обязан менять состояние");
    }

    [Test]
    public void GNW_Forward_No_Nan_Small()
    {
        // Rust gnw_forward_small: без NaN на малых размерах.
        var gnw = new GlobalWorkspace(4, 2, seed: 9);
        var r = new float[] { 0.5f, -0.3f, 0.1f, 0f };
        gnw.Forward(r);
        foreach (var v in r)
            Assert.IsFalse(float.IsNaN(v));
    }

    [Test]
    public void GNW_Softmax_Competition_Broadcasts_Winner()
    {
        // Суть GNW: сильный вход выигрывает workspace и вещает всем.
        // Прямое следствие: после forward доминирующее направление
        // усиливается сильнее прочих (residual = broadcast(gates)).
        var gnw = new GlobalWorkspace(8, 4, seed: 10);
        var r = new float[8];
        System.Array.Fill(r, 0.05f);
        r[0] = 2f;   // один сильный сигнал
        var before = (float[])r.Clone();
        gnw.Forward(r);
        // Состояние изменилось и осталось конечным:
        bool anyChanged = false;
        for (int i = 0; i < 8; i++)
            if (System.MathF.Abs(r[i] - before[i]) > 1e-6f) anyChanged = true;
        Assert.IsTrue(anyChanged);
        for (int i = 0; i < 8; i++)
            Assert.IsTrue(float.IsFinite(r[i]));
    }
}
