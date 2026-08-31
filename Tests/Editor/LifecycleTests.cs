// Жизненный цикл: возраст, старение, агония, смерть, спасение. Порт фазы
// смертности из Rust `sim/mod.rs` — проверяем те же пороги и переходы.
//
// Смертность НЕ трогает динамику мозга (она поверх тика), поэтому паритет
// мозга здесь не при чём — тестируем саму арифметику урона и переходы.

using NUnit.Framework;
using Dioma.Bio;
using Dioma.Body;

public class LifecycleTests
{
    static Blood FedBlood(float glucose)
    {
        var b = Blood.Creature();
        b.Conc[Chem.Glucose] = glucose;
        return b;
    }

    [Test]
    public void MaxAge_Is_Base_Plus_Minus_30_Percent()
    {
        // Разброс срока жизни от сида — чтобы поколение не вымирало разом.
        ulong min = ulong.MaxValue, max = 0;
        for (ulong seed = 1; seed <= 500; seed++)
        {
            var lc = Lifecycle.Newborn(seed);
            if (lc.MaxAge < min) min = lc.MaxAge;
            if (lc.MaxAge > max) max = lc.MaxAge;
        }
        // База ±30%: 50400 … 93600.
        Assert.GreaterOrEqual(min, (ulong)(Lifecycle.BaseMaxAge * 0.7f));
        Assert.LessOrEqual(max, (ulong)(Lifecycle.BaseMaxAge * 1.3f));
        Assert.Less(min, max, "сиды должны давать разброс, а не одно число");
    }

    [Test]
    public void Same_Seed_Same_Lifespan()
    {
        Assert.AreEqual(Lifecycle.Newborn(12345).MaxAge, Lifecycle.Newborn(12345).MaxAge);
    }

    [Test]
    public void Young_Fed_Creature_Does_Not_Age_Damage()
    {
        var lc = new Lifecycle(maxAge: 72_000);
        var blood = FedBlood(200f);   // сыт по горло
        for (int t = 0; t < 1000; t++) lc.Advance(blood, asleep: false);
        Assert.AreEqual(0f, lc.Senescence, "молодой и сытый не должен копить старческий урон");
        Assert.IsFalse(lc.Dying);
        Assert.Greater(lc.Age, 0);
    }

    [Test]
    public void Old_Age_Accumulates_Senescence_And_Kills()
    {
        // Короткий срок жизни: быстро дойти до старости и смерти в тесте.
        var lc = new Lifecycle(maxAge: 200);
        // Глюкоза в коридоре (0, RecoverGlucose): выше нуля — нет голодного
        // катаболизма; ниже 50 — агония НЕ спасается сытостью, старость
        // доводит до смерти; ниже 200 — старческий урон не лечится.
        var blood = FedBlood(30f);

        int diedAt = -1;
        for (int t = 0; t < 20000; t++)
        {
            blood.Conc[Chem.Glucose] = 30f;
            bool became = lc.Advance(blood, asleep: false);
            if (became) { diedAt = t; break; }
        }

        Assert.Greater(lc.Senescence, 0f, "после порога старости урон должен копиться");
        Assert.Greater(diedAt, 0, "старость обязана убить за отведённое время");
        Assert.IsTrue(lc.Dead);
    }

    [Test]
    public void Well_Fed_Creature_Survives_Its_Agony()
    {
        // Обратная сторона: если глюкоза выше RecoverGlucose, агония от
        // старости КАЖДЫЙ раз спасается — сытое существо живёт дольше срока.
        // Это не баг: кормление продлевает жизнь, смерть придёт от endgame,
        // когда урон копится быстрее, чем спасение успевает.
        var lc = new Lifecycle(maxAge: 200);
        var blood = FedBlood(120f);
        bool everRescuedPastMaxAge = false;
        for (int t = 0; t < 6000; t++)
        {
            blood.Conc[Chem.Glucose] = 120f;   // выше RecoverGlucose, ниже HealGlucose
            lc.Advance(blood, asleep: false);
            if (lc.Age > lc.MaxAge && !lc.Dying && lc.Senescence > 0f)
                everRescuedPastMaxAge = true;
        }
        Assert.Greater(lc.Age, lc.MaxAge, "сытое существо переживает базовый срок");
        Assert.IsTrue(everRescuedPastMaxAge, "кормление вытягивает из агонии");
    }

