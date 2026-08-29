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

        // Индексы регионов WL/WR (-1 — нет: стена идёт в SL/SR).
        readonly int _wallL, _wallR;
        float[] _ext;

        GoldenRunner(DiomaBrain brain, List<TickRow> rows)
        {
            Brain = brain;
            Rows = rows;
            _wallL = FindRegion(brain, "WL");
            _wallR = FindRegion(brain, "WR");
        }

        internal static int FindRegion(DiomaBrain brain, string tag)
        {
            for (int i = 0; i < brain.Regions.Length; i++)
                if (brain.Regions[i].Tag == tag)
                    return i;
            return -1;
        }

        DiomaBrain.Region Region(string tag)
        {
            int i = FindRegion(Brain, tag);
            if (i < 0)
                throw new InvalidOperationException($"в коннектоме нет региона {tag}");
            return Brain.Regions[i];
        }

        public static GoldenRunner Load(string dataDir, string prefix = "dioma")
        {
            var brain = DiomaBrain.FromJson(
                File.ReadAllText(Path.Combine(dataDir, prefix + "_connectome.json")));
            var golden = Json.Obj(Json.Parse(
                File.ReadAllText(Path.Combine(dataDir, prefix + "_golden.json"))));
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
            const float eatDopamine = 1.0f;
            const float wallDopamine = -0.5f;

            // Маршрутизация сенсоров по тегам (как в export_connectome.rs):
            // SL/SR ← запах, WL/WR ← стена; нет WL/WR — стена в SL/SR.
            var sl = Region("SL");
            var sr = Region("SR");
            var wl = _wallL >= 0 ? brain.Regions[_wallL] : sl;
            var wr = _wallR >= 0 ? brain.Regions[_wallR] : sr;
            var ml = Region("ML");
            var mr = Region("MR");

            if (_ext == null || _ext.Length != brain.N) _ext = new float[brain.N];
            var ext = _ext;
            for (int i = 0; i < brain.N; i++) ext[i] = basal;
            // Порядок сложений — как в export_connectome.rs (сначала запах,
            // затем стены), иначе ulp-расхождения в ulp-чувствительных тиках.
            for (int i = sl.Start; i < sl.End; i++) ext[i] += row.Sl * smellCurrent;
            for (int i = sr.Start; i < sr.End; i++) ext[i] += row.Sr * smellCurrent;
            for (int i = wl.Start; i < wl.End; i++) ext[i] += row.Wl * wallCurrent;
            for (int i = wr.Start; i < wr.End; i++) ext[i] += row.Wr * wallCurrent;

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
