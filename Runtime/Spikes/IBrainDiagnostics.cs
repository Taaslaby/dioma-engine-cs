// Read-only capability для HUD, мониторов и профилирования. Симуляции не
// нужны ни мембраны, ни raster спайков, поэтому потребители принимают этот
// узкий интерфейс вместо DiomaBrain.

using System.Collections.Generic;

namespace Dioma.Spikes
{
    public interface IBrainDiagnostics
    {
        int NeuronCount { get; }
        int SynapseCount { get; }
        IReadOnlyList<DiomaBrain.Region> Regions { get; }
        NeuronModel Model { get; }
        string KernelName { get; }
        string PlasticityKernelName { get; }
        IReadOnlyList<int> SpikeHistory { get; }
        float LastDopamine { get; }
        void ReadMembrane(float[] into);
        float[] WeightsSnapshot();
        double RegionPairWeight(int preRegion, int postRegion);
        double WeightSum();
        double EligibilitySum();
        (float[] D1, float[] D2) ReadPathTraces();
    }
}