    [Test]
    public void Starvation_Damage_Without_Glucose()
    {
        var lc = new Lifecycle(maxAge: 72_000);   // молодой: только голод
        var blood = FedBlood(0f);
        for (int t = 0; t < 100; t++)
        {
            blood.Conc[Chem.Glucose] = 0f;
            lc.Advance(blood, asleep: false);
        }
        // 100 тиков × 0.25 = 25 очков старческой копилки от катаболизма.
        Assert.AreEqual(25f, lc.Senescence, 0.01f);
    }

    [Test]
    public void Sleeping_Ages_But_Does_Not_Damage()
    {
        var lc = new Lifecycle(maxAge: 200);
        var blood = FedBlood(0f);   // голодный, но спит
        for (int t = 0; t < 500; t++) lc.Advance(blood, asleep: true);
        Assert.AreEqual(500ul, lc.Age, "возраст идёт и во сне");
        Assert.AreEqual(0f, lc.Senescence, "спящий метаболизм стоит — урон не копится");
    }

    [Test]
    public void Agony_Then_Death_By_Timer()
    {
        var lc = new Lifecycle(maxAge: 100);
        var blood = FedBlood(0f);   // не спасётся: глюкоза ниже RecoverGlucose

        // Догоняем до агонии.
        int guard = 0;
        while (!lc.Dying && guard++ < 50000)
        {
            blood.Conc[Chem.Glucose] = 10f;   // ниже RecoverGlucose (50)
            lc.Advance(blood, asleep: false);
        }
        Assert.IsTrue(lc.Dying, "должна начаться агония");

        // Агония ровно DyingTicks, потом труп.
        bool died = false;
        for (int t = 0; t < Lifecycle.DyingTicks + 5 && !died; t++)
        {
            blood.Conc[Chem.Glucose] = 10f;
            died = lc.Advance(blood, asleep: false);
        }
        Assert.IsTrue(lc.Dead, "по истечении агонии — смерть");
        Assert.AreEqual(Lifecycle.CorpseTicks, lc.CorpseLeft, "труп начинает лежать");
    }

    [Test]
    public void Feeding_During_Agony_Rescues()
    {
        // Голодная агония МОЛОДОГО спасается едой. Большой срок жизни, чтобы
        // в агонию завёл именно голод, а не старость (старость едой не лечится).
        var lc = new Lifecycle(maxAge: 1_000_000);
        var blood = FedBlood(10f);
        int guard = 0;
        while (!lc.Dying && guard++ < 50000)
        {
            blood.Conc[Chem.Glucose] = 0f;   // голод копит урон
            lc.Advance(blood, asleep: false);
        }
        Assert.IsTrue(lc.Dying, "голод должен довести до агонии");

        // Накормили выше RecoverGlucose — следующий тик спасает.
        blood.Conc[Chem.Glucose] = 120f;
        lc.Advance(blood, asleep: false);
        Assert.IsFalse(lc.Dying, "сытость выше порога снимает голодную агонию");
        Assert.IsFalse(lc.Dead, "спасённый не умирает");
    }

    [Test]
    public void Old_Age_Agony_Is_Not_Rescued_By_Food()
    {
        // Осознанное отклонение от Rust: перешагнувшего свой срок едой не
        // вернуть. Иначе успешный охотник держит глюкозу выше порога спасения
        // и живёт вечно — поколения не сменяются.
        var lc = new Lifecycle(maxAge: 100);
        var blood = FedBlood(120f);
        bool died = false;
        for (int t = 0; t < 20000 && !died; t++)
        {
            blood.Conc[Chem.Glucose] = 120f;   // сыт, но стар
            died = lc.Advance(blood, asleep: false);
        }
        Assert.IsTrue(lc.Dead, "старость должна убить даже сытого");
        Assert.Greater(lc.Age, lc.MaxAge, "смерть наступает за пределами срока");
    }

    [Test]
    public void Corpse_Decays_And_Advance_Is_Idle()
    {
        var lc = new Lifecycle(maxAge: 1);
        var blood = FedBlood(0f);
        // Быстро в труп.
        for (int t = 0; t < 100000 && !lc.Dead; t++)
        {
            blood.Conc[Chem.Glucose] = 0f;
            lc.Advance(blood, asleep: false);
        }
        Assert.IsTrue(lc.Dead);

        int corpse0 = lc.CorpseLeft;
        ulong age0 = lc.Age;
        lc.Advance(blood, asleep: false);
        Assert.AreEqual(corpse0 - 1, lc.CorpseLeft, "труп истлевает");
        Assert.AreEqual(age0, lc.Age, "мёртвый не стареет");
    }
}
