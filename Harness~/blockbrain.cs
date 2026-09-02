// Первая схема, собранная Прометеем-Билдером: Core (рыбий холст) +
// CoincidenceGate (ГАМК-затвор: SL∩SR открывает мотор) + Comparator
// (прогноз HC vs факт: ERR-затвор). Рост → s2b-механика (регионы,
// гейны) → DiomaBrain → живость.
//
// Смысл сборки: проверить, что блоки не просто растут по отдельности
// (50/50 xunit), а собираются в РАБОЧИЙ МОЗГ одной цепочкой —
// как печатная плата из микросхем.
using System;
using System.IO;
using System.Linq;
using System.Text;
using Dioma.Data;
using Dioma.Spikes;
using Prometheus.Runtime;
using Prometheus.Runtime.Grow;

class BlockBrain
{
    const string Dst = "blockbrain_dioma.json";

    public static int Run(string[] args)
    {
        // ── Сборка схемы из блоков ──
        // GATE: совпадение SL и SR (еда ПРЯМО ПЕРЕД носом) открывает ML.
        // CMP: сравнение — сенсорный факт vs PRED-регион (носитель
        //      прогноза): несовпадение оставляет ERR открытым.
        // HostWire: источники растут к реле-приманкам блоков (правило
        // Прометея: растёт владелец аксона; реле секретирует).
        var layout = Blocks.Core()
            .And(Blocks.CoincidenceGate("G1", "SL", "SR", "ML"))
            .And(Blocks.Comparator("C1", "PRED", "SR", "MR", "TAIL"))
            .And(b => b.Region("PRED").At(-20, -80, 0).Radius(8).Count(12)
                .Axon("C1.A", 60))
            .AxonTo("SL", "G1.A", 70)
            .AxonTo("SR", "G1.B", 70)
            .Build();
        layout.Validate();
        Console.WriteLine("[bb] схема: регионы " +
            string.Join(",", layout.Regions.Select(r => r.Tag)) +
            $" | пар {layout.AllowedPairs.Count}");

        // ── Рост ──
        var grown = ConnectomeGrow.Grow(layout, Steering.Default, 42);
        Console.WriteLine($"[bb] вырос: {grown.RegionOf.Length} нейронов, " +
            $"{grown.Edges.Count} синапсов");
        int n = grown.RegionOf.Length;

        // ── s2b-механика: регионы по блокам (теги уже блоковые) ──
        // Норм-веса: блоки дают рёбра с grown-весами (логнорм, медиана ~2):
        // как в s2b — единичные гейны внутри, смысловые межблочные.
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new StringBuilder(grown.Edges.Count * 24 + 4096);
        sb.Append("{\n \"n\":").Append(n)
          .Append(", \"dt\":0.5, \"stepsPerTick\":100,\n");
        sb.Append(" \"lif\":{\"tauRef\":2.2,\"tauM\":20,\"vRest\":-52," +
            "\"vThresh\":-45,\"vReset\":-52,\"tauSyn\":3,\"tDelay\":1.8},\n");
        sb.Append(" \"stdp\":{\"tauPre\":20,\"tauPost\":20,\"tauE\":1000," +
            "\"aLtp\":0.1,\"aLtd\":0.005,\"lr\":0.15,\"wMin\":0,\"wMax\":10},\n");
        sb.Append(" \"plasticity\":{\"homeoRate\":0.25,\"rewardDelayTicks\":1," +
            "\"homeoEveryTicks\":0,\"homeoExcludePre\":[]},\n \"seed\":42,\n");

        // Регионы: сплошные блоки в порядке роста.
        var tags = grown.RegionTags;
        sb.Append(" \"regions\":[");
        int start = 0;
        for (int r = 0; r < tags.Length; r++)
        {
            int end = r + 1 < tags.Length
                ? Array.IndexOf(grown.RegionOf, r + 1, start)
                : n;
            if (end < 0) end = n;
            if (r > 0) sb.Append(',');
            bool inh = grown.Nt[start] < 0;
            sb.Append("{\"tag\":\"").Append(tags[r])
              .Append("\",\"start\":").Append(start)
              .Append(",\"end\":").Append(end)
              .Append(",\"inhibitory\":").Append(inh ? "true" : "false")
              .Append('}');
            start = end;
        }
        sb.Append("],\n");

        // Гейны: единичная матрица (внутриблочные grown-веса сами несут
        // смысл: затвор уже зашит в схему — DIS тормозит GATE).
        sb.Append(" \"gains\":[");
        for (int a = 0; a < tags.Length; a++)
        {
            if (a > 0) sb.Append(',');
            sb.Append('[');
            for (int b = 0; b < tags.Length; b++)
                sb.Append(b > 0 ? ",1" : "1");
            sb.Append(']');
        }
        sb.Append("],\n \"nt\":[");
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(',');
            float v = grown.Nt[i];
            sb.Append(v == MathF.Floor(v) ? ((int)v).ToString(inv)
                                          : v.ToString("R", inv));
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
        File.WriteAllText(Dst, json);   // (первая версия забыла записать)
        Console.WriteLine($"[bb] коннектом: {json.Length / 1024:F0} KB → {Dst}");

        // ── Мозг + живость ──
        using var brain = DiomaBrain.FromJson(json);
        Console.WriteLine($"[bb] FromJson: n={brain.N}, syn={brain.SynapseCount}, " +
            $"regions={brain.Regions.Length}");

        // Затвор в деле: SL+SR вместе → ML-пул. Один вход → тише.
        var ext = new float[brain.N];
        int sl = brain.FindRegion("SL"), sr = brain.FindRegion("SR");
        int ml = brain.FindRegion("ML");
        var ms = new[] { brain.Regions[ml].Start };
        var me = new[] { brain.Regions[ml].End };
        var sums = new int[1];

        int Drive(int mode, int ticks)
        {
            Array.Clear(ext, 0, ext.Length);
            if (mode != 1)
                for (int i = brain.Regions[sl].Start; i < brain.Regions[sl].End; i++)
                    ext[i] = 4f;
            if (mode != 0)
                for (int i = brain.Regions[sr].Start; i < brain.Regions[sr].End; i++)
                    ext[i] = 4f;
            int acc = 0;
            for (int t = 0; t < ticks; t++)
            {
                brain.TickMotorPools(ext, ms, me, sums);
                acc += sums[0];
                Array.Clear(sums, 0, 1);
            }
            return acc;
        }
        int onlyL = Drive(0, 50), onlyR = Drive(1, 50), both = Drive(2, 50);
        Console.WriteLine($"[bb] затвор: SL={onlyL} SR={onlyR} ОБА={both} " +
            $"(гейт: {both} > {onlyL + onlyR} = {(both > onlyL + onlyR ? "СРАБОТАЛ — синергия входов" : "не сложился")})");

        // Comparator: факт SR без прогноза PRED → ERR (TAIL) гребёт.
        int pred = brain.FindRegion("PRED");
        int tail = brain.FindRegion("TAIL");
        var ts = new[] { brain.Regions[tail].Start };
        var te = new[] { brain.Regions[tail].End };
        int tAcc = 0;
        Array.Clear(ext, 0, ext.Length);
        for (int i = brain.Regions[sr].Start; i < brain.Regions[sr].End; i++)
            ext[i] = 4f;
        for (int t = 0; t < 30; t++)
        {
            brain.TickMotorPools(ext, ts, te, sums);
            tAcc += sums[0];
            Array.Clear(sums, 0, 1);
        }
        Console.WriteLine($"[bb] компаратор: SR-факт без PRED → TAIL-удивление {tAcc} сп. " +
            $"(ERR-путь жив: {tAcc > 0})");

        bool ok = brain.SynapseCount > 0 && both > 0 && tAcc > 0;
        Console.WriteLine(ok ? "ИТОГ: PASS" : "ИТОГ: FAIL");
        return ok ? 0 : 1;
    }
}
