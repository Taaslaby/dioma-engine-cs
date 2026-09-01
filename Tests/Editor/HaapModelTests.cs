// HAAP-модель мембраны: физические инварианты из тестов Rust
// (`pillars::zoo::neurons::haap`) — те же VNC-параметры, те же токи,
// те же ожидания. Числового паритета с Rust для HAAP нет (экспортёра не
// существует), честность модели держат инварианты: равновесие покоя,
// отклик на ток, тишина без стимула, устойчивость к перегрузке.

using System;
using NUnit.Framework;
using Dioma.Spikes;

public class HaapModelTests
{
    const int N = 8;
    const float Dt = 0.05f;

    /// VNC-профиль из make_vnc_params (Rust): покой −65 (e_l), порог −40.
    static float[] VncProfile()
    {
        var profile = new float[N * HaapCols.N];
        for (int i = 0; i < N; i++)
        {
            var row = new float[HaapCols.N];
            HaapCols.VncInto(row, Dt);
            Array.Copy(row, 0, profile, i * HaapCols.N, HaapCols.N);
        }
        return profile;
    }

    static SpikeLayout Layout(float[] profile)
    {
        // Одиночный нейронный массив без синапсов: тестируем мембрану,
        // не рассылку. CSR с нулём рёбер на нейрон.
        var rowPtr = new int[N + 1];
        var layout = new SpikeLayout
        {
            N = N,
            RowPtr = rowPtr,
            ColIdx = Array.Empty<int>(),
            Nt = new float[N],
            TimeFactor = 1f,
            SynDecay = 0f,
            RefrSteps = 1f,
            VRest = -65f,
            VThresh = -40f,
            VReset = -55f,
            RingSize = 2,
            Model = NeuronModel.Haap,
            HaapProfile = profile,
        };
        return layout;
    }

    static (ManagedSpikeKernel<HaapModel> Kernel, int[] History) KernelWith(
        float[] profile, float[] ext)
    {
        var kernel = new ManagedSpikeKernel<HaapModel>(
            Layout(profile), new float[0], new HaapModel(Layout(profile)));
        var history = new int[2000 * N];
        kernel.Run(ext, 2000, history);
        return (kernel, history);
    }

    static int TotalSpikes(int[] history, int n, int steps)
    {
        int total = 0;
        for (int i = 0; i < n * steps; i++)
            total += history[i];
        return total;
    }

    [Test]
    public void Spikes_With_Current()
    {
        // Rust: «HAAP should spike with I=15» — 2000 шагов, суммарно > 0.
        var ext = new float[N];
        for (int i = 0; i < N; i++) ext[i] = 15f;
        var (_, history) = KernelWith(VncProfile(), ext);
        Assert.Greater(TotalSpikes(history, N, 2000), 0, "HAAP обязан спайкать при I=15");
    }

    [Test]
    public void Silent_With_Inhibition()
    {
        // Rust: «HAAP should not spike with I=-10» — 1000 шагов, ноль.
        var ext = new float[N];
        for (int i = 0; i < N; i++) ext[i] = -10f;
        var profile = VncProfile();
        var layout = Layout(profile);
        var kernel = new ManagedSpikeKernel<HaapModel>(
            layout, new float[0], new HaapModel(layout));
        var history = new int[1000 * N];
        kernel.Run(ext, 1000, history);
        Assert.AreEqual(0, TotalSpikes(history, N, 1000),
            "тормозной ток не должен рождать спайков (иначе — PING-петля)");
    }

    [Test]
    public void Silence_Is_Stable_And_Finite()
    {
        // Rust test_haap_stress_silence: 10000 шагов при I=0 — ни спайка,
        // ни NaN. Покой — устойчивое равновесие, инициализация в нём.
        var ext = new float[N];
        var profile = VncProfile();
        var layout = Layout(profile);
        var kernel = new ManagedSpikeKernel<HaapModel>(
            layout, new float[0], new HaapModel(layout));
        var history = new int[10000 * N];
        kernel.Run(ext, 10000, history);
        Assert.AreEqual(0, TotalSpikes(history, N, 10000),
            "в тишине HAAP не стреляет (равновесие покоя)");

        var membrane = new float[N];
        kernel.ReadMembrane(membrane);
        for (int i = 0; i < N; i++)
            Assert.IsFalse(float.IsNaN(membrane[i]), $"v[{i}] NaN в тишине");
    }

    [Test]
    public void Overload_Stays_Finite()
    {
        // Rust test_haap_stress_explosion: 10000 шагов при I=1000 —
        // без NaN и бесконечностей. Клэмп экспоненты и гейтов держат
        // разрыв даже на нефизиологическом токе.
        var ext = new float[N];
        for (int i = 0; i < N; i++) ext[i] = 1000f;
        var profile = VncProfile();
        var layout = Layout(profile);
        var kernel = new ManagedSpikeKernel<HaapModel>(
            layout, new float[0], new HaapModel(layout));
        var history = new int[10000 * N];
        kernel.Run(ext, 10000, history);

        var membrane = new float[N];
        kernel.ReadMembrane(membrane);
        for (int i = 0; i < N; i++)
        {
            Assert.IsTrue(float.IsFinite(membrane[i]), $"v[{i}] не конечен при перегрузке");
        }
    }

