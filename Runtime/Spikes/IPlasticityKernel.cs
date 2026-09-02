// Сменный вычислитель пластичности: mean-field дофаминовая STDP.
//
// Почему это отдельный кернел, а не часть мембранного. Профиль тика на
// stage-C (760 нейронов, 32.5k синапсов, 100 подшагов) после ускорения
// мембраны: мембрана ~2 мс, пластичность ~1.7 мс. Дальше выигрыш только
// здесь, и природа работы совсем другая — не арифметика, а обход памяти:
//
//   NoteSpikes  76 000 итераций за тик, пишет 594 КБ истории следов;
//   Apply       32 583 итерации, каждая делает ДВА случайных чтения из
//               массивов по 297 КБ (в L2 не влезают).
//
// Ключевое наблюдение — асимметрия доступа. Синапсы обходятся в порядке CSR,
// то есть `pre` внутри строки фиксирован, а `post` скачет:
//
//   LTP читает preHist[mPost·N + pre]:  pre фиксирован, mPost скачет
//                                       → шаг 3 КБ, промах на каждый синапс
//   LTD читает postHist[mPre·N + post]: mPre фиксирован (зависит только от
//                                       pre!), post скачет → строка 3 КБ,
//                                       уже помещается в L1
//
// То есть LTD «удобен» в порядке CSR, а LTP — в порядке CSC (по входящим
// рёбрам): при фиксированном `post` его `mPost` тоже фиксирован, и вся
// строка preHist читается локально. Отсюда двухпроходная схема
// (см. `ManagedPlasticityKernel`), которая сохраняет ПОРЯДОК арифметики
// эталона — иначе паритет бит-в-бит сломался бы.

using System;

namespace Dioma.Spikes
{
    /// Неизменная часть: топология в двух видах (CSR и CSC) и константы STDP.
    public sealed class PlasticityLayout
    {
        public int N;
        public int StepsPerTick;
        /// CSR: строки по пресинапсу (порядок обхода эталона).
        public int[] RowPtr;   // N+1
        public int[] PreOf;
        public int[] PostOf;
        /// CSC: для каждого поста — список индексов ВХОДЯЩИХ синапсов.
        /// Строится из CSR один раз; нужен, чтобы читать историю следов пре
        /// локально (см. шапку файла).
        public int[] InPtr;    // N+1
        public int[] InSyn;    // синапсы, сгруппированные по посту
        public float DecayPre, DecayPost, DecayElig;
        public float ALtp, ALtd;

        /// Построить CSC-индекс входящих рёбер из массива постов.
        public static void BuildIncoming(int n, int[] postOf, out int[] inPtr, out int[] inSyn)
        {
            inPtr = new int[n + 1];
            foreach (int post in postOf)
                inPtr[post + 1]++;
            for (int i = 0; i < n; i++)
                inPtr[i + 1] += inPtr[i];
            inSyn = new int[postOf.Length];
            var cursor = (int[])inPtr.Clone();
            for (int idx = 0; idx < postOf.Length; idx++)
                inSyn[cursor[postOf[idx]]++] = idx;
        }
    }

    /// Пластичность одного мозга. Кернел владеет следами, историей следов и
    /// eligibility; наружу отдаёт их только по запросу (награда, сон,
    /// диагностика) — на горячем пути копирований нет.
    public interface IPlasticityKernel : IDisposable
    {
        string Name { get; }

        /// Подшаг: продвинуть зеркала следов и накопить статистику спайков.
        /// `history` — буфер спайков тика, `at` — смещение подшага.
        /// Возвращает число спайков строки, чтобы вызывающий не сканировал
        /// ту же историю отдельным полным проходом.
        int NoteSpikes(byte[] history, int at, int row);

        /// Обработать полную историю одного тика и сразу применить STDP.
        /// Возвращает суммарное число спайков. Это production-путь: кернел
        /// может перенести историю в свою память один раз на весь тик.
        int ProcessTick(byte[] history);

        /// Конец тика: один проход по синапсам — LTP/LTD/затухание.
        void Apply();

        /// Прочитать eligibility (нужно дофамину: Δw = lr·D·e).
        void ReadEligibility(float[] dst);

        /// Обновить D1/D2-тени рядом с владельцем eligibility, не копируя
        /// полный массив через публичную границу кернела.
        void UpdatePathTraces(float[] d1, float[] d2, float d1Decay, float d2Decay);

        /// Обнулить eligibility целиком (ночь: окно обучения закрывается).
        void ClearEligibility();

        /// Оставить eligibility только там, где И пре, И пост в маске
        /// (прожигание врождённого рефлекса).
        void RestrictEligibility(bool[] keepNeurons);

        double EligibilitySum();
    }
}
