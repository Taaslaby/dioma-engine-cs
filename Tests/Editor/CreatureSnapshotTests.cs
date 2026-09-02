using System.IO;
using NUnit.Framework;
using Dioma.Bio;
using Dioma.Body;
using Dioma.Spikes;

public class CreatureSnapshotTests
{
    [Test]
    public void SpeciesCreature_Snapshot_RoundTrips_Persistent_State()
    {
        string json = File.ReadAllText(Path.Combine(TestPaths.DataDir,
            "dioma_fish_connectome.json"));
        var species = Species.Fish();
        using var sourceBrain = DiomaBrain.FromJson(json, KernelMode.Managed);
        var source = new SpeciesCreature(species.Bind(sourceBrain));
        source.Blood.SetConcentration(Chem.Glucose, 77f);
        source.Collide();
        source.Tick(new SenseFrame(species.Sense));
        var snapshot = source.CaptureState();

        // Снимок не должен разделять изменяемые массивы с исходником.
        float capturedGlucose = snapshot.BloodConcentrations[Chem.Glucose];
        source.Blood.SetConcentration(Chem.Glucose, 1f);
        Assert.AreEqual(capturedGlucose, snapshot.BloodConcentrations[Chem.Glucose]);

        using var restoredBrain = DiomaBrain.FromJson(json, KernelMode.Managed);
        var restored = SpeciesCreature.FromState(species.Bind(restoredBrain), snapshot);
        CollectionAssert.AreEqual(snapshot.BrainWeights, restored.Brain.CsrValues());
        CollectionAssert.AreEqual(snapshot.BirthWeights, restored.Brain.BirthWeights());
        CollectionAssert.AreEqual(snapshot.BloodConcentrations,
            restored.Blood.ConcentrationSnapshot());
        Assert.AreEqual(snapshot.BloodTick, restored.Blood.Tick);
        Assert.AreEqual(snapshot.OrganDamage, restored.Blood.Organ.Damage);
        Assert.AreEqual(snapshot.FoodEaten, restored.FoodEaten);
        Assert.AreEqual(snapshot.Collisions, restored.Collisions);
        Assert.AreEqual(snapshot.AwakeTicks, restored.AwakeTicks);
        Assert.AreEqual(snapshot.SleepLeft, restored.SleepLeft);
        Assert.AreEqual(snapshot.Nights, restored.Nights);
    }
}
