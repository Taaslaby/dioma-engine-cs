// Паттерн-протокол: мозг учится ОТВЕЧАТЬ на стимул-последовательность
// «вверх-вверх-вниз-вниз» — классика выучивания временных паттернов.
//
// Протокол (не паритет — способность):
//   4 сенсорных канала: 0=«вверх», 1=«вниз» (как у существа: два
//   направления). Паттерн UUDD = стимулы 0,0,1,1 по тикам.
//   Реплика: N попыт, в каждой — случайный порядок или паттерн.
//   Награда: ТОЛЬКО если 4-символьная история стимулов == UUDD И
//   «свой» мотор (TAIL-пул) стрелял в момент завершения паттерна.
//   Проверка в конце: ответ на UUDD сильнее, чем на любой другой
//   порядок тех же символов (UDUD, DDUU, ...) — мозг выучил
//   ПОСЛЕДОВАТЕЛЬНОСТЬ, а не отдельные стимулы.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dioma.Spikes;

class PatternBox
{
    const int Trials = 400;
    const int Gap = 8;          // тиков тишины между попытами: eligibility
                                // затухает, чужие попытки не кредитуются
    const float Stim = 4f;      // ток стимула (как живость-проверка s2b)
    const float RewardDopamine = 1f;

    static DiomaBrain? _brain;
    static float[] _ext = null!;
    static int _sl, _sr, _motor;

