// Авторегистрация Burst-кернела. Ядро (`Dioma.Spikes`) о нём не знает —
// зависимость направлена в одну сторону, и билд без этого модуля просто
// работает на managed-кернеле.
//
// `RuntimeInitializeOnLoadMethod` покрывает плеер и Play Mode,
// `InitializeOnLoad` — редактор (тесты EditMode).

#if DIOMA_BURST
using UnityEngine;

namespace Dioma.Spikes.Burst
{
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    public static class BurstKernelRegistration
    {
#if UNITY_EDITOR
        static BurstKernelRegistration() => Register();
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Register()
        {
            SpikeKernels.Register(KernelMode.Burst,
                (layout, values) => new BurstSpikeKernel(layout, values));
            SpikeKernels.RegisterPlasticity(KernelMode.Burst,
                layout => new BurstPlasticityKernel(layout));
        }
    }
}
#endif
