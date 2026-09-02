// Проверка моста sardaanaCM → движок dioma: настоящий сгенерированный
// мозг (TAAS2, писатель обновлён под формат Dioma.Data.Taas) читается
// C#-ридером нативно. Путь можно переопределить аргументом — режим
// «taas2» харнесса полезен и для будущих мозгов (flywire, mouse).

using System;
using System.IO;
using System.Linq;
using Dioma.Data;

class TaasCheck
{
    public static int Run(string[] args)
    {
        string path = args.Length > 0 ? args[0]
            : @"D:\Проекты\asm_engines\sardaanaCM\experiments\sardna_ultra\generated\celegans_male_n256_t0_85.taas";
        var r = TaasReader.FromFile(path);
        Console.WriteLine($"[taas2] v{r.Version}, секций {r.Sections.Count}, " +
                          $"байт {new FileInfo(path).Length}");
        int[] rp = r.I32("row_ptr");
        int[] ci = r.I32("col_indices");
        float[] vals = r.F32("values");
        float[] nt = r.F32("nt_types");

        int n = rp.Length - 1;
        int syn = vals.Length;
        int exc = nt.Count(v => v > 0), inh = nt.Count(v => v < 0);
        double wMax = vals.Max(Math.Abs);
        Console.WriteLine($"[celegans] n={n}, syn={syn}, exc={exc}, inh={inh}, " +
                          $"|w|max={wMax:F1}, col_bounds_ok=" +
                          $"{(ci.Length == 0 || (ci.Min() >= 0 && ci.Max() < n))}");
        Console.WriteLine($"[taas2] metadata: n_neurons={r.Metadata["n_neurons"]}");

        // Настоящий celegans-образец: 256 нейронов, CSR согласован,
        // знаки нейротрансмиттеров на месте, метаданные целы.
        bool ok = n == 256 && syn == rp[n] && nt.Length == n
                  && ci.Min() >= 0 && ci.Max() < n
                  && r.Metadata["n_neurons"] == "256";
        Console.WriteLine(ok ? "ИТОГ: PASS" : "ИТОГ: FAIL");
        return ok ? 0 : 1;
    }
}
