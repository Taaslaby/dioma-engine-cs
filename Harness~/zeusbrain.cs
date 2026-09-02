// Мозг Зевса: первая схема, спроектированная АРХИТЕКТОРОМ (проводками),
// выращенная Прометеем и выпущенная в движок. Дифф-путь (руль) +
// CPG-качалка (тяга) + гейт (совпадение = ускорение).
//
// Смысл: до сих пор топологии были трёх видов — ручные (Layouts),
// статистические (Sardna), эволюционные (мутации Steering). Это
// четвёртый: ДЕКЛАРАТИВНЫЙ — что сказал архитектор, то и выросло.
using System;
using System.IO;
using System.Linq;
using System.Text;
using Dioma.Spikes;
using Prometheus.Runtime;
using Prometheus.Runtime.Grow;

class ZeusBrain
{
    const string Dst = "zeusbrain_dioma.json";

    public static int Run(string[] args)
    {
        // ── Архитектура (языком Зевса) ──
        // SL/SR — обоняние влево/вправо; TAIL — хвост-мотор (CPG качает
        // тягу); ML/MR — руль; гейт: еда ПРЯМО ПЕРЕД носом (SL и SR
        // вместе) → гейт открывает ML-путь (рывок к цели).
        var layout = Zeus.Sensorimotor()
            .And(Zeus.Differential("SL", "SR", "ML", "MR"))   // руль: антагонизм
            .And(Zeus.Cpg("I", "ML", "MR"))                   // тяга: I качает моторы
            .And(Zeus.Gate("SL", "SR", into: "TAIL"))         // совпадение → рывок
            .And(Zeus.Fan("SL", "I", "TAIL"))                // левое чуть-чуть в общий
            .And(Zeus.Fan("SR", "I", "TAIL"))                 // правое тоже
            .Build();
        layout.Validate();
        Console.WriteLine("[zb] архитектура: " +
            string.Join(",", layout.Regions.Select(r => r.Tag)) +
            $" | пар {layout.AllowedPairs.Count}");

        // ── Рост Прометеем ──
        var grown = ConnectomeGrow.Grow(layout, Steering.Default, 42);
        int n = grown.RegionOf.Length;
        Console.WriteLine($"[zb] вырос: {n} нейронов, {grown.Edges.Count} синапсов");

        // ── s2b-механика: конвертация в коннектом движка ──
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new StringBuilder(grown.Edges.Count * 24 + 4096);
        sb.Append("{\n \"n\":").Append(n).Append(", \"dt\":0.5, \"stepsPerTick\":100,\n");
        sb.Append(" \"lif\":{\"tauRef\":2.2,\"tauM\":20,\"vRest\":-52," +
            "\"vThresh\":-45,\"vReset\":-52,\"tauSyn\":3,\"tDelay\":1.8},\n");
        sb.Append(" \"stdp\":{\"tauPre\":20,\"tauPost\":20,\"tauE\":1000," +
            "\"aLtp\":0.1,\"aLtd\":0.005,\"lr\":0.15,\"wMin\":0,\"wMax\":10},\n");
        sb.Append(" \"plasticity\":{\"homeoRate\":0.25,\"rewardDelayTicks\":1," +
            "\"homeoEveryTicks\":0,\"homeoExcludePre\":[]},\n \"seed\":42,\n");

        var tags = grown.RegionTags;
        sb.Append(" \"regions\":[");
        int start = 0;
        for (int r = 0; r < tags.Length; r++)
        {
            int end = r + 1 < tags.Length
                ? Array.IndexOf(grown.RegionOf, r + 1, start) : n;
            if (end < 0) end = n;
            if (r > 0) sb.Append(',');
            bool inh = grown.Nt[start] < 0;
            sb.Append("{\"tag\":\"").Append(tags[r])
              .Append("\",\"start\":").Append(start)
              .Append(",\"end\":").Append(end)
              .Append(",\"inhibitory\":").Append(inh ? "true" : "false").Append('}');
            start = end;
        }
        sb.Append("],\n \"gains\":[");
        for (int a = 0; a < tags.Length; a++)
        {
            if (a > 0) sb.Append(',');
            sb.Append('[');
            for (int b = 0; b < tags.Length; b++) sb.Append(b > 0 ? ",1" : "1");
            sb.Append(']');
        }
        sb.Append("],\n \"nt\":[");
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(',');
            float v = grown.Nt[i];
            sb.Append(v == MathF.Floor(v) ? ((int)v).ToString(inv) : v.ToString("R", inv));
        }
        sb.Append("],\n \"edges\":[");
        for (int i = 0; i < grown.Edges.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('[').Append(grown.Edges[i].Pre).Append(',')
              .Append(grown.Edges[i].Post).Append(',')
              .Append(grown.Edges[i].Weight.ToString("R", inv)).Append(']');
        }
        sb.Append("]\n}\n");
        string json = sb.ToString();
        File.WriteAllText(Dst, json);