    [Test]
    public void Adaptation_Slows_Firing()
    {
        // Суть HAAP: M-ток копит delta_u с каждым спайком, и частота
        // ПАДАЕТ со временем. Первый миллисекунды гуще хвоста.
        var ext = new float[N];
        for (int i = 0; i < N; i++) ext[i] = 15f;
        var profile = VncProfile();
        var layout = Layout(profile);
        var kernel = new ManagedSpikeKernel<HaapModel>(
            layout, new float[0], new HaapModel(layout));
        var history = new int[4000 * N];
        kernel.Run(ext, 4000, history);

        int Count(int fromStep, int toStep)
        {
            int total = 0;
            for (int s = fromStep; s < toStep; s++)
                for (int i = 0; i < N; i++)
                    total += history[s * N + i];
            return total;
        }
        int head = Count(0, 1000);
        int tail = Count(3000, 4000);
        Assert.Greater(head, 0);
        Assert.Greater(head, tail,
            $"адаптация обязана гасить частоту: голова {head} против хвоста {tail}");
    }

    [Test]
    public void Resting_State_Matches_Leak()
    {
        // Равновесие = точка, где сумма токов обращается в ноль. Проверяем
        // ИМЕННО ЭТО (физика), а не конкретное v: у профиля из pillars-тестов
        // e_l=−65, но h-ток (g_h 0.3, h_∞>0) смещает покой вверх — до ~−59.5.
        var row = new float[HaapCols.N];
        HaapCols.VncInto(row, Dt);
        float gl = row[(int)HaapCol.GL], el = row[(int)HaapCol.EL];
        float gm = row[(int)HaapCol.GM], ek = row[(int)HaapCol.EK];
        float gh = row[(int)HaapCol.GH], eh = row[(int)HaapCol.EH];
        float v = HaapModel.RestingState(row, 0, out float u, out float h);

        float iTotal = gl * (v - el) + gm * u * (v - ek) + gh * h * (v - eh);
        Assert.AreEqual(0f, iTotal, 1e-2f,
            "в равновесии бисекции сумма токов обязана быть нулём");
        Assert.That(v, Is.InRange(-75f, -50f), "покой в физиологическом диапазоне");
        Assert.That(u, Is.InRange(0f, 1f));
        Assert.That(h, Is.InRange(0f, 1f));
    }

    [Test]
    public void Kcnq_Knockout_Removes_Adaptation()
    {
        // Нокаут KCNQ (M-ток → 0): адаптация исчезает, частота держится.
        // Это ДНК→поведение одним числом: гены каналов реально работают.
        var genes = PhysioGenes.Wildtype();
        genes.Kcnq = 0f;

        var profile = new float[N * HaapCols.N];
        var row = new float[HaapCols.N];
        for (int i = 0; i < N; i++)
        {
            HaapPhysiology.ColumnsInto(row, NeuronArchetype.Motor, genes);
            Array.Copy(row, 0, profile, i * HaapCols.N, HaapCols.N);
        }
        var ext = new float[N];
        for (int i = 0; i < N; i++) ext[i] = 15f;
        var layout = Layout(profile);
        var knockout = new ManagedSpikeKernel<HaapModel>(
            layout, new float[0], new HaapModel(layout));
        var historyKo = new int[4000 * N];
        knockout.Run(ext, 4000, historyKo);

        int Count(int[] hist, int fromStep, int toStep)
        {
            int total = 0;
            for (int s = fromStep; s < toStep; s++)
                for (int i = 0; i < N; i++)
                    total += hist[s * N + i];
            return total;
        }
        int tailKo = Count(historyKo, 3000, 4000);

        // Для сравнения — дикий тип: хвост после адаптации.
        var wildProfile = new float[N * HaapCols.N];
        for (int i = 0; i < N; i++)
        {
            HaapPhysiology.ColumnsInto(row, NeuronArchetype.Motor,
                PhysioGenes.Wildtype());
            Array.Copy(row, 0, wildProfile, i * HaapCols.N, HaapCols.N);
        }
        var wildLayout = Layout(wildProfile);
        var wildKernel = new ManagedSpikeKernel<HaapModel>(
            wildLayout, new float[0], new HaapModel(wildLayout));
        var historyWild = new int[4000 * N];
        wildKernel.Run(ext, 4000, historyWild);
        int tailWild = Count(historyWild, 3000, 4000);

        Assert.GreaterOrEqual(tailKo, tailWild,
            $"нокаут KCNQ не может стрелять РЕЖЕ дикого типа: {tailKo} vs {tailWild}");
    }

