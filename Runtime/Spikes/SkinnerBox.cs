// Протоколы обучения: ящик Скиннера (оперантное R-STDP). Проверяют не
// паритет, а СПОСОБНОСТЬ УЧИТЬСЯ — награждаемый путь усиливается,
// ненаграждаемые остаются на месте.
//
// Порт `dioma/src/bin/export_learning.rs`; сценарий и эталонные веса лежат
// в `dioma_skinner_golden.json`.

using Dioma.Data;
using System;
using System.Collections.Generic;
using System.IO;

namespace Dioma.Spikes
{
    public sealed class SkinnerBox
    {
        public struct Trial
        {
            public int Target;         // какой сенсор стимулировали
            public int Fired;          // спайков «своего» мотора
            public bool Rewarded;
            public float[] W;          // веса диагонали после пробы
            public double Wsum;
        }

        public readonly DiomaBrain Brain;
        public readonly float Basal, Stimulus, Reward, W0;
        public readonly int GapTicks, TrialTicks, Sensors;

        readonly float[] _ext;
        readonly float[] _quiet;

        public int TrialsRun { get; private set; }

        public SkinnerBox(DiomaBrain brain, float basal, float stimulus, float reward,
                          int gapTicks, int trialTicks, float w0)
        {
            Brain = brain;
            Basal = basal;
            Stimulus = stimulus;
            Reward = reward;
            GapTicks = gapTicks;
            TrialTicks = trialTicks;
            W0 = w0;
            Sensors = brain.Regions[0].End - brain.Regions[0].Start;
            _ext = new float[brain.N];
            _quiet = new float[brain.N];
            for (int i = 0; i < brain.N; i++) _quiet[i] = basal;
        }

        /// Загрузить протокол из ассетов: коннектом + параметры сценария.
        public static SkinnerBox Load(string dataDir, out List<Trial> golden)
        {
            var brain = DiomaBrain.FromJson(
                File.ReadAllText(Path.Combine(dataDir, "dioma_skinner_connectome.json")));
            var root = Json.Obj(Json.Parse(
                File.ReadAllText(Path.Combine(dataDir, "dioma_skinner_golden.json"))));

            golden = new List<Trial>();
            foreach (var t in Json.Arr(root["trials"]))
            {
                var o = Json.Obj(t);
                var w = Json.Arr(o["w"]);
                var trial = new Trial
                {
                    Target = (int)Json.Num(o["target"]),
                    Fired = (int)Json.Num(o["fired"]),
                    Rewarded = Json.Bool(o["rewarded"]),
                    Wsum = Json.Num(o["wsum"]),
                    W = new float[w.Count],
                };
                for (int i = 0; i < w.Count; i++) trial.W[i] = Json.F(w[i]);
                golden.Add(trial);
            }

            return new SkinnerBox(
                brain,
                Json.F(root["basal"]),
                Json.F(root["stimulus"]),
                Json.F(root["reward"]),
                (int)Json.Num(root["gapTicks"]),
                (int)Json.Num(root["trialTicks"]),
                Json.F(root["w0"]));
        }

        /// Одна проба: стимул на сенсор `target`, награда если это
        /// награждаемый сенсор и «свой» мотор стрелял, затем холостой зазор
        /// (eligibility затухает — иначе следующая награда кредитует и этот путь).
        public Trial Step(int target, int rewardedSensor = 0)
        {
            var sensors = Brain.Regions[0];
            var motors = Brain.Regions[1];

            for (int i = 0; i < _ext.Length; i++) _ext[i] = Basal;
            _ext[sensors.Start + target] += Stimulus;

            int fired = 0;
            int m = motors.Start + target;
            for (int t = 0; t < TrialTicks; t++)
            {
                var (ml, _, _) = Brain.TickMotor(_ext, m, m + 1, motors.Start, motors.Start + 1);
                fired += ml;
            }

            bool rewarded = target == rewardedSensor && fired > 0;
            if (rewarded)
                Brain.RewardNow(Reward);
            for (int g = 0; g < GapTicks; g++)
                Brain.Tick(_quiet);

            TrialsRun++;
            var vals = Brain.CsrValues();
            var w = new float[Sensors];
            Array.Copy(vals, w, Math.Min(Sensors, vals.Length));
            return new Trial
            {
                Target = target,
                Fired = fired,
                Rewarded = rewarded,
                W = w,
                Wsum = Brain.WeightSum(),
            };
        }

        /// Избирательность: во сколько раз награждаемый путь сильнее среднего
        /// ненаграждаемого. 1.0 = обучение неизбирательно (все пути растут).
        public double Selectivity(int rewardedSensor = 0)
        {
            var vals = Brain.CsrValues();
            double others = 0;
            int count = 0;
            for (int i = 0; i < Sensors && i < vals.Length; i++)
            {
                if (i == rewardedSensor)
                    continue;
                others += vals[i];
                count++;
            }
            if (count == 0 || others <= 0)
                return double.PositiveInfinity;
            return vals[rewardedSensor] / (others / count);
        }
    }
}
