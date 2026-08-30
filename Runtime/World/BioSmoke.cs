// Headless-смоук биохимии: 600 тиков эталонного сценария (еда, удары,
// моторная активность) против `dioma_bio_golden.json`. Кровь считает
// одними и теми же числами, что и Rust, так что допуск здесь — не про
// «похоже», а про порядок операций: 1e-4 ловит перепутанную фазу,
// а float-шум остаётся на уровне 1e-8.

using System;
using System.IO;
using Dioma.Bio;
using Dioma.Data;

namespace Dioma.World
{
    public static class BioSmoke
    {
        /// Прогнать эталон биохимии из dataDir. Возвращает 0 (PASS) или 1 (FAIL).
        public static int Run(string dataDir)
        {
            var root = Json.Obj(Json.Parse(
                File.ReadAllText(Path.Combine(dataDir, "dioma_bio_golden.json"))));
            var ticks = Json.Arr(root["ticks"]);

            var blood = Blood.Creature();
            blood.Conc[Chem.Hunger] = 120f;   // новорождённый голоден

            double maxConc = 0, maxLoci = 0, maxParam = 0;
            int badPaid = 0, first = -1, t = 0;

            foreach (var item in ticks)
            {
                var o = Json.Obj(item);
                blood.Loci[Locus.RateMotor] = Json.F(o["rateMotor"]);
                blood.Loci[Locus.GlucoseDeficit] =
                    MathF.Max(Blood.GlucoseAbundant - blood.Conc[Chem.Glucose], 0f);
                if (Json.Bool(o["wall"])) blood.BumpWall();
                if (Json.Bool(o["ate"])) blood.IngestFood(60f);
                blood.Update();

                float pay = Json.F(o["pay"]);
                bool paid = pay > 0f && blood.PayForPlasticity(pay);
                if (Json.Bool(o["sleep"])) blood.Sleep();

                var conc = Json.Arr(o["conc"]);
                for (int i = 0; i < Chem.N; i++)
                {
                    double refv = Json.Num(conc[i]);
                    maxConc = Math.Max(maxConc,
                        Math.Abs(blood.Conc[i] - refv) / Math.Max(1.0, Math.Abs(refv)));
                }
                var loci = Json.Arr(o["loci"]);
                for (int i = 0; i < Locus.N; i++)
                {
                    double refv = Json.Num(loci[i]);
                    maxLoci = Math.Max(maxLoci,
                        Math.Abs(blood.Loci[i] - refv) / Math.Max(1.0, Math.Abs(refv)));
                }
                maxParam = Math.Max(maxParam, Math.Abs(blood.SensorGain() - Json.F(o["sensorGain"])));
                maxParam = Math.Max(maxParam, Math.Abs(blood.MotorTonic() - Json.F(o["motorTonic"])));
                maxParam = Math.Max(maxParam, Math.Abs(blood.Speed() - Json.F(o["speed"])));
                maxParam = Math.Max(maxParam, Math.Abs(blood.TurnGain() - Json.F(o["turnGain"])));
                maxParam = Math.Max(maxParam, Math.Abs(blood.PlasticityBudget() - Json.F(o["budget"])));

                if (paid != Json.Bool(o["paid"])) badPaid++;
                if (first < 0 && (maxConc > Tolerance || paid != Json.Bool(o["paid"]))) first = t;
                t++;
            }

            Console.WriteLine($"[bio] {ticks.Count} тиков: max rel conc={maxConc:E2}, " +
                              $"loci={maxLoci:E2}, abs params={maxParam:E2}, " +
                              $"paid-расхождений {badPaid}, first={first}");
            bool pass = maxConc <= Tolerance && maxLoci <= Tolerance
                        && maxParam <= Tolerance && badPaid == 0;
            Console.WriteLine($"[bio] ИТОГ: {(pass ? "PASS" : "FAIL")}");
            return pass ? 0 : 1;
        }

        public const double Tolerance = 1e-4;
    }
}