        // ── Мозг + архитектурные живости ──
        using var brain = DiomaBrain.FromJson(json);
        Console.WriteLine($"[zb] FromJson: n={brain.N}, syn={brain.SynapseCount}, " +
            $"regions={brain.Regions.Length}");

        var ext = new float[brain.N];
        int sl = brain.FindRegion("SL"), sr = brain.FindRegion("SR");
        int ml = brain.FindRegion("ML"), mr = brain.FindRegion("MR");
        int tail = brain.FindRegion("TAIL");
        var sums = new int[1];

        int Pool(int region, int ticks, bool leftOn, bool rightOn)
        {
            Array.Clear(ext, 0, ext.Length);
            if (leftOn)
                for (int i = brain.Regions[sl].Start; i < brain.Regions[sl].End; i++)
                    ext[i] = 4f;
            if (rightOn)
                for (int i = brain.Regions[sr].Start; i < brain.Regions[sr].End; i++)
                    ext[i] = 4f;
            var st = new[] { brain.Regions[region].Start };
            var en = new[] { brain.Regions[region].End };
            int acc = 0;
            for (int t = 0; t < ticks; t++)
            {
                brain.TickMotorPools(ext, st, en, sums);
                acc += sums[0];
                Array.Clear(sums, 0, 1);
            }
            return acc;
        }

        // 1) РУЛЬ: слева еда → левый мотор сильнее правого (антагонизм).
        int mlL = Pool(ml, 50, true, false), mrL = Pool(mr, 50, true, false);
        int mlR = Pool(ml, 50, false, true), mrR = Pool(mr, 50, false, true);
        bool steer = mlL + mrR > mlR + mrL;
        Console.WriteLine($"[zb] руль: еда слева → ML={mlL}/MR={mrL}; " +
            $"еда справа → ML={mlR}/MR={mrR} = " +
            (steer ? "СВОИ МОТОРЫ СИЛЬНЕЕ ЧУЖИХ (антагонизм работает)" :
                     "не повернулся"));

        // 2) ГЕЙТ-РЫВОК: еда прямо перед носом (оба) → TAIL.
        int tailBoth = Pool(tail, 50, true, true);
        int tailL = Pool(tail, 50, true, false);
        Console.WriteLine($"[zb] гейт-рывок: TAIL(оба)={tailBoth}, " +
            $"TAIL(один SL)={tailL} = " +
            (tailBoth > tailL ? "СОВПАДЕНИЕ КАЧАЕТ ХВОСТ СИЛЬНЕЕ ОДИНОЧКИ" :
                                 "гейт не различает"));

        // 3) Тишина: CPG не должен палить в пустоту (I-качалка тоническая
        //    — ждём умеренный фон, не эпилепсию).
        int idle = Pool(tail, 30, false, false);
        Console.WriteLine($"[zb] CPG-фон в тишине: {idle} сп. " +
            (idle < tailBoth * 3 ? "(умеренный)" : "(штормит)"));

        bool ok = steer && tailBoth > tailL;
        Console.WriteLine(ok ? "ИТОГ: PASS" : "ИТОГ: FAIL");
        return ok ? 0 : 1;
    }
}
