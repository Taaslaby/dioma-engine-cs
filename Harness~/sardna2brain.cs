// Мост sardaanaCM → диома: TAAS2-коннектом → коннектом-JSON движка
// (DiomaBrain.FromJson). У taas нет регионов и гейнов — их назначает
// этот конвертер по статистике сети:
//
//   роли: сенсоры  = нейроны с НУЛЕВЫМ/низким in-degree (никто не
//                    входит — им остаётся слушать «внешний мир»);
//         моторы   = топ по out-degree (они командуют);
//         интерн.  = тормозные (nt<0) — как I рыбки;
//   блоки: SL/SR/I/ML/MR/TAIL — сплошные, как требует Regions,
//         перенумерация рёбер вслед за сортировкой нейронов.
//
// Гейны: единичная матрица разрешает всё внутри мозга, входы/выходы
//         блока — по смыслу рыбки (SL→ML, SL→TAIL, ... из Species.Fish).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Dioma.Data;
using Dioma.Spikes;

class Sardna2Brain
{
    const int WantSense = 20;   // как SL/SR рыбки (20+20)

    public static int Run(string[] args)
    {
        string src = args.Length > 0 ? args[0]
            : @"D:\Проекты\asm_engines\sardaanaCM\experiments\sardna_ultra\generated\celegans_male_n256_t0_85.taas";
        string dst = args.Length > 1 ? args[1]
            : Path.Combine(Path.GetDirectoryName(src)!, "celegans_male_dioma.json");

        var r = TaasReader.FromFile(src);
        int[] rp = r.I32("row_ptr");
        int[] ci = r.I32("col_indices");
        float[] vals = r.F32("values");
        float[] nt = r.F32("nt_types");
        int n = rp.Length - 1;
        Console.WriteLine($"[s2b] {Path.GetFileName(src)}: n={n}, syn={vals.Length}");

        // ── in/out degrees ──
        var outDeg = new int[n];
        for (int i = 0; i < n; i++) outDeg[i] = rp[i + 1] - rp[i];
        var inDeg = new int[n];
        for (int idx = 0; idx < ci.Length; idx++) inDeg[ci[idx]]++;

        // ── роли: интернейроны = тормозные; сенсоры = min in-degree
        // среди возбуждающих; моторы = топ out-degree из остатка ──
        var inhib = Enumerable.Range(0, n).Where(i => nt[i] < 0).ToArray();
        var excit = Enumerable.Range(0, n).Where(i => nt[i] >= 0).ToArray();

        var sensors = excit.OrderBy(i => inDeg[i]).ThenBy(i => outDeg[i])
                           .Take(WantSense * 2).ToArray();
        var senseSet = new HashSet<int>(sensors);
        var motors = excit.Where(i => !senseSet.Contains(i))
                          .OrderByDescending(i => outDeg[i])
                          .Take(n / 3).ToArray();   // ML+MR+TAIL треть сети
        var motorSet = new HashSet<int>(motors);

        // SL — первые WantSense, SR — следующие (детерминизм: по индексу)
        Array.Sort(sensors);
        var sl = sensors.Take(WantSense).ToArray();
        var sr = sensors.Skip(WantSense).Take(WantSense).ToArray();
        // Три моторных блока ровными третями: TAIL не должен остаться пустым
        // (первый прогон: n/4 моторов / 2 половины = TAIL 0 — FAIL).
        Array.Sort(motors);
        int third = motors.Length / 3;
        var ml = motors.Take(third).ToArray();
        var mr = motors.Skip(third).Take(third).ToArray();
        var tail = motors.Skip(third * 2).ToArray();

        var blockOf = new int[n];
        Array.Fill(blockOf, -1);
        foreach (var i in inhib) blockOf[i] = 2;   // I
        foreach (var i in sl) blockOf[i] = 0;      // SL
        foreach (var i in sr) blockOf[i] = 1;      // SR
        foreach (var i in ml) blockOf[i] = 3;      // ML
        foreach (var i in mr) blockOf[i] = 4;      // MR
        foreach (var i in tail) blockOf[i] = 5;    // TAIL

        // Внутриблочные заблокированные выше перекрываются? Порядок
        // присвоения: inhib первыми, потом роли перезапишут exc.
        // Остаток (не выбранные) → I-блок (реле).
        for (int i = 0; i < n; i++)
            if (blockOf[i] < 0) blockOf[i] = 2;

        // ── порядок нейронов: блоками [SL, SR, I, ML, MR, TAIL] ──
        var order = new List<int>(n);
        for (int b = 0; b < 6; b++)
            for (int i = 0; i < n; i++)
                if (blockOf[i] == b) order.Add(i);
        var newIdx = new int[n];
        for (int pos = 0; pos < n; pos++) newIdx[order[pos]] = pos;

        // ── рёбра с перенумерацией ──
        var edges = new List<(int Pre, int Post, float W)>(ci.Length);
        for (int pre = 0; pre < n; pre++)
            for (int k = rp[pre]; k < rp[pre + 1]; k++)
                edges.Add((newIdx[pre], newIdx[ci[k]], vals[k]));

        // ── регионы по блокам нового порядка (сплошные по построению) ──
        string[] tags = { "SL", "SR", "I", "ML", "MR", "TAIL" };
        var starts = new int[6];
        var ends = new int[6];
        for (int b = 0; b < 6; b++)
        {
            int s = order.FindIndex(i => blockOf[i] == b);
            // блоки сплошные в новом порядке — пересчитаем по newIdx
        }
        // проще: по blockOf нового индекса
        var nb = new int[n];
        for (int pos = 0; pos < n; pos++) nb[pos] = blockOf[order[pos]];
        int cur = nb[0]; starts[0] = 0;
        for (int pos = 1; pos <= n; pos++)
        {
            if (pos == n || nb[pos] != cur)
            {
                ends[cur] = pos;
                if (pos < n) { cur = nb[pos]; starts[cur] = pos; }
            }
        }

        // ── гейны: как рыбка (Species.Fish/DiomaBridge.Fish) ──
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new StringBuilder(edges.Count * 24 + 2048);
        sb.Append("{\n \"n\":").Append(n).Append(", \"dt\":0.5, \"stepsPerTick\":100,\n");
        sb.Append(" \"lif\":{\"tauRef\":2.2,\"tauM\":20,\"vRest\":-52,\"vThresh\":-45," +
                  "\"vReset\":-52,\"tauSyn\":3,\"tDelay\":1.8},\n");
        sb.Append(" \"stdp\":{\"tauPre\":20,\"tauPost\":20,\"tauE\":1000,\"aLtp\":0.1," +
                  "\"aLtd\":0.005,\"lr\":0.15,\"wMin\":0,\"wMax\":10},\n");
        sb.Append(" \"plasticity\":{\"homeoRate\":0.25,\"rewardDelayTicks\":1," +
                  "\"homeoEveryTicks\":0,\"homeoExcludePre\":[]},\n");
        sb.Append(" \"seed\":0,\n \"regions\":[");
        bool any = false;
        for (int b = 0; b < 6; b++)
        {
            if (ends[b] <= starts[b]) continue;   // пустой блок пропустить
            if (any) sb.Append(',');
            any = true;
            bool inh = b == 2;
            sb.Append("{\"tag\":\"").Append(tags[b])
              .Append("\",\"start\":").Append(starts[b])
              .Append(",\"end\":").Append(ends[b])
              .Append(",\"inhibitory\":").Append(inh ? "true" : "false")
              .Append('}');
        }
        sb.Append("],\n \"gains\":[");
        // Гейны DiomaBridge.Fish: SL/SR→ML/MR/I, I→ML/MR/TAIL, SL/SR→TAIL
        float G(int a, int b) => (a, b) switch
        {
            (0, 3) or (1, 4) => 1.0f,   // SL→ML, SR→MR
            (0, 2) or (1, 2) => 0.8f,   // →I
            (2, 3) or (2, 4) => 1.0f,   // I→ML/MR
            (2, 5) => 0.4f,             // I→TAIL
            (0, 5) or (1, 5) => 1.2f,   // SL/SR→TAIL
            _ => 0.15f,                 // внутриблочная рекуррентность жива
        };
        for (int a = 0; a < 6; a++)
        {
            if (a > 0) sb.Append(',');
            sb.Append('[');
            for (int b = 0; b < 6; b++)
            {
                if (b > 0) sb.Append(',');
                float g = G(a, b);
                sb.Append(g == MathF.Floor(g) ? ((int)g).ToString(inv)
                                              : g.ToString("R", inv));
            }
            sb.Append(']');
        }
        sb.Append("],\n \"nt\":[");
        for (int pos = 0; pos < n; pos++)
        {
            if (pos > 0) sb.Append(',');
            float v = nt[order[pos]];
            sb.Append(v == MathF.Floor(v) ? ((int)v).ToString(inv) : v.ToString("R", inv));
        }
        sb.Append("],\n \"edges\":[");
        for (int i = 0; i < edges.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('[').Append(edges[i].Pre).Append(',')
              .Append(edges[i].Post).Append(',')
              .Append(edges[i].W.ToString("R", inv)).Append(']');
        }
        sb.Append("]\n}\n");
        File.WriteAllText(dst, sb.ToString());

        Console.WriteLine($"[s2b] блоки: SL={sl.Length} SR={sr.Length} I={inhib.Length} " +
                          $"ML={ml.Length} MR={mr.Length} TAIL={tail.Length}");
        Console.WriteLine($"[s2b] -> {dst} ({new FileInfo(dst).Length / 1024:F0} KB)");

        var json = File.ReadAllText(dst);
        using var brain = DiomaBrain.FromJson(json);
        Console.WriteLine($"[s2b] FromJson: n={brain.N}, syn={brain.SynapseCount}, " +
                          $"regions={brain.Regions.Length}");
        foreach (var tag in tags)
            Console.WriteLine($"  {tag}: {(brain.FindRegion(tag) >= 0 ? "ok" : "НЕТ")}");
        long spikes = brain.Tick(new float[brain.N]);
        Console.WriteLine($"[s2b] тик тишины: спайков {spikes}");
        // Живость: тонус в сенсоры — мозг обязан откликнуться спайками
        // (иначе конвертер собрал глухую проводку, и PASS был бы пустым).
        var ext = new float[brain.N];
        int slR = brain.FindRegion("SL"), srR = brain.FindRegion("SR");
        for (int i = brain.Regions[slR].Start; i < brain.Regions[slR].End; i++) ext[i] = 4f;
        for (int i = brain.Regions[srR].Start; i < brain.Regions[srR].End; i++) ext[i] = 4f;
        long alive = 0;
        for (int t = 0; t < 50; t++) alive += brain.Tick(ext);
        Console.WriteLine($"[s2b] 50 тиков с током сенсоров: спайков {alive}");
        bool ok = brain.FindRegion("SL") >= 0 && brain.FindRegion("TAIL") >= 0
                  && brain.SynapseCount > 0 && alive > 0;
        Console.WriteLine(ok ? "ИТОГ: PASS" : "ИТОГ: FAIL");
        return ok ? 0 : 1;
    }
}
