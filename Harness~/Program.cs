// Быстрый харнесс: гоняет проверки движка без Unity-редактора.
//   dotnet run -c Release --project "Harness~/smoke.csproj"            → паритет мозга
//   dotnet run -c Release --project "Harness~/smoke.csproj" -- bio     → паритет биохимии
//   dotnet run -c Release --project "Harness~/smoke.csproj" -- skinner → протокол обучения
//   dotnet run -c Release --project "Harness~/smoke.csproj" -- arena   → смоук арены
//   dotnet run -c Release --project "Harness~/smoke.csproj" -- all     → все четыре
// Код выхода 0 = PASS, 1 = FAIL, 2 = неизвестный режим; scripts/verify.sh
// смотрит именно на него, а не на текст.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Dioma.Bio;
using Dioma.Data;
using Dioma.Spikes;
using Dioma.World;

class Program
{
    static string _dataDir;

    /// Эталоны лежат в пакете (`Tests/Editor/Data`). Ищем их подъёмом от
    /// каталога сборки, поэтому путь не зависит ни от cwd, ни от глубины
    /// `bin/Release/net8.0`.
    static string DataDir => _dataDir ??= FindDataDir();

    static string FindDataDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Tests", "Editor", "Data");
            if (File.Exists(Path.Combine(candidate, "dioma_connectome.json")))
                return candidate;
        }
        throw new DirectoryNotFoundException(
            "не найден Tests/Editor/Data пакета (искал вверх от " + AppContext.BaseDirectory + ")");
    }

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "parity";
        switch (mode)
        {
            case "parity":
                return Parity("dioma") | Parity("dioma_c");
            case "bio":
                return Bio();
            case "skinner":
                return Skinner();
            case "arena":
                return Arena();
            case "bench-haap":
                return BenchHaap();
            case "taas2":
                return TaasCheck.Run(new string[0]);
            case "s2b":
                return Sardna2Brain.Run(new string[0]);
            case "all":
                return All();
            default:
                Console.Error.WriteLine(
                    $"неизвестный режим `{mode}`; есть parity|bio|skinner|arena|bench-haap|all");
                return 2;
        }
    }

    static int All()
    {
        var stages = new (string Name, Func<int> Run)[]
        {
            ("parity", () => Parity("dioma") | Parity("dioma_c")),
            ("bio", Bio),
            ("skinner", Skinner),
            ("arena", Arena),
        };
        var failed = new List<string>();
        foreach (var (name, run) in stages)
        {
            Console.WriteLine($"── {name} ──");
            if (run() != 0)
                failed.Add(name);
            Console.WriteLine();
        }
        Console.WriteLine(failed.Count == 0
            ? $"[all] {stages.Length}/{stages.Length} PASS"
            : $"[all] FAIL: {string.Join(", ", failed)}");
        return failed.Count == 0 ? 0 : 1;
    }

    /// Замер скорости моделей мембраны на ОДНОЙ топологии: эталонный
    /// stage-C (760 нейронов, все синапсы) прогоняется тремя моделями
    /// зоопарка. HAAP получает профиль от архетипов (как настоящий
    /// HAAP-мозг), сравнение честное: тик = тик, топология одна.
    static int BenchHaap()
    {
        string json = File.ReadAllText(Path.Combine(DataDir, "dioma_c_connectome.json"));
        int ticks = 200;
        var results = new List<(string Model, double MsPerTick, long Spikes)>();

        // AlphaLif — как есть в файле.
        using (var brain = DiomaBrain.FromJson(json))
        {
            // Тонический ток: без него alpha-lif/lif молчат, и их время НЕ
            // включает рассылку по синапсам (HAAP-пейсмекеры стреляют сами).
            // Ток выше порога: даёт ВСЕМ моделям постоянную активность в
            // рабочем режиме — замер включает мембрану И рассылку. 20 тиков
            // прогрева — JIT generic-кернела не попадает в замер.
            var ext = new float[brain.N];
            Array.Fill(ext, 0.6f);
            for (int t = 0; t < 20; t++) brain.Tick(ext);
            var sw = Stopwatch.StartNew();
            long spikes = 0;
            for (int t = 0; t < ticks; t++) spikes += brain.Tick(ext);
            sw.Stop();
            results.Add(("alpha-lif", sw.Elapsed.TotalMilliseconds / ticks, spikes));
        }

        // Lif: тот же коннектом, поле model переписано.
        string lifJson = json.Replace("\"edges\"", "\"model\":\"lif\",\"edges\"");
        using (var brain = DiomaBrain.FromJson(lifJson))
        {
            // Тонический ток: без него alpha-lif/lif молчат, и их время НЕ
            // включает рассылку по синапсам (HAAP-пейсмекеры стреляют сами).
            // Ток выше порога: даёт ВСЕМ моделям постоянную активность в
            // рабочем режиме — замер включает мембрану И рассылку. 20 тиков
            // прогрева — JIT generic-кернела не попадает в замер.
            var ext = new float[brain.N];
            Array.Fill(ext, 0.6f);
            for (int t = 0; t < 20; t++) brain.Tick(ext);
            var sw = Stopwatch.StartNew();
            long spikes = 0;
            for (int t = 0; t < ticks; t++) spikes += brain.Tick(ext);
            sw.Stop();
            results.Add(("lif", sw.Elapsed.TotalMilliseconds / ticks, spikes));
        }

        // Haap: тот же коннектом + haapProfile по архетипам регионов.
        // Регион арены: индекс из порядкового номера региона в файле.
        var probe = DiomaBrain.FromJson(json);
        int[] regionOf = new int[probe.N];
        for (int i = 0; i < probe.N; i++) regionOf[i] = probe.RegionOf(i);
        probe.Dispose();
        var genes = Dioma.Spikes.PhysioGenes.Wildtype();
        var profile = Dioma.Spikes.HaapPhysiology.BrainProfile(
            regionOf, Dioma.Spikes.HaapPhysiology.CognitiveArchetype, genes);

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        // Вставляем model и haapProfile ПЕРЕД "regions" оригинального
        // файла: остальное (lif/stdp/plasticity/edges/...) не трогаем.
        int at = json.IndexOf("\"regions\"");
        sb.Append(json.Substring(0, at));
        sb.Append("\"model\":\"haap\",\"haapProfile\":[");
        for (int i = 0; i < probe.N; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('[');
            for (int c = 0; c < Dioma.Spikes.HaapCols.N; c++)
            {
                if (c > 0) sb.Append(',');
                float v = profile[i * Dioma.Spikes.HaapCols.N + c];
                sb.Append(v == MathF.Floor(v) && MathF.Abs(v) < 1e7f
                    ? ((long)v).ToString(inv) : v.ToString("R", inv));
            }
            sb.Append(']');
        }
        sb.Append("],");
        sb.Append(json.Substring(at));
        string haapJson = sb.ToString();
        using (var brain = DiomaBrain.FromJson(haapJson))
        {
            // Тонический ток: без него alpha-lif/lif молчат, и их время НЕ
            // включает рассылку по синапсам (HAAP-пейсмекеры стреляют сами).
            // Ток выше порога: даёт ВСЕМ моделям постоянную активность в
            // рабочем режиме — замер включает мембрану И рассылку. 20 тиков
            // прогрева — JIT generic-кернела не попадает в замер.
            var ext = new float[brain.N];
            Array.Fill(ext, 0.6f);
            for (int t = 0; t < 20; t++) brain.Tick(ext);
            var sw = Stopwatch.StartNew();
            long spikes = 0;
            for (int t = 0; t < ticks; t++) spikes += brain.Tick(ext);
            sw.Stop();
            results.Add(("haap", sw.Elapsed.TotalMilliseconds / ticks, spikes));
        }

        Console.WriteLine($"[bench-haap] stage-C 760 нейронов, {ticks} тиков, " +
            "тонус 0.6, managed-кернел:");
        foreach (var (model, ms, spikes) in results)
            Console.WriteLine($"  {model,-10} {ms,8:F2} мс/тик   спайков/тик {spikes / ticks}");
        double alpha = results[0].MsPerTick;
        double haap = results[2].MsPerTick;
        Console.WriteLine($"  haap/alpha-lif = {haap / alpha:F2}×  " +
            $"({results[1].MsPerTick / alpha:F2}× у плоского lif)");
        Console.WriteLine(results.Count == 3 ? "PASS" : "FAIL");
        return results.Count == 3 ? 0 : 1;
    }

    static int Arena()
    {
        int rc = ArenaSmoke.Run(DataDir, "dioma", 4000, 4);
        Console.WriteLine();
        return rc | ArenaSmoke.Run(DataDir, "dioma_c", 2000, 4);
    }

    /// Протокол обучения: награждаемый путь должен расти, прочие — стоять.
    static int Skinner()
    {
        var box = SkinnerBox.Load(DataDir, out var golden);
        int firedMismatch = 0, rewardMismatch = 0, first = -1;
        double maxErr = 0;
        for (int t = 0; t < golden.Count; t++)
        {
            var mine = box.Step(golden[t].Target);
            var g = golden[t];
            if (mine.Fired != g.Fired) firedMismatch++;
            if (mine.Rewarded != g.Rewarded) rewardMismatch++;
            for (int i = 0; i < g.W.Length; i++)
                maxErr = Math.Max(maxErr, Math.Abs(mine.W[i] - g.W[i]) / Math.Max(1.0, Math.Abs(g.W[i])));
            if (first < 0 && (mine.Fired != g.Fired || mine.Rewarded != g.Rewarded)) first = t;
        }
        var w = box.Brain.CsrValues();
        double sel = box.Selectivity();
        Console.WriteLine($"[skinner] {golden.Count} проб: награждаемый {box.W0:F3} → {w[0]:F3}, " +
            $"прочие [{w[1]:F3} {w[2]:F3} {w[3]:F3}], избирательность ×{sel:F2}");
        Console.WriteLine($"[skinner] паритет: спайков расходится {firedMismatch}, наград {rewardMismatch}, " +
            $"max rel вес={maxErr:E2}, first={first}");
        bool learned = w[0] > box.W0 * 2f && sel > 3.0;
        bool parity = firedMismatch == 0 && rewardMismatch == 0 && maxErr <= 1e-3;
        Console.WriteLine(learned && parity ? "PASS" : $"FAIL{(learned ? "" : " (не учится)")}{(parity ? "" : " (расходится)")}");
        return learned && parity ? 0 : 1;
    }

    static int Parity(string prefix)
    {
        var runner = GoldenRunner.Load(DataDir, prefix);
        int exact = 0, total = runner.Rows.Count, maxDml = 0, maxDmr = 0, first = -1;
        double maxRel = 0;
        var sw = Stopwatch.StartNew();
        foreach (var row in runner.Rows)
        {
            runner.Step();
            int dml = Math.Abs(row.Ml - row.MlRef), dmr = Math.Abs(row.Mr - row.MrRef);
            if (dml == 0 && dmr == 0) exact++;
            maxDml = Math.Max(maxDml, dml);
            maxDmr = Math.Max(maxDmr, dmr);
            maxRel = Math.Max(maxRel, Math.Abs(row.Wsum - row.WsumRef) / Math.Max(1.0, Math.Abs(row.WsumRef)));
            if (first < 0 && (dml > 0 || dmr > 0)) first = runner.Cursor - 1;
        }
        sw.Stop();
        Console.WriteLine($"[{prefix}] {runner.Brain.SynapseCount} синапсов: паритет {exact}/{total}, " +
            $"max|dML|={maxDml} max|dMR|={maxDmr}, rel wsum={maxRel:E2}, first={first}, " +
            $"{sw.Elapsed.TotalMilliseconds / total:F2} мс/тик");
        Console.WriteLine(exact == total ? "PASS" : "FAIL");
        return exact == total ? 0 : 1;
    }

    static int Bio() => BioSmoke.Run(DataDir);
}
