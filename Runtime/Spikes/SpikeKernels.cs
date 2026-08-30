// Выбор вычислителей: мембрана (ISpikeKernel) и пластичность
// (IPlasticityKernel). Кернелы регистрируются здесь, а `DiomaBrain` просит
// нужный по режиму — модуль Burst подключается одной ссылкой в asmdef и не
// тянет зависимость в обратную сторону: ядро о нём не знает, он о ядре знает.

using System;
using System.Collections.Generic;

namespace Dioma.Spikes
{
    public enum KernelMode
    {
        /// Обычный C#: детерминирован, отлаживается, работает под чистым .NET.
        Managed,
        /// Burst: тот же алгоритм в нативном коде. Требует модуля
        /// `Dioma.Spikes.Burst` — иначе тихо падает в Managed.
        Burst,
        /// Лучший доступный: Burst, если зарегистрирован, иначе Managed.
        Fastest,
    }

    public static class SpikeKernels
    {
        /// Режим по умолчанию для новых мозгов. Managed — потому что
        /// предсказуемость дороже скорости, пока особей единицы.
        public static KernelMode Default = KernelMode.Managed;

        static readonly Dictionary<KernelMode, Func<SpikeLayout, float[], ISpikeKernel>> Membrane
            = new Dictionary<KernelMode, Func<SpikeLayout, float[], ISpikeKernel>>
            {
                [KernelMode.Managed] = (layout, values) => new ManagedSpikeKernel(layout, values),
            };

        static readonly Dictionary<KernelMode, Func<PlasticityLayout, IPlasticityKernel>> Plasticity
            = new Dictionary<KernelMode, Func<PlasticityLayout, IPlasticityKernel>>
            {
                [KernelMode.Managed] = layout => new ManagedPlasticityKernel(layout),
            };

        /// Зарегистрировать вычислитель мембраны (вызывает модуль Burst).
        public static void Register(KernelMode mode,
                                    Func<SpikeLayout, float[], ISpikeKernel> factory) =>
            Membrane[mode] = factory;

        /// Зарегистрировать вычислитель пластичности.
        public static void RegisterPlasticity(KernelMode mode,
                                              Func<PlasticityLayout, IPlasticityKernel> factory) =>
            Plasticity[mode] = factory;

        /// Есть ли у режима хотя бы мембранная реализация.
        public static bool IsAvailable(KernelMode mode) => Membrane.ContainsKey(mode);

        public static bool IsPlasticityAvailable(KernelMode mode) => Plasticity.ContainsKey(mode);

        /// Разрешить `Fastest` в конкретный режим по тому, что зарегистрировано.
        static KernelMode Resolve<T>(KernelMode mode, Dictionary<KernelMode, T> table)
        {
            if (mode == KernelMode.Fastest)
                return table.ContainsKey(KernelMode.Burst) ? KernelMode.Burst : KernelMode.Managed;
            return table.ContainsKey(mode) ? mode : KernelMode.Managed;
        }

        /// Создать вычислитель мембраны. Недоступный режим не роняет игру, а
        /// откатывается на Managed: билд без Burst-модуля должен запускаться.
        public static ISpikeKernel Create(KernelMode mode, SpikeLayout layout, float[] values) =>
            Membrane[Resolve(mode, Membrane)](layout, values);

        public static IPlasticityKernel CreatePlasticity(KernelMode mode, PlasticityLayout layout) =>
            Plasticity[Resolve(mode, Plasticity)](layout);
    }
}