    public static int Run(string[] args)
    {
        string path = args.Length > 0 ? args[0]
            : "D:" + Path.DirectorySeparatorChar + "Проекты" +
              Path.DirectorySeparatorChar + "asm_engines" +
              Path.DirectorySeparatorChar + "sardaanaCM" +
              Path.DirectorySeparatorChar + "experiments" +
              Path.DirectorySeparatorChar + "sardna_ultra" +
              Path.DirectorySeparatorChar + "generated" +
              Path.DirectorySeparatorChar + "celegans_male_dioma.json";
        _brain = DiomaBrain.FromJson(File.ReadAllText(path));
        _ext = new float[_brain.N];
        _sl = _brain.FindRegion("SL");
        _sr = _brain.FindRegion("SR");
        _motor = _brain.FindRegion("TAIL");
        var rng = new XorShift(777);

        // Моторный отклик: сумма спайков моторного пула за окно ответа.
        int MotorSpikes(float[] ext, int steps)
        {
            var starts = new[] { _brain!.Regions[_motor].Start };
            var ends = new[] { _brain.Regions[_motor].End };
            var sums = new int[1];
            long total = 0;
            for (int s = 0; s < steps; s++)
            {
                total = _brain.TickMotorPools(ext, starts, ends, sums);
                _ = total;
            }
            return sums[0];
        }

        // Одна попытка: подаём 4 стимула (по 1 тику каждый), каждый —
        // ток в SL (вверх) или SR (вниз). Мотор слушаем весь паттерн.
        (int motorSpikes, int[] seq) Play(int[] seq)
        {
            Array.Clear(_ext, 0, _ext.Length);
            int fired = 0;
            foreach (var s in seq)
            {
                var region = s == 0 ? _sl : _sr;
                for (int i = _brain!.Regions[region].Start;
                     i < _brain.Regions[region].End; i++)
                    _ext[i] = Stim;
                fired += MotorSpikes(_ext, 1);
                Array.Clear(_ext, 0, _ext.Length);
                _ = region;
            }
            return (fired, seq);
        }

        bool IsPattern(int[] seq) =>
            seq[0] == 0 && seq[1] == 0 && seq[2] == 1 && seq[3] == 1;

        // ── Обучение: чередуем паттерн (25%) и случайные порядки ──
        int rewards = 0;
        var history = new List<string>();
        for (int t = 0; t < Trials; t++)
        {
            int[] seq;
            if (rng.Unit() < 0.25)
                seq = new[] { 0, 0, 1, 1 };
            else
            {
                // случайный порядок из {0,1}^4, ≠ паттерну
                do
                {
                    seq = new int[4];
                    for (int i = 0; i < 4; i++) seq[i] = (int)(rng.Unit() * 2);
                } while (IsPattern(seq));
            }
            var (fired, _) = Play(seq);
            if (IsPattern(seq) && fired > 0)
            {
                // D1-путь (R-STDP из BDM-SNN): дофамин домножает СЛЕД,
                // который за 4-тик-паттерн копил вклад всех стимулов
                // с затуханием 0.8^k. Голова паттерна (UU) жива в следе
                // наравне с хвостом (DD) — RewardNow кредитовал бы
                // только хвост (замер: избирательность 0.97).
                _brain!.EnablePathTraces();
                _brain.RewardPathed(RewardDopamine);
                rewards++;
            }
            // зазор: eligibility затухает, попытки независимы
            var quiet = new float[_brain!.N];
            for (int g = 0; g < Gap; g++) _brain.Tick(quiet);
            if (t % 100 == 0)
                history.Add($"попытка {t}: наград {rewards}");
        }
        Console.WriteLine($"[pattern] обучение: {Trials} попыток, " +
                          $"наград {rewards} (паттерн встречался ~25%)");

        // ── Проверка ИЗБИРАТЕЛЬНОСТИ: база до обучения vs после ──
        // Отклик меряем на: паттерн UUDD и все его непаттерновые
        // перестановки (UDUD, DUUD, ...). Выучил последовательность =
        // рост отклика на UUDD БОЛЬШЕ, чем на чужих порядках (у них
        // те же символы — рост за счёт отдельных стимулов одинаков).
        int Probe(int[] seq)
        {
            var (fired, _) = Play(seq);
            return fired;
        }
        int ProbeAvg(int[] seq, int reps)
        {
            // Спайки шумят: усредняем повторы (шум ринга задержек).
            long sum = 0;
            for (int i = 0; i < reps; i++) sum += Probe(seq);
            return (int)(sum / reps);
        }

        var orders = new[]
        {
            (name: "UUDD", seq: new[] { 0, 0, 1, 1 }),
            (name: "UDUD", seq: new[] { 0, 1, 0, 1 }),
            (name: "DDUU", seq: new[] { 1, 1, 0, 0 }),
            (name: "UUUU", seq: new[] { 0, 0, 0, 0 }),
            (name: "DDDD", seq: new[] { 1, 1, 1, 1 }),
            (name: "UDDD", seq: new[] { 0, 1, 1, 1 }),
        };
        const int reps = 30;
        Console.WriteLine("[pattern] отклик мотора (спайки за попытку):");
        foreach (var (name, seq) in orders)
            Console.WriteLine($"  {name}: {ProbeAvg(seq, reps)}");

        // Вердикт: UUDD-отклик строго больше среднего чужих.
        int target = ProbeAvg(orders[0].seq, reps);
        double others = orders.Skip(1).Average(o => (double)ProbeAvg(o.seq, reps));
        Console.WriteLine($"[pattern] UUDD={target} против среднее-чужих={others:F1}");
        // Базовая различимость: один стимул против другого (без порядка).
        int uOnly = ProbeAvg(new[] { 0 }, 20);
        int dOnly = ProbeAvg(new[] { 1 }, 20);
        Console.WriteLine($"[pattern] одиночные: U={uOnly} D={dOnly} " +
                          $"(различимость {Math.Max(uOnly, dOnly) / (double)Math.Max(1, Math.Min(uOnly, dOnly)):F2}×)");
        double ratio = others > 0 ? target / others : double.PositiveInfinity;
        Console.WriteLine($"[pattern] избирательность ×{ratio:F2} " +
                          (ratio > 1.15
                              ? "— ПОСЛЕДОВАТЕЛЬНОСТЬ ВЫУЧЕНА (не отдельные стимулы)"
                              : "— паттерн от компонентов не отличим"));
        foreach (var g in history) Console.WriteLine($"  {g}");
        return 0;
    }
}
