// Выбор вычислителя мембраны. Кернелы регистрируются здесь, а `DiomaBrain`
// просит нужный по режиму — так модуль Burst подключается одной ссылкой в
// asmdef и не тянет за собой зависимость в обратную сторону: ядро о нём не
// знает, он о ядре знает.

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

        static readonly Dictionary<KernelMode, Func<SpikeLayout, float[], ISpikeKernel>> Factories
            = new Dictionary<KernelMode, Func<SpikeLayout, float[], ISpikeKernel>>
            {
                [KernelMode.Managed] = (layout, values) => new ManagedSpikeKernel(layout, values),
            };

        /// Зарегистрировать реализацию (вызывает модуль Burst при загрузке).
        public static void Register(KernelMode mode,
                                    Func<SpikeLayout, float[], ISpikeKernel> factory) =>
            Factories[mode] = factory;

        public static bool IsAvailable(KernelMode mode) => Factories.ContainsKey(mode);

        /// Создать кернел. Недоступный режим не роняет игру, а откатывается на
        /// Managed: билд без Burst-модуля должен запускаться, пусть и медленнее.
        public static ISpikeKernel Create(KernelMode mode, SpikeLayout layout, float[] values)
        {
            if (mode == KernelMode.Fastest)
                mode = Factories.ContainsKey(KernelMode.Burst) ? KernelMode.Burst : KernelMode.Managed;
            if (!Factories.TryGetValue(mode, out var factory))
                factory = Factories[KernelMode.Managed];
            return factory(layout, values);
        }
    }
}
