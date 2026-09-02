// Типизированная форма входа DiomaBrain. В отличие от JSON-ассета, она
// позволяет игровому рантайму передавать выращенный коннектом без
// сериализации/парсинга строк. JSON остаётся форматом файлов и совместимости.

using System;
using System.Collections.Generic;

namespace Dioma.Spikes
{
    public sealed class BrainBlueprint
    {
        public sealed class LifSpec
        {
            public float TauRef, TauM, VRest, VThresh, VReset, TauSyn, TDelay;
        }

        public sealed class StdpSpec
        {
            public float TauPre, TauPost, TauE, ALtp, ALtd, Lr, WMin, WMax;
        }

        public sealed class PlasticitySpec
        {
            public float HomeoRate;
            public int RewardDelayTicks, HomeoEveryTicks;
            public int[] HomeoExcludePre = Array.Empty<int>();
        }

        public struct RegionSpec
        {
            public string Tag;
            public int Start, End;
            public bool Inhibitory;
        }

        public struct Edge
        {
            public int Pre, Post;
            public float Weight;
        }

        public int N;
        public float Dt;
        public int StepsPerTick;
        public LifSpec Lif = new LifSpec();
        public StdpSpec Stdp = new StdpSpec();
        public PlasticitySpec Plasticity = new PlasticitySpec();
        public RegionSpec[] Regions = Array.Empty<RegionSpec>();
        /// Матрица gain[preRegion][postRegion].
        public float[][] Gains = Array.Empty<float[]>();
        public float[] Nt = Array.Empty<float>();
        public Edge[] Edges = Array.Empty<Edge>();
        public NeuronModel Model = NeuronModel.AlphaLif;
        /// Плоский профиль HAAP: N строк по HaapCols.N чисел.
        public float[] HaapProfile;

        internal Dictionary<string, object> ToObjectGraph()
        {
            Validate();
            var root = new Dictionary<string, object>
            {
                ["n"] = (double)N,
                ["dt"] = (double)Dt,
                ["stepsPerTick"] = (double)StepsPerTick,
                ["lif"] = new Dictionary<string, object>
                {
                    ["tauRef"] = (double)Lif.TauRef, ["tauM"] = (double)Lif.TauM,
                    ["vRest"] = (double)Lif.VRest, ["vThresh"] = (double)Lif.VThresh,
                    ["vReset"] = (double)Lif.VReset, ["tauSyn"] = (double)Lif.TauSyn,
                    ["tDelay"] = (double)Lif.TDelay,
                },
                ["stdp"] = new Dictionary<string, object>
                {
                    ["tauPre"] = (double)Stdp.TauPre, ["tauPost"] = (double)Stdp.TauPost,
                    ["tauE"] = (double)Stdp.TauE, ["aLtp"] = (double)Stdp.ALtp,
                    ["aLtd"] = (double)Stdp.ALtd, ["lr"] = (double)Stdp.Lr,
                    ["wMin"] = (double)Stdp.WMin, ["wMax"] = (double)Stdp.WMax,
                },
                ["plasticity"] = new Dictionary<string, object>
                {
                    ["homeoRate"] = (double)Plasticity.HomeoRate,
                    ["rewardDelayTicks"] = (double)Plasticity.RewardDelayTicks,
                    ["homeoEveryTicks"] = (double)Plasticity.HomeoEveryTicks,
                    ["homeoExcludePre"] = Numbers(Plasticity.HomeoExcludePre),
                },
                ["regions"] = RegionsGraph(),
                ["gains"] = GainsGraph(),
                ["nt"] = Numbers(Nt),
                ["edges"] = EdgesGraph(),
                ["model"] = ModelName(Model),
            };
            if (HaapProfile != null)
                root["haapProfile"] = HaapGraph();
            return root;
        }

        void Validate()
        {
            if (N <= 0 || Dt <= 0f || StepsPerTick <= 0)
                throw new ArgumentException("BrainBlueprint: n, dt и stepsPerTick должны быть положительными");
            if (Lif == null || Stdp == null || Plasticity == null)
                throw new ArgumentException("BrainBlueprint: отсутствуют физиологические параметры");
            if (Regions == null || Regions.Length == 0 || Nt == null || Nt.Length != N)
                throw new ArgumentException("BrainBlueprint: регионы или nt не согласованы с n");
            if (Gains == null || Gains.Length != Regions.Length)
                throw new ArgumentException("BrainBlueprint: матрица gains не согласована с регионами");
            for (int i = 0; i < Regions.Length; i++)
            {
                if (Regions[i].Start < 0 || Regions[i].End < Regions[i].Start || Regions[i].End > N)
                    throw new ArgumentException($"BrainBlueprint: некорректные границы региона {i}");
                if (Gains[i] == null || Gains[i].Length != Regions.Length)
                    throw new ArgumentException($"BrainBlueprint: строка gains {i} имеет неверную длину");
            }
            if (Edges == null) throw new ArgumentException("BrainBlueprint: edges не задан");
            foreach (var edge in Edges)
                if (edge.Pre < 0 || edge.Pre >= N || edge.Post < 0 || edge.Post >= N)
                    throw new ArgumentException("BrainBlueprint: ребро выходит за n");
            if (Model == NeuronModel.Haap &&
                (HaapProfile == null || HaapProfile.Length != N * HaapCols.N))
                throw new ArgumentException("BrainBlueprint: HAAP требует профиль на каждого нейрона");
        }

        List<object> RegionsGraph()
        {
            var result = new List<object>(Regions.Length);
            foreach (var r in Regions)
                result.Add(new Dictionary<string, object>
                {
                    ["tag"] = r.Tag, ["start"] = (double)r.Start,
                    ["end"] = (double)r.End, ["inhibitory"] = r.Inhibitory,
                });
            return result;
        }

        List<object> GainsGraph()
        {
            var result = new List<object>(Gains.Length);
            foreach (var row in Gains) result.Add(Numbers(row));
            return result;
        }

        List<object> EdgesGraph()
        {
            var result = new List<object>(Edges.Length);
            foreach (var edge in Edges)
                result.Add(new List<object> { (double)edge.Pre, (double)edge.Post, (double)edge.Weight });
            return result;
        }

        List<object> HaapGraph()
        {
            var result = new List<object>(N);
            for (int neuron = 0; neuron < N; neuron++)
            {
                var row = new List<object>(HaapCols.N);
                for (int col = 0; col < HaapCols.N; col++)
                    row.Add((double)HaapProfile[neuron * HaapCols.N + col]);
                result.Add(row);
            }
            return result;
        }

        static List<object> Numbers(int[] values)
        {
            var result = new List<object>(values?.Length ?? 0);
            if (values != null) foreach (var value in values) result.Add((double)value);
            return result;
        }

        static List<object> Numbers(float[] values)
        {
            var result = new List<object>(values?.Length ?? 0);
            if (values != null) foreach (var value in values) result.Add((double)value);
            return result;
        }

        static string ModelName(NeuronModel model)
        {
            switch (model)
            {
                case NeuronModel.AlphaLif: return "alpha-lif";
                case NeuronModel.Lif: return "lif";
                case NeuronModel.Srm0: return "srm0";
                case NeuronModel.Haap: return "haap";
                default: throw new ArgumentOutOfRangeException(nameof(model));
            }
        }
    }
}
