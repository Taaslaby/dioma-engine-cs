// Порт `biology::biocell.rs` + `body::cell_chemistry.rs` (Rust) —
// программируемые секреторные клетки: сигнал → рецепторная активация →
// кальций → выброс из пула везикул → секреция → восстановление пула.
//
// Порядок операций шага — один-в-один с Rust-эталоном:
//   activation = max(signal − threshold, 0)
//   calcium   += (activation − calcium) · calcium_response
//   release   = min(calcium · release_gain · vesicles, vesicles)
//   vesicles  = clamp(vesicles − release + (1 − vesicles) · recovery_rate, 0, 1)
//
// Границы модуля: BioCellRuntime не знает ни о крови, ни о мире. Секреция
// возвращается как события Secrete; хост решает (через TissueCellBridge /
// SubstanceMap) куда они идут. SubstanceId нейтрален: маппинг на Chem —
// дело слоя тела (Tissue.cs).

using System;

namespace Dioma.Bio
{
    /// Хост-определённый идентификатор вещества. BioCellRuntime не назначает
    /// химический смысл числу; маппинг на каналы Chem — дело слоя тела.
    public readonly struct SubstanceId : IEquatable<SubstanceId>
    {
        public readonly ushort Value;
        public SubstanceId(ushort value) => Value = value;
        public bool Equals(SubstanceId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is SubstanceId s && Equals(s);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => $"Substance({Value})";
        public static bool operator ==(SubstanceId a, SubstanceId b) => a.Equals(b);
        public static bool operator !=(SubstanceId a, SubstanceId b) => !a.Equals(b);
    }

    public enum BioEventKind : byte
    {
        Secrete,
        Transport,
        Divide,
        Differentiate,
        Contract,
    }

    public struct BioEvent
    {
        public int Cell;
        public BioEventKind Kind;
        /// null = событие без вещества (хост сам решает, что с ним делать).
        public SubstanceId? Substance;
        public float Value;
    }

    /// Гомогенная популяция программируемых секреторных клеток.
    ///
    /// Порядок вычислений шага — буквально как в Rust-эталоне. Состояние
    /// column-major: слот k нейрона i = `_state[k * n + i]` (проход по
    /// нейронам линейный).
    public sealed class BioCellRuntime
    {
        const int StSignal = 0, StCalcium = 1, StVesicles = 2, StSecretion = 3;
        const int StateCount = 4;

        readonly int _n;
        readonly float _threshold;
        readonly float _releaseGain;
        readonly float _calciumResponse;
        readonly float _recoveryRate;
        readonly float[] _state;

        public BioCellRuntime(int nCells, float threshold, float releaseGain,
                              float calciumResponse, float vesicleRecoveryRate)
        {
            if (nCells <= 0)
                throw new ArgumentException("популяция без клеток", nameof(nCells));
            _n = nCells;
            _threshold = threshold;
            _releaseGain = releaseGain;
            _calciumResponse = calciumResponse;
            _recoveryRate = vesicleRecoveryRate;
            _state = new float[StateCount * nCells];
            // Везикулы стартуют полными.
            for (int i = 0; i < nCells; i++) _state[StVesicles * nCells + i] = 1f;
        }

        public int NCells => _n;

        public void SetSignal(ReadOnlySpan<float> signal)
        {
            if (signal.Length != _n)
                throw new ArgumentException($"длина сигнала {signal.Length} ≠ числу клеток {_n}");
            signal.CopyTo(_state.AsSpan(0, _n));
        }

        public ReadOnlySpan<float> Signal => _state.AsSpan(0, _n);
        public ReadOnlySpan<float> Calcium => _state.AsSpan(StCalcium * _n, _n);
        public ReadOnlySpan<float> Vesicles => _state.AsSpan(StVesicles * _n, _n);
        public ReadOnlySpan<float> Secretion => _state.AsSpan(StSecretion * _n, _n);

        /// Один шаг локальной биологии. События Secrete эмитируются клетками
        /// с secretion > 0 (порог 0.0). Порядок: активация → кальций →
        /// выброс → восстановление пула.
        public void Step()
        {
            var signal = _state.AsSpan(0, _n);
            var calcium = _state.AsSpan(StCalcium * _n, _n);
            var vesicles = _state.AsSpan(StVesicles * _n, _n);
            var secretion = _state.AsSpan(StSecretion * _n, _n);

            for (int i = 0; i < _n; i++)
            {
                // Активация: max(signal − threshold, 0).
                float activation = MathF.Max(signal[i] - _threshold, 0f);

                // Кальций догоняет активацию с конечным временем ответа.
                float ca = calcium[i] + (activation - calcium[i]) * _calciumResponse;
                calcium[i] = ca;

                // Выброс: кальций-драйв, ограниченный пулом везикул.
                float release = MathF.Min(ca * _releaseGain * vesicles[i], vesicles[i]);
                secretion[i] = release;

                // Пул истощается выбросом; пустая ёмкость восстанавливается.
                float recovery = (1f - vesicles[i]) * _recoveryRate;
                float v = vesicles[i] - release + recovery;
                vesicles[i] = Math.Clamp(v, 0f, 1f);
            }
        }
    }

    /// Параметры секреторной популяции (значения по умолчанию — Rust-эталон:
    /// calcium_response 0.25, vesicle_recovery_rate 0.05).
    public struct SecretoryCellConfig
    {
        public float ActivationThreshold;
        public float ReleaseGain;
        public float CalciumResponse;
        public float VesicleRecoveryRate;
    }

}
