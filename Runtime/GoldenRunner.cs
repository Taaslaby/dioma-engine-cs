// Воспроизведение «золотого следа» на C#-ядре: упрощённый контракт
// существа (этап 1), зеркало цикла из export_connectome.rs.

using System;
using System.Collections.Generic;
using System.IO;

namespace Dioma.Spikes
{
    public sealed class GoldenRunner
    {
        public sealed class TickRow
        {
            public float Sl, Sr, Wl, Wr;
            public bool Eat, Wall;
            // Эталон из JSON (Rust):
            public int MlRef, MrRef;
            public float SteerRef;
            public long SpikesRef;
            public double WsumRef;
            // Наш прогон:
            public int Ml, Mr;
            public long Spikes;
            public double Wsum;
        }

        public readonly DiomaBrain Brain;
        public readonly List<TickRow> Rows;
        public int Cursor;

        GoldenRunner(DiomaBrain brain, List<TickRow> rows)
        {
            Brain = brain;
            Rows = rows;
        }

        public static GoldenRunner Load(string dataDir)
        {
            var brain = DiomaBrain.FromJson(
                File.ReadAllText(Path.Combine(dataDir, "dioma_connectome.json")));
            var golden = Json.Obj(Json.Parse(
                File.ReadAllText(Path.Combine(dataDir, "dioma_golden.json"))));
            var rows = new List<TickRow>();
            foreach (var t in Json.Arr(golden["ticks"]))
            {
                var o = Json.Obj(t);
                rows.Add(new TickRow
                {
                    Sl = Json.F(o["sl"]),
                    Sr = Json.F(o["sr"]),
                    Wl = Json.F(o["wl"]),
                    Wr = Json.F(o["wr"]),
                    Eat = Json.Bool(o["eat"]),
                    Wall = Json.Bool(o["wall"]),
                    MlRef = (int)Json.Num(o["ml"]),
                    MrRef = (int)Json.Num(o["mr"]),
                    SteerRef = Json.F(o["steer"]),
                    SpikesRef = (long)Json.Num(o["spikes"]),
                    WsumRef = Json.Num(o["wsum"]),
                });
            }
            return new GoldenRunner(brain, rows);
        }

        /// Прогнать один тик упрощённого существа на C#-мозге.
        public TickRow Step()
        {
            var row = Rows[Cursor];
            var brain = Brain;

            const float basal = 0.12f;
            const float smellCurrent = 4.0f;
            const float wallCurrent = 4.0f;
            const float steerScale = 0.25f;
            const float eatDopamine = 1.0f;
            const float wallDopamine = -0.5f;

            var sl = brain.Regions[0];
            var sr = brain.Regions[1];
            var ml = brain.Regions[3];
            var mr = brain.Regions[4];

            var ext = new float[brain.N];
            for (int i = 0; i < brain.N; i++) ext[i] = basal;
            // Порядок сложений — как в export_connectome.rs (сначала запах,
            // затем стены), иначе ulp-расхождения в ulp-чувствительных тиках.
            for (int i = sl.Start; i < sl.End; i++) ext[i] += row.Sl * smellCurrent;
            for (int i = sr.Start; i < sr.End; i++) ext[i] += row.Sr * smellCurrent;
            for (int i = sl.Start; i < sl.End; i++) ext[i] += row.Wl * wallCurrent;
            for (int i = sr.Start; i < sr.End; i++) ext[i] += row.Wr * wallCurrent;

            var (mlSum, mrSum, spikes) =
                brain.TickMotor(ext, ml.Start, ml.End, mr.Start, mr.End);

            if (row.Eat) brain.ScheduleReward(eatDopamine);
            if (row.Wall) brain.RewardNow(wallDopamine);

            row.Ml = mlSum;
            row.Mr = mrSum;
            row.Spikes = spikes;
            row.Wsum = brain.WeightSum();
            Cursor++;
            return row;
        }

        public float SteerOf(TickRow row) =>
            Math.Clamp((row.Mr - row.Ml) * 0.25f, -1f, 1f);
    }
}
