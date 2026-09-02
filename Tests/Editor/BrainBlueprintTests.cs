// Типизированный вход должен иметь ту же динамику, что и файловый JSON.
// Это страховка прямого Prometheus → Dioma моста: он не может тихо создать
// другое существо из той же физиологии и проводки.

using NUnit.Framework;
using Dioma.Spikes;

public class BrainBlueprintTests
{
    [Test]
    public void Direct_Blueprint_Is_Bit_Identical_To_Json()
    {
        using var direct = DiomaBrain.FromBlueprint(Blueprint(), KernelMode.Managed);
        using var json = DiomaBrain.FromJson(Json, KernelMode.Managed);
        var ext = new[] { 8f, 0f };
        for (int tick = 0; tick < 16; tick++)
        {
            Assert.AreEqual(json.Tick(ext), direct.Tick(ext), $"спайки на тике {tick}");
            CollectionAssert.AreEqual(json.CsrValues(), direct.CsrValues(),
                $"CSR на тике {tick}");
        }
    }

    static BrainBlueprint Blueprint() => new BrainBlueprint
    {
        N = 2,
        Dt = 0.5f,
        StepsPerTick = 4,
        Lif = new BrainBlueprint.LifSpec
        {
            TauRef = 2.2f, TauM = 20f, VRest = -52f, VThresh = -45f,
            VReset = -52f, TauSyn = 3f, TDelay = 1.8f,
        },
        Stdp = new BrainBlueprint.StdpSpec
        {
            TauPre = 20f, TauPost = 20f, TauE = 1000f, ALtp = .1f,
            ALtd = .005f, Lr = .15f, WMin = 0f, WMax = 6f,
        },
        Plasticity = new BrainBlueprint.PlasticitySpec
        {
            HomeoRate = .25f, RewardDelayTicks = 1, HomeoEveryTicks = 0,
        },
        Regions = new[]
        {
            new BrainBlueprint.RegionSpec { Tag = "S", Start = 0, End = 1 },
            new BrainBlueprint.RegionSpec { Tag = "M", Start = 1, End = 2 },
        },
        Gains = new[] { new[] { 0f, 1f }, new[] { 0f, 0f } },
        Nt = new[] { 1f, 1f },
        Edges = new[] { new BrainBlueprint.Edge { Pre = 0, Post = 1, Weight = 2f } },
    };

    const string Json = @"{
      ""n"":2, ""dt"":0.5, ""stepsPerTick"":4,
      ""lif"":{ ""tauRef"":2.2,""tauM"":20,""vRest"":-52,""vThresh"":-45,""vReset"":-52,""tauSyn"":3,""tDelay"":1.8},
      ""stdp"":{ ""tauPre"":20,""tauPost"":20,""tauE"":1000,""aLtp"":0.1,""aLtd"":0.005,""lr"":0.15,""wMin"":0,""wMax"":6},
      ""plasticity"":{ ""homeoRate"":0.25,""rewardDelayTicks"":1,""homeoEveryTicks"":0,""homeoExcludePre"":[]},
      ""regions"":[{ ""tag"":""S"",""start"":0,""end"":1,""inhibitory"":false},{""tag"":""M"",""start"":1,""end"":2,""inhibitory"":false}],
      ""gains"":[[0,1],[0,0]], ""nt"":[1,1], ""edges"":[[0,1,2]]
    }";
}
