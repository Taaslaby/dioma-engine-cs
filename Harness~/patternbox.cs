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
    const float Stim = 1.0f;    // ↓ с 4: при 4 мотор сатурирован базой
                                // (763/734 спайков) и вклад HC-состояния
                                // тонет; меньше ток — отклик пропорциональнее
                                // входу, разница порядков различима
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

        // ── Гиппокамп: WM-след порядка стимулов ──
        // Запись: каждый стимул — 2-мерный вектор (U/D) в слот.
        // Чтение: буфер через GNW сжимается и добавляется в I-нейроны:
        // моторный тик видит «что было» рядом с «что есть».
        var wm = new Dioma.Body.WorkingMemory(slots: 4, dim: 2, seed: 9001)
        { RecurrentGain = 0.1f, Decay = 0.95f };
        int iR = _brain!.FindRegion("I");
        var gnw = new Dioma.Body.GlobalWorkspace(
            nNeurons: _brain.Regions[iR].End - _brain.Regions[iR].Start,
            workspaceSize: 8, seed: 9002);

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
        (int motorSpikes, int lastTick, int[] seq) Play(int[] seq)
        {
            Array.Clear(_ext, 0, _ext.Length);
            int fired = 0;
            int last = 0;
            foreach (var s in seq)
            {
                var region = s == 0 ? _sl : _sr;
                for (int i = _brain!.Regions[region].Start;
                     i < _brain.Regions[region].End; i++)
                    _ext[i] = Stim;

                // HC-путь: slot-major развёртка с ВЕСОМ ВОЗРАСТА —
                // порядок живёт в (слот, возраст): UUDD и UDUD пишут
                // разные слоты в одинаковые возрасты. Развёртка по
                // кругу (прошлая) вымывала порядок в сумму.
                wm.Store(s == 0 ? new float[] { 1f, 0f } : new float[] { 0f, 1f });
                var hc = wm.RecallAll();
                int iStart = _brain.Regions[iR].Start;
                int iCount = _brain.Regions[iR].End - iStart;
                var hcFeed = new float[iCount];
                for (int i = 0; i < iCount; i++)
                {
                    int slot = i % wm.Slots;
                    int comp = (i / wm.Slots) % wm.Dim;
                    // возраст слота: чем старше, тем тише вклад.
                    float ageW = 1f / (1f + (float)wm.SlotAge(slot));
                    hcFeed[i] = hc[slot * wm.Dim + comp] * ageW * 4f;
                }
                gnw.Forward(hcFeed);
                // Оба адреса: состояние памяти видно И кольцу I (усиление),
                // И мотору напрямую (минуя эхо). Веса рассеяны: вклад HC в
                // TAIL меньше вклада стимула, чтобы не подменять стимул.
                int tStart = _brain.Regions[_motor].Start;
                int tCount = _brain.Regions[_motor].End - tStart;
                for (int i = 0; i < tCount && i < hcFeed.Length; i++)
                    _ext[tStart + i] += hcFeed[i] * 0.3f;
                for (int i = 0; i < iCount; i++)
                    _ext[iStart + i] += hcFeed[i] * 0.4f;

                last = MotorSpikes(_ext, 1);
                fired += last;
                Array.Clear(_ext, 0, _ext.Length);
                _ = region;
            }
            // Межстимульная динамика памяти: след затухает, но живёт.
            wm.Tick();
            return (fired, last, seq);
        }

        bool IsPattern(int[] seq) =>
            seq[0] == 0 && seq[1] == 0 && seq[2] == 1 && seq[3] == 1;

        // ── Обучение: чередуем паттерн (25%) и случайные порядки ──
        int rewards = 0, punishments = 0;
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
            // Surprise-кредитование: прогноз HC ≈ отклик прошлых тиков
            // (что мотор делал бы на такой истории). Факт = отклик
            // последнего тика. Награда ТОЛЬКО за превышение прогноза —
            // иначе D-хвост всегда выигрывает кредит просто потому что
            // он сильнее анатомически (замер: базы D=209 против U=32).
            var (fired, last, _) = Play(seq);
            int prev = fired - last;
            double predicted = seq.Length > 1 ? prev / (double)(seq.Length - 1) : 0;
            // Честное окно: превышение на ОДИН тик-эквивалент (не на 4 —
            // при 15-17 наградах STDP не успевает закрепить путь; рыбка
            // училась на ~95 кредитах).
            double surprise = last - predicted;
            _brain!.EnablePathTraces();
            // ПОРЯДОК-ЧУВСТВИТЕЛЬНЫЙ гейт: наказание срабатывает только
            // когда история HC не паттерн — сырому «громкому хвосту» DD
            // (который у UUDD тоже есть) прощение не нужно: наказание
            // обязано отличать UUDD от UDDD, а не тихий хвост от громкого.
            // Носитель порядка — WM (слоots уже записали последовательность).
            bool patternHistory = IsPattern(seq);
            if (patternHistory && surprise > 0 && last > 0)
            {
                // Кредит ПРОПОРЦИОНАЛЕН превышению прогноза: сильный
                // сюрприз — сильная награда (плоская ставка не
                // дифференцировала паттерны внутри 95 кредитов).
                float mag = (float)Math.Min(surprise / Math.Max(predicted, 1.0), 3.0);
                _brain.RewardPathed(RewardDopamine * mag);
                rewards++;
            }
            else if (!patternHistory && surprise > 0)
            {
                // На чужом порядке — ЗЛО (D2, строит НЕ-действие).
                _brain.RewardPathed(-RewardDopamine * 0.5f);
                punishments++;
            }
            // ТИШИНА не наказывается: surprise<=0 на паттерне — просто
            // слабый отклик, не ошибка порядка.
            // зазор: eligibility затухает, попытки независимы
            var quiet = new float[_brain!.N];
            for (int g = 0; g < Gap; g++)
            {
                _brain.Tick(quiet);
                wm.Tick();
            }
            if (t % 100 == 0)
                history.Add($"попытка {t}: наград {rewards}, наказаний {punishments}");
        }
        Console.WriteLine($"[pattern] обучение: {Trials} попыток, " +
                          $"наград {rewards}, наказаний {punishments} (сюрприз-сигнал симметричен)");

        // ── Проверка ИЗБИРАТЕЛЬНОСТИ: база до обучения vs после ──
        // Отклик меряем на: паттерн UUDD и все его непаттерновые
        // перестановки (UDUD, DUUD, ...). Выучил последовательность =
        // рост отклика на UUDD БОЛЬШЕ, чем на чужих порядках (у них
        // те же символы — рост за счёт отдельных стимулов одинаков).
        int Probe(int[] seq)
        {
            var (fired, _, _) = Play(seq);
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
        // HC-различимость: hcFeed на последнем стимуле UUDD vs UDUD.
        float[] FeedOf(int[] seq)
        {
            wm.Clear();
            float[] feed = new float[0];
            foreach (var s in seq)
            {
                wm.Store(s == 0 ? new float[] { 1f, 0f } : new float[] { 0f, 1f });
                var hc = wm.RecallAll();
                int iStart2 = _brain!.Regions[iR].Start;
                int iCount2 = _brain.Regions[iR].End - iStart2;
                feed = new float[iCount2];
                for (int i = 0; i < iCount2; i++)
                {
                    int slot = i % wm.Slots;
                    int comp = (i / wm.Slots) % wm.Dim;
                    float ageW = 1f / (1f + (float)wm.SlotAge(slot));
                    feed[i] = hc[slot * wm.Dim + comp] * ageW * 4f;
                }
            }
            return feed;
        }
        // Проводка I→TAIL: есть ли у HC-входа путь к мотору вообще?
        int iR3 = _brain!.FindRegion("I");
        var brainVals = _brain.CsrValues();
        int iS = _brain.Regions[iR3].Start, iE = _brain.Regions[iR3].End;
        int tS = _brain.Regions[_motor].Start, tE = _brain.Regions[_motor].End;
        int[,] pre2post = new int[1,1];
        _ = pre2post;
        // Считаем через CSR: для каждого I-нейрона входящие в TAIL.
        int iToTail = 0;
        var rp2 = _brain.GetType().GetField("_rowPtr",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        // проще: RegionPairWeight
        double wIT = _brain.RegionPairWeight(iR3, _motor);
        double wST = _brain.RegionPairWeight(_sl, _motor);
        double wIT2 = _brain.RegionPairWeight(iR3, iR3);
        Console.WriteLine($"[pattern] проводка: W(I→TAIL)={wIT:F1}, W(SL→TAIL)={wST:F1}, " +
                          $"W(I→I)={wIT2:F1} — " +
                          $"{(wIT > 0.01 ? "путь есть" : "ПУТИ НЕТ: HC-вход не доезжает")}");
        // Нормировка на U/D-асимметрию: прогноз отклика по одиночным
        // базам. selectivity = отклик(порядок) / прогноз(порядок) —
        // чистый вклад ПОСЛЕДОВАТЕЛЬНОСТИ без баланса U/D.
        // Базы: одиночный стимул в позиции k даёт вклад single[s].
        var single = new[] { ProbeAvg(new[] { 0 }, 10), ProbeAvg(new[] { 1 }, 10) };
        Console.WriteLine($"[pattern] базы: U0={single[0]} D0={single[1]}");
        Func<int[], double> Predicted = seq => seq.Sum(s => (double)single[s]) / 2.0;
        Console.WriteLine("[pattern] нормированная избирательность:");
        double uu = target / Predicted(orders[0].seq);
        double othersN = orders.Skip(1).Average(o => ProbeAvg(o.seq, reps) / Predicted(o.seq));
        Console.WriteLine($"[pattern] UUDD={uu:F3} против чужих={othersN:F3} " +
                          $"(нормировано на прогноз одиночных)");
        var feedA = FeedOf(new[] { 0, 0, 1, 1 });
        var feedB = FeedOf(new[] { 0, 1, 0, 1 });
        double diff = 0, na = 0;
        for (int i = 0; i < feedA.Length; i++)
        {
            diff += MathF.Abs(feedA[i] - feedB[i]);
            na += feedA[i] * feedA[i];
        }
        Console.WriteLine($"[pattern] HC-вход различим: |UUDD-UDUD|={diff:F3} " +
                          $"при норме {MathF.Sqrt((float)na):F3} " +
                          $"({(diff / Math.Max(na, 1e-9) > 0.15 ? "РАЗЛИЧИМ" : "порядок вымывается")})");
        // Базовая различимость: один стимул против другого (без порядка).
        int uOnly = ProbeAvg(new[] { 0 }, 20);
        int dOnly = ProbeAvg(new[] { 1 }, 20);
        Console.WriteLine($"[pattern] одиночные: U={uOnly} D={dOnly} " +
                          $"(различимость {Math.Max(uOnly, dOnly) / (double)Math.Max(1, Math.Min(uOnly, dOnly)):F2}×)");
        // Вердикт — по НОРМИРОВАННОЙ метрике (снимает U/D-асимметрию
        // топологии: D-путь к мотору в 6.5× сильнее — сырые спайки
        // паттерна никогда его не перебьют, рост живёт над прогнозом).
        double normRatio = othersN > 0 ? uu / othersN : double.PositiveInfinity;
        double rawRatio = others > 0 ? target / others : double.PositiveInfinity;
        Console.WriteLine($"[pattern] сырая ×{rawRatio:F2} (диагностика), " +
                          $"нормированная ×{normRatio:F2} (вердикт)");
        Console.WriteLine(normRatio > 1.15
            ? "[pattern] ПОСЛЕДОВАТЕЛЬНОСТЬ ВЫУЧЕНА (над прогнозом одиночных)"
            : "[pattern] паттерн от компонентов не отличим");
        foreach (var g in history) Console.WriteLine($"  {g}");
        return 0;
    }
}
