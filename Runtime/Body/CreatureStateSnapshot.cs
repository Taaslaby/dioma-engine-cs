// Снимок переносимого состояния организма. Короткоживущая мембрана и
// eligibility намеренно не входят: это persistence snapshot, а не rewind
// нейронного подшага.

namespace Dioma.Body
{
    public sealed class CreatureStateSnapshot
    {
        public readonly Genome Genome;
        public readonly float[] BrainWeights;
        public readonly float[] BirthWeights;
        public readonly float[] BloodConcentrations;
        public readonly int BloodTick;
        public readonly float OrganDamage;
        public readonly int FoodEaten, Collisions, AwakeTicks, SleepLeft, Nights;

        public CreatureStateSnapshot(Genome genome, float[] brainWeights,
            float[] birthWeights, float[] bloodConcentrations, int bloodTick,
            float organDamage, int foodEaten, int collisions, int awakeTicks,
            int sleepLeft, int nights)
        {
            Genome = genome?.Clone();
            BrainWeights = (float[])brainWeights.Clone();
            BirthWeights = (float[])birthWeights.Clone();
            BloodConcentrations = (float[])bloodConcentrations.Clone();
            BloodTick = bloodTick;
            OrganDamage = organDamage;
            FoodEaten = foodEaten;
            Collisions = collisions;
            AwakeTicks = awakeTicks;
            SleepLeft = sleepLeft;
            Nights = nights;
        }
    }
}
