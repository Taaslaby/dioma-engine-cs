// Headless-смоук пакета: прогон золотого следа без редактора/окна.
// Вызывается CLI-обёрткой:
//   Unity.exe -batchmode -projectPath <proj> \
//     -executeMethod <обёртка> -quit -logFile …
// Ассемблер Runtime свободен от UnityEngine — вывод через Console,
// в batch-режиме он попадает и в -logFile.

using System;
using System.IO;

namespace Dioma.Spikes
{
    public static class SmokeRunner
    {
        /// <summary>
        /// Прогнать золотой след из dataDir (dioma_connectome.json +
        /// dioma_golden.json). Возвращает 0 (PASS) или 1 (FAIL).
        /// </summary>
        public static int Run(string dataDir)
        {
            var runner = GoldenRunner.Load(dataDir);
            int exact = 0, total = runner.Rows.Count;
            int maxDml = 0, maxDmr = 0, firstMismatch = -1;
            double maxWsumRel = 0;

            foreach (var row in runner.Rows)
            {
                runner.Step();
                int dml = Math.Abs(row.Ml - row.MlRef);
                int dmr = Math.Abs(row.Mr - row.MrRef);
                if (dml == 0 && dmr == 0) exact++;
                maxDml = Math.Max(maxDml, dml);
                maxDmr = Math.Max(maxDmr, dmr);
                double rel = Math.Abs(row.Wsum - row.WsumRef) / Math.Max(1.0, Math.Abs(row.WsumRef));
                maxWsumRel = Math.Max(maxWsumRel, rel);
                if (firstMismatch < 0 && (dml > 0 || dmr > 0))
                    firstMismatch = runner.Cursor - 1;
                if ((runner.Cursor - 1) % 40 == 0)
                    Console.WriteLine(
                        $"tick {runner.Cursor - 1,3}: ml={row.Ml} (ref {row.MlRef}) " +
                        $"mr={row.Mr} (ref {row.MrRef}) spikes={row.Spikes} " +
                        $"wsum={row.Wsum:F2} (ref {row.WsumRef:F2})");
            }

            Console.WriteLine($"[smoke] паритет моторных сумм: {exact}/{total} тиков точных, " +
                              $"max|dML|={maxDml}, max|dMR|={maxDmr}, " +
                              $"max rel wsum={maxWsumRel:E2}, first mismatch={firstMismatch}");
            bool pass = exact * 10 >= total * 9 && maxDml <= 8 && maxDmr <= 8 && maxWsumRel <= 2e-3;
            Console.WriteLine($"[smoke] ИТОГ: {(pass ? "PASS" : "FAIL")}");
            return pass ? 0 : 1;
        }
    }
}
