// Порт `body::cell_chemistry.rs` (Rust): SubstanceMap (SubstanceId → Chem),
// StepIntoBlood (клетки → ткань → кровь) и StepExchange (ткань↔кровь обмен).
//
// Ткань — well-mixed отсек между клеточной секрецией и системной кровью.
// Секреция сначала оседает в ткани (концентрация += value / volume), затем
// градиентный перенос в кровь с сохранением полного количества.

using System;

namespace Dioma.Bio
{
    /// Well-mixed локальный химический отсек (ткань).
    public sealed class TissueChemistry
    {
        readonly float[] _concentration;
        readonly float _volume;
        readonly float _permeability;
        readonly float[] _decay;

        public TissueChemistry(int nSubstances, float volume, float permeability)
        {
            if (volume <= 0f) throw new ArgumentException("volume must be > 0", nameof(volume));
            if (permeability < 0f) throw new ArgumentException("permeability must be >= 0", nameof(permeability));
            _concentration = new float[nSubstances];
            _volume = volume;
            _permeability = permeability;
            _decay = new float[nSubstances];
            for (int i = 0; i < nSubstances; i++) _decay[i] = 1f;
        }

        public int NSubstances => _concentration.Length;

        /// Доля, остающаяся за единицу времени для одного вещества.
        public void SetDecayFactor(SubstanceId substance, float factor)
        {
            int i = substance.Value;
            if (i < _decay.Length) _decay[i] = Math.Clamp(factor, 0f, 1f);
        }

        /// Локальный распад за один шаг (dt в единицах тика).
        public void Decay(float dt)
        {
            for (int i = 0; i < _concentration.Length; i++)
                _concentration[i] *= MathF.Pow(_decay[i], dt);
        }

        public float Concentration(SubstanceId substance) =>
            substance.Value < _concentration.Length ? _concentration[substance.Value] : 0f;

        /// Секреция клеток оседает в ткани.
        public void AbsorbEvents(BioEvent[] events)
        {
            foreach (var e in events)
            {
                if (e.Kind != BioEventKind.Secrete || e.Substance is null) continue;
                int i = e.Substance.Value.Value;
                if (i < _concentration.Length)
                    _concentration[i] += MathF.Max(e.Value, 0f) / _volume;
            }
        }

        /// Перенос в кровь за один шаг (в направлении градиента).
        /// Возвращает количество, ушедшее из ткани в кровь за шаг.
        public float TransportToBlood(Blood blood, float bloodVolume, float dt, SubstanceMap map)
        {
            float transported = 0f;
            for (int i = 0; i < _concentration.Length; i++)
            {
                var substance = new SubstanceId((ushort)i);
                int? chemIdx = map.Channel(substance);
                if (chemIdx is null) continue;
                int c = chemIdx.Value;

                float gradient = MathF.Max(_concentration[i] - blood.Conc[c], 0f);
                float available = _concentration[i] * _volume;
                float bloodCapacity = (255f - blood.Conc[c]) * bloodVolume;
                float amount = MathF.Min(
                    _permeability * gradient * dt * _volume,
                    MathF.Min(available, bloodCapacity));

                _concentration[i] -= amount / _volume;
                blood.Conc[c] += amount / bloodVolume;
                transported += amount;
            }
            return transported;
        }

        /// Обмен ткань↔кровь: сначала локальный распад, затем перенос
        /// по градиенту в обе стороны (ткань может пополняться из крови).
        public float StepExchange(Blood blood, float bloodVolume, float dt, SubstanceMap map)
        {
            Decay(dt);
            float exchanged = 0f;

            for (int i = 0; i < _concentration.Length; i++)
            {
                var substance = new SubstanceId((ushort)i);
                int? chemIdx = map.Channel(substance);
                if (chemIdx is null) continue;
                int c = chemIdx.Value;

                float gradient = _concentration[i] - blood.Conc[c];
                float amount = _permeability * MathF.Abs(gradient) * dt * _volume;
                if (gradient >= 0f)
                    amount = MathF.Min(amount, _concentration[i] * _volume);
                else
                    amount = MathF.Min(amount, blood.Conc[c] * bloodVolume);

                if (gradient >= 0f)
                {
                    _concentration[i] -= amount / _volume;
                    blood.Conc[c] += amount / bloodVolume;
                }
                else
                {
                    _concentration[i] += amount / _volume;
                    blood.Conc[c] -= amount / bloodVolume;
                }
                exchanged += amount;
            }
            return exchanged;
        }
    }

    /// Мост между клетками и кровью: события Secrete из BioCellRuntime
    /// оседают в ткани, затем ткань обменивается с кровью.
    public static class TissueCellBridge
    {
        /// Осадить секрецию клеток в ткани.
        public static void AbsorbEvents(TissueChemistry tissue, BioEvent[] events) =>
            tissue.AbsorbEvents(events);

        /// Ткань → кровь (градиентный перенос).
        public static float StepExchange(
            TissueChemistry tissue, Blood blood, float bloodVolume, float dt, SubstanceMap map) =>
            tissue.StepExchange(blood, bloodVolume, dt, map);
    }

    /// Канонический маппинг du SubstanceId (neutre) vers l'indice de canal
    /// Chem. Le couche Bio parle en ids abstraits ; seul le pont vers le
    /// corps connaît les indices chimiques.
    public sealed class SubstanceMap
    {
        int?[] _channels;

        public SubstanceMap(int nSubstances)
        {
            _channels = new int?[nSubstances];
        }

        public void Bind(SubstanceId substance, int chemical)
        {
            int index = substance.Value;
            if (index >= _channels.Length)
            {
                var grown = new int?[index + 1];
                Array.Copy(_channels, grown, _channels.Length);
                _channels = grown;
            }
            _channels[index] = chemical;
        }

        public int? Channel(SubstanceId substance) =>
            substance.Value < _channels.Length ? _channels[substance.Value] : null;
    }
}