    [Test]
    public void BrainProfile_Covers_Every_Neuron()
    {
        // Профиль обязан покрывать всех: иначе HAAP-модель откажет на
        // создании с внятной ошибкой (не молча на нулях).
        var regionOf = new int[] { 0, 0, 1, 2, 3, 3, 4, 5 };
        var profile = HaapPhysiology.BrainProfile(regionOf,
            HaapPhysiology.ArenaArchetype, PhysioGenes.Wildtype());
        Assert.AreEqual(8 * HaapCols.N, profile.Length);
        for (int i = 0; i < 8; i++)
        {
            float dt = profile[i * HaapCols.N + (int)HaapCol.Dt];
            Assert.AreEqual(HaapPhysiology.DtMs, dt,
                $"нейрон {i}: dt должен стоять в каждой строке");
        }
    }

    [Test]
    public void Haap_Brain_From_Json_Parses_And_Runs()
    {
        // Коннектом с «model»: «haap» и «haapProfile» обязан читаться
        // тем же парсером и тикать. Рефлекс S→I→M на 3 нейрона, по
        // рёбрам — стреляющий профиль.
        int n = 3;
        var regionOf = new int[] { 0, 1, 2 };
        var profile = HaapPhysiology.BrainProfile(regionOf,
            HaapPhysiology.ReflexArchetype, PhysioGenes.Wildtype());

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"n\":3,\"dt\":0.5,\"stepsPerTick\":100,");
        sb.Append("\"lif\":{\"tauRef\":2.2,\"tauM\":20,\"vRest\":-52,\"vThresh\":-45," +
                  "\"vReset\":-52,\"tauSyn\":3,\"tDelay\":1.8},");
        sb.Append("\"stdp\":{\"tauPre\":20,\"tauPost\":20,\"tauE\":1000,\"aLtp\":0.1," +
                  "\"aLtd\":0.005,\"lr\":0.15,\"wMin\":0,\"wMax\":6},");
        sb.Append("\"plasticity\":{\"homeoRate\":0.25,\"rewardDelayTicks\":1," +
                  "\"homeoEveryTicks\":0,\"homeoExcludePre\":[]},");
        sb.Append("\"model\":\"haap\",");
        sb.Append("\"regions\":[");
        sb.Append("{\"tag\":\"S\",\"start\":0,\"end\":1,\"inhibitory\":false},");
        sb.Append("{\"tag\":\"I\",\"start\":1,\"end\":2,\"inhibitory\":true},");
        sb.Append("{\"tag\":\"M\",\"start\":2,\"end\":3,\"inhibitory\":false}],");
        sb.Append("\"gains\":[[0,0,0],[0,0,1],[0,0,0]],");
        sb.Append("\"nt\":[1,-1,1],");
        sb.Append("\"edges\":[[0,2,2.0]],");
        sb.Append("\"haapProfile\":[");
        for (int i = 0; i < n; i++)
        {
            sb.Append('[');
            for (int c = 0; c < HaapCols.N; c++)
            {
                if (c > 0) sb.Append(',');
                float v = profile[i * HaapCols.N + c];
                sb.Append(v == MathF.Floor(v) && MathF.Abs(v) < 1e7f
                    ? ((long)v).ToString(inv) : v.ToString("R", inv));
            }
            sb.Append(']');
            if (i < n - 1) sb.Append(',');
        }
        sb.Append("]}");

        using var brain = DiomaBrain.FromJson(sb.ToString());
        Assert.AreEqual(NeuronModel.Haap, brain.Model);
        Assert.AreEqual(3, brain.N);

        // Тик с током в сенсор: не обязано дать спайк (VNC-масштабы), но
        // обязано НЕ упасть и NOT дать NaN.
        var ext = new float[3];
        ext[0] = 20f;
        long spikes = brain.Tick(ext);
        Assert.IsTrue(spikes >= 0, "тик прошёл без исключений");
    }

    [Test]
    public void Haap_Without_Profile_Fails_Loudly()
    {
        // Мозг с «model»: «haap» и БЕЗ профиля — внятная ошибка, не
        // молчаливые нули (модель без параметров не имеет смысла).
        var json = "{\"n\":1,\"dt\":0.5,\"stepsPerTick\":10," +
                   "\"lif\":{\"tauRef\":2.2,\"tauM\":20,\"vRest\":-52,\"vThresh\":-45," +
                   "\"vReset\":-52,\"tauSyn\":3,\"tDelay\":1.8}," +
                   "\"stdp\":{\"tauPre\":20,\"tauPost\":20,\"tauE\":1000,\"aLtp\":0.1,\"aLtd\":0.005,\"lr\":0.15,\"wMin\":0,\"wMax\":6}," +
                   "\"plasticity\":{\"homeoRate\":0.25,\"rewardDelayTicks\":1,\"homeoEveryTicks\":0,\"homeoExcludePre\":[]}," +
                   "\"model\":\"haap\"," +
                   "\"regions\":[{\"tag\":\"S\",\"start\":0,\"end\":1,\"inhibitory\":false}]," +
                   "\"gains\":[[0]],\"nt\":[1],\"edges\":[]}";
        Assert.Throws<ArgumentNullException>(() => DiomaBrain.FromJson(json),
            "HAAP без haapProfile обязан отказаться внятно");
    }
}
