// Врождённый рефлекс: «стимул → ожидаемое действие + дофамин», прожигаемый
// в мозг при рождении. Порт `dioma_engine::brain::genes::instincts`.
//
// Новорождённый с дефолтным геномом инстинктов НЕ имеет — он учится сам.
// Инстинкты появляются у потомков через эффект Болдуина: путь, который
// родитель нажил за жизнь (LearnedGain выше порога), сворачивается в
// врождённую прошивку ребёнка. Так навык одного поколения становится
// рефлексом следующего — без изменения топологии.

using System;
using System.Collections.Generic;
using Dioma.Spikes;

namespace Dioma.Body
{
    public sealed class Instinct
    {
        /// Верхняя граница дофамина: за много поколений мутаций сигнал не
        /// должен расходиться бесконечно.
        public const float DopamineMax = 8.0f;
        public const float DopamineMin = 0.2f;

        /// Клетки для стимуляции: (нейрон, ток).
        public readonly List<(int Neuron, float Drive)> Inputs;
        /// Фазический дофамин за реакцию (> 0).
        public float Dopamine;

        public Instinct(List<(int, float)> inputs, float dopamine)
        {
            Inputs = inputs;
            Dopamine = dopamine;
        }

        public Instinct Clone() =>
            new Instinct(new List<(int, float)>(Inputs), Dopamine);

        /// Мутация: дрейфуют токи и дофамин; индексы нейронов не трогаем —
        /// топология врождённая.
        public Instinct Mutate(XorShift rng, float sigma)
        {
            var inputs = new List<(int, float)>(Inputs.Count);
            foreach (var (neuron, drive) in Inputs)
                inputs.Add((neuron, MathF.Max(drive + (float)rng.Gauss() * sigma * 20f, 0.5f)));
            float dopamine = Math.Clamp(
                Dopamine + (float)rng.Gauss() * sigma * 4f, DopamineMin, DopamineMax);
            return new Instinct(inputs, dopamine);
        }

        /// Прожечь рефлекс в мозг: стимулировать вход, наградить ТОЛЬКО свою
        /// дугу, закрепить (новая норма гомеостаза + снимок памяти).
        ///
        /// `wCap` обычно выше рабочего `w_max`: у выращенного коннектома хвосты
        /// весов — десятки единиц, и обычный дофамин срезал бы их при первом
        /// награждении, ослабив рефлекс вместо усиления.
        public void BurnInto(DiomaBrain brain, float tonic, float wCap)
        {
            var ext = new float[brain.N];
            var stimulated = new bool[brain.N];
            for (int i = 0; i < brain.N; i++)
                ext[i] = tonic;
            foreach (var (neuron, drive) in Inputs)
            {
                if (neuron < 0 || neuron >= brain.N)
                    continue;
                ext[neuron] += drive;
                stimulated[neuron] = true;
            }

            brain.Tick(ext);
            // Только собственная дуга: ко-активация сенсора и мотора возбуждает
            // и тормозные пути через интернейроны — без фильтра награда усилила
            // бы их, и рефлекс глушил бы собственный драйв.
            brain.RestrictEligibility(stimulated);
            brain.RewardNowCapped(Dopamine, wCap);
            brain.ClearEligibility();
            brain.SleepConsolidate(0.6f);
            brain.MemorySnapshot();
        }

        public void BurnInto(DiomaBrain brain, float tonic) =>
            BurnInto(brain, tonic, brain.WMax);
    }
}
