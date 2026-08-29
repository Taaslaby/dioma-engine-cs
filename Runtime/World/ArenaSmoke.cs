// Headless-смоук: существо живёт в тестовой арене. Метрика осмысленности —
// съеденные ягоды и средняя дистанция до ближайшей ягоды по окнам.

using System;
using System.IO;
using Dioma.Bio;
using Dioma.Body;
using Dioma.Spikes;

namespace Dioma.World
{
    public static class ArenaSmoke
    {
        public struct Window
        {
            public int Eaten, WallHits, Sleeps;
            public double AvgBerryDistance, AvgSpikes, AvgDrive, AvgHunger, AvgGlucose;
        }

        /// Прогнать арену на коннектоме `prefix` из dataDir.
        /// Возвращает 0 (PASS: существо ест и не замолкает) / 1 (FAIL).
        public static int Run(string dataDir, string prefix = "dioma", int ticks = 4000,
                              int windows = 4)
        {
            var brain = DiomaBrain.FromJson(
                File.ReadAllText(Path.Combine(dataDir, prefix + "_connectome.json")));
            var creature = new Creature(brain);
            var arena = new TestArena(creature);

            int perWindow = Math.Max(1, ticks / windows);
            var stats = new Window[windows];
            var sw = System.Diagnostics.Stopwatch.StartNew();

            for (int w = 0; w < windows; w++)
            {
                int eatStart = creature.FoodEaten, hitStart = creature.WallHits;
                double dist = 0, spikes = 0, drive = 0, hunger = 0, glucose = 0;
                int sleeps = 0;
                for (int t = 0; t < perWindow; t++)
                {
                    var a = arena.Step();
                    if (a.Asleep) sleeps++;
                    dist += arena.NearestBerryDistance();
                    spikes += creature.LastSpikes;
                    drive += a.Drive;
                    hunger += creature.Blood.Conc[Chem.Hunger];
                    glucose += creature.Blood.Conc[Chem.Glucose];
                }
                stats[w] = new Window
                {
                    Eaten = creature.FoodEaten - eatStart,
                    WallHits = creature.WallHits - hitStart,
                    Sleeps = sleeps,
                    AvgBerryDistance = dist / perWindow,
                    AvgSpikes = spikes / perWindow,
                    AvgDrive = drive / perWindow,
                    AvgHunger = hunger / perWindow,
                    AvgGlucose = glucose / perWindow,
                };
                var s = stats[w];
                Console.WriteLine(
                    $"[arena:{prefix}] окно {w + 1}/{windows} ({perWindow} тиков = " +
                    $"{perWindow * TestArena.TickSeconds:F0}с): съедено {s.Eaten}, " +
                    $"ударов {s.WallHits}, сна {s.Sleeps}, дист {s.AvgBerryDistance:F1}, " +
                    $"спайков/тик {s.AvgSpikes:F0}, drive {s.AvgDrive:F2}, " +
                    $"голод {s.AvgHunger:F0}, глюкоза {s.AvgGlucose:F0}");
            }
            sw.Stop();

            Console.WriteLine(
                $"[arena:{prefix}] ИТОГО: {ticks} тиков ({ticks * TestArena.TickSeconds:F0}с мира) " +
                $"за {sw.Elapsed.TotalSeconds:F1}с, съедено {creature.FoodEaten}, " +
                $"ударов {creature.WallHits}, урон органа {creature.Blood.Organ.Damage:F3}, " +
                $"{sw.Elapsed.TotalMilliseconds / ticks:F2} мс/тик");

            // Критерий: существо ЕСТ, мозг не замолк, тело живо.
            bool eats = creature.FoodEaten > 0;
            bool alive = stats[windows - 1].AvgSpikes > 0
                         && creature.Blood.Organ.Damage < 1.0f;
            Console.WriteLine($"[arena:{prefix}] ИТОГ: {(eats && alive ? "PASS" : "FAIL")}" +
                              (eats ? "" : " (не ест)") + (alive ? "" : " (мозг молчит/тело мертво)"));
            return eats && alive ? 0 : 1;
        }
    }
}
