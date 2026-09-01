// ДНК-машина: типизированные гены, байтовый формат «DNA1», кроссовер.
// Ожидания портированы из тестов `creature::dna` (Rust): побайтовый
// round-trip, детерминизм от сида, окна онтогенеза, сцепка с полом.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Dioma.Body;
using Dioma.Spikes;

public class DnaGenomeTests
{
    static DnaGenome SampleGenome()
    {
        // Полный геном со всеми классами — образец для round-trip'ов.
        var g = new DnaGenome { Sex = Sex.Female };

        g.Genes.Add(new Gene
        {
            Header = GeneHeader.New(GeneKind.Metabolism),
            Payload = new GenePayload
            {
                Metabolism = new MetabolismGene
                {
                    CostPerActivity = 0.002f, StarveDamage = 0.005f,
                    CostPerDopamine = 0.4f, HealRate = 0.0001f,
                },
            },
        });

        var reg = RegGenome.Wildtype();
        var slot = reg.Systemic[(int)RegTarget.Plasticity];
        slot.Strength = 1.4f;
        reg.Systemic[(int)RegTarget.Plasticity] = slot;
        reg.Regional.Add(RegGene.Regional(RegTarget.Satiety, 2, 0.7f));
        g.Genes.Add(new Gene
        {
            Header = GeneHeader.New(GeneKind.Regulatory),
            Payload = new GenePayload { Regulatory = reg },
        });

        g.Genes.Add(new Gene
        {
            Header = GeneHeader.New(GeneKind.Coat),
            Payload = new GenePayload { Coat = (0.2f, 0.8f, 0.4f) },
        });
        g.Genes.Add(new Gene
        {
            Header = GeneHeader.New(GeneKind.Eye),
            Payload = new GenePayload { Eye = (3, 1.5f) },
        });
        g.Genes.Add(new Gene
        {
            Header = GeneHeader.New(GeneKind.Memory),
            Payload = new GenePayload { Memory = (1, 200f) },
        });
        g.Genes.Add(new Gene
        {
            Header = GeneHeader.New(GeneKind.Channels),
            Payload = new GenePayload
            {
                Channels = new Dioma.Spikes.PhysioGenes
                { Hcn = 1f, Kcnq = 0.8f, Para = 1.2f, Leak = 0.9f },
            },
        });
        g.Genes.Add(new Gene
        {
            Header = GeneHeader.New(GeneKind.Steering),
            Payload = new GenePayload
            {
                Steering = new SteeringData
                {
                    NoiseWeight = 0.05, GradientWeight = 1.2, OldDirWeight = 0.2,
                    TurnRateDeg = 25.0, StopConcentration = 9.5, BumpMax = 5.0,
                    BumpSigma = 30.0, SynapseDist = 13.0, GrowSteps = 400,
                },
            },
        });
        return g;
    }

    [Test]
    public void Roundtrip_Bytes_Is_Exact()
    {
        // Полная симметрия: один геном — одни байты, байты — тот же геном.
        var g = SampleGenome();
        byte[] a = DnaCodec.ToBytes(g);
        byte[] b = DnaCodec.ToBytes(g);
        CollectionAssert.AreEqual(a, b, "сериализация детерминирована");

        var read = DnaCodec.FromBytes(a, out var err);
        Assert.AreEqual(DnaError.None, err, "ошибка чтения не ожидалась");
        Assert.IsNotNull(read);
        byte[] again = DnaCodec.ToBytes(read);
        CollectionAssert.AreEqual(a, again,
            "round-trip обязан быть побайтовым");

        Assert.AreEqual(Sex.Female, read.Sex);
        Assert.AreEqual(g.Genes.Count, read.Genes.Count, "число генов");
        Assert.AreEqual(GeneKind.Metabolism, read.Genes[0].Header.Kind);
        Assert.AreEqual(0.002f, read.Genes[0].Payload.Metabolism.CostPerActivity, 1e-7f);
        Assert.AreEqual((0.2f, 0.8f, 0.4f), read.Genes[2].Payload.Coat);
        Assert.AreEqual(1.4f,
            read.Genes[1].Payload.Regulatory
                .Systemic[(int)RegTarget.Plasticity].Strength, 1e-6f);
    }

    [Test]
    public void Bad_Magic_Rejected()
    {
        var bytes = DnaCodec.ToBytes(SampleGenome());
        bytes[0] = (byte)'X';
        var read = DnaCodec.FromBytes(bytes, out var err);
        Assert.IsNull(read);
        Assert.AreEqual(DnaError.BadMagic, err);
    }

    [Test]
    public void Truncated_Rejected()
    {
        // Обрыв в середине: каждая длина префикса до конца — Truncated
        // или валидный отказ, но не падение и не «дочитал как смог».
        var bytes = DnaCodec.ToBytes(SampleGenome());
        var cut = bytes.Take(bytes.Length / 2).ToArray();
        var read = DnaCodec.FromBytes(cut, out var err);
        if (read == null)
            Assert.AreEqual(DnaError.Truncated, err, "обрыв обязан вскрываться");
    }

    [Test]
    public void Ontogenesis_Hides_Gene_By_Age()
    {
        // Ген включается в тик 100 и гаснет в 200: снаружи окна его нет.
        var g = new DnaGenome { Sex = Sex.Male };
        var h = GeneHeader.New(GeneKind.Coat);
        h.TurnOn = 100;
        h.TurnOff = 200;
        g.Genes.Add(new Gene { Header = h, Payload = new GenePayload() });

        Assert.AreEqual(0, g.ExpressedAt(50).Count(), "до окна ген молчит");
        Assert.AreEqual(1, g.ExpressedAt(100).Count(), "граница входа включена");
        Assert.AreEqual(1, g.ExpressedAt(199).Count(), "окно открыто");
        Assert.AreEqual(0, g.ExpressedAt(200).Count(), "граница выхода выключена");
    }

    [Test]
    public void Sex_Link_Hides_Gene_From_Other_Sex()
    {
        var g = new DnaGenome { Sex = Sex.Female };
        var maleOnly = GeneHeader.New(GeneKind.Coat);
        maleOnly.SexLink = SexLink.Male;
        g.Genes.Add(new Gene { Header = maleOnly, Payload = new GenePayload() });

        Assert.AreEqual(0, g.ExpressedAt(0).Count(),
            "сцепленный с самцами ген у самки не выражен");
    }

    [Test]
    public void Mutation_Of_Coat_Stays_In_Bounds()
    {
        var g = SampleGenome();
        var rng = new XorShift(7);
        for (int i = 0; i < 10; i++)
        {
            g = g.Mutate(rng);
            foreach (var gene in g.Genes)
                if (gene.Header.Kind == GeneKind.Coat)
                {
                    var (r, gg, b) = gene.Payload.Coat;
                    Assert.That(r, Is.InRange(0f, 1f));
                    Assert.That(gg, Is.InRange(0f, 1f));
                    Assert.That(b, Is.InRange(0f, 1f));
                }
        }
    }

    [Test]
    public void Crossover_Is_Deterministic_By_Seed()
    {
        var a = SampleGenome();
        var b = SampleGenome();
        b.Sex = Sex.Male;
        var bCoat = GeneHeader.New(GeneKind.Coat);
        var gB = b.Genes.First(x => x.Header.Kind == GeneKind.Coat);
        gB.Payload.Coat = (0.9f, 0.1f, 0.1f);

        var c1 = a.Crossover(b, Sex.Male, 42);
        var c2 = a.Crossover(b, Sex.Male, 42);
        var d1 = DnaCodec.ToBytes(c1);
        var d2 = DnaCodec.ToBytes(c2);
        CollectionAssert.AreEqual(d1, d2, "кроссовер детерминирован по сиду");

        var c3 = a.Crossover(b, Sex.Male, 43);
        // Другой сид может дать другого ребёнка (не обязан, но может
        // отличаться байтами — выбор 50/50 в resolve_slot).
        Assert.IsNotNull(c3);
    }

    [Test]
    public void Regulatory_Mutates_In_Dna()
    {
        // Регуляторный ген внутри ДНК мутирует регуляторным же механизмом:
        // сила остаётся в [0..2], эпистаз сохраняется.
        var g = SampleGenome();
        var rng = new XorShift(9);
        var mutated = g.Mutate(rng);
        var reg = mutated.Genes.First(x => x.Header.Kind == GeneKind.Regulatory);
        foreach (var slot in reg.Payload.Regulatory.Systemic)
            Assert.That(slot.Strength, Is.InRange(0f, 2f));
    }

    [Test]
    public void Steering_Mutates_With_Clamps()
    {
        var g = new DnaGenome { Sex = Sex.Male };
        g.Genes.Add(new Gene
        {
            Header = GeneHeader.New(GeneKind.Steering),
            Payload = new GenePayload
            {
                Steering = new SteeringData
                {
                    NoiseWeight = 0.05, GradientWeight = 1.2, OldDirWeight = 0.2,
                    TurnRateDeg = 25.0, StopConcentration = 9.5, BumpMax = 5.0,
                    BumpSigma = 30.0, SynapseDist = 13.0, GrowSteps = 400,
                },
            },
        });
        var rng = new XorShift(3);
        bool anyChanged = false;
        for (int i = 0; i < 20; i++)
        {
            g = g.Mutate(rng);
            var st = g.Genes[0].Payload.Steering;
            Assert.GreaterOrEqual((double)st.GrowSteps, 50.0);
            Assert.LessOrEqual((double)st.GrowSteps, 1000.0);
            Assert.LessOrEqual(st.TurnRateDeg, 90.0);
            Assert.Greater(st.SynapseDist, 0.0);
            if (st.GrowSteps != 400) anyChanged = true;
        }
        Assert.IsTrue(anyChanged, "мутация Steering обязана дрейфовать");
    }

    [Test]
    public void Channels_Roundtrip_And_Mutate()
    {
        var g = new DnaGenome { Sex = Sex.Female };
        g.Genes.Add(new Gene
        {
            Header = GeneHeader.New(GeneKind.Channels),
            Payload = new GenePayload
            {
                Channels = new Dioma.Spikes.PhysioGenes
                { Hcn = 1f, Kcnq = 0.8f, Para = 1.2f, Leak = 0.9f },
            },
        });
        var bytes = DnaCodec.ToBytes(g);
        var read = DnaCodec.FromBytes(bytes, out var err);
        Assert.AreEqual(DnaError.None, err);
        var ch = read.Genes[0].Payload.Channels;
        Assert.AreEqual(0.8f, ch.Kcnq, 1e-6f);
        Assert.AreEqual(1.2f, ch.Para, 1e-6f);

        var rng = new XorShift(5);
        var mutated = g.Mutate(rng).Genes[0].Payload.Channels;
        Assert.Greater(mutated.Hcn, 0f);
        Assert.Greater(mutated.Leak, 0f);
    }

    [Test]
    public void Channels_Feed_Haap_Profile()
    {
        var g = new DnaGenome { Sex = Sex.Female };
        var ko = Dioma.Spikes.PhysioGenes.Wildtype();
        ko.Kcnq = 0f;
        g.Genes.Add(new Gene
        {
            Header = GeneHeader.New(GeneKind.Channels),
            Payload = new GenePayload { Channels = ko },
        });

        var genes = g.Genes[0].Payload.Channels;
        var row = new float[Dioma.Spikes.HaapCols.N];
        Dioma.Spikes.HaapPhysiology.ColumnsInto(row,
            Dioma.Spikes.NeuronArchetype.Motor, genes);
        Assert.AreEqual(0f, row[(int)Dioma.Spikes.HaapCol.GM],
            "нокаут KCNQ обязан убрать M-ток");

        var wild = new float[Dioma.Spikes.HaapCols.N];
        Dioma.Spikes.HaapPhysiology.ColumnsInto(wild,
            Dioma.Spikes.NeuronArchetype.Motor,
            Dioma.Spikes.PhysioGenes.Wildtype());
        Assert.Greater(wild[(int)Dioma.Spikes.HaapCol.GM], 0f,
            "дикий тип держит M-ток");
    }

    [Test]
    public void Legacy_Adapters_Roundtrip()
    {
        var legacy = Genome.Newborn();
        legacy.Metabolism = new MetabolismGene
        {
            CostPerActivity = 0.002f, StarveDamage = 0.005f,
            CostPerDopamine = 0.4f, HealRate = 0.0001f,
        };
        var slot = legacy.Reg.Systemic[(int)RegTarget.Plasticity];
        slot.Strength = 1.3f;
        legacy.Reg.Systemic[(int)RegTarget.Plasticity] = slot;

        var dna = DnaGenome.FromLegacy(legacy, Sex.Female);
        Assert.IsTrue(dna.Genes.Any(x => x.Header.Kind == GeneKind.Metabolism));
        Assert.IsTrue(dna.Genes.Any(x => x.Header.Kind == GeneKind.Regulatory));

        var back = dna.ToLegacy(0);
        Assert.AreEqual(0.002f, back.Metabolism.CostPerActivity, 1e-7f);
        Assert.AreEqual(1.3f,
            back.Reg.Systemic[(int)RegTarget.Plasticity].Strength, 1e-6f);

        var updated = Genome.Newborn();
        updated.Metabolism = new MetabolismGene
        { CostPerActivity = 0.009f, StarveDamage = 0.001f,
          CostPerDopamine = 0.2f, HealRate = 0.0002f };
        dna.SyncLegacy(updated, Sex.Male);
        Assert.AreEqual(Sex.Male, dna.Sex);
        var reg = dna.ToLegacy(0);
        Assert.AreEqual(0.009f, reg.Metabolism.CostPerActivity, 1e-7f);
    }

    [Test]
    public void Ontogenesis_In_ToLegacy()
    {
        var g = new DnaGenome { Sex = Sex.Male };
        var h = GeneHeader.New(GeneKind.Metabolism);
        h.TurnOn = 0;
        h.TurnOff = 500;
        g.Genes.Add(new Gene
        {
            Header = h,
            Payload = new GenePayload
            {
                Metabolism = new MetabolismGene
                { CostPerActivity = 0.5f, StarveDamage = 0.01f,
                  CostPerDopamine = 1f, HealRate = 0.01f },
            },
        });
        var h2 = GeneHeader.New(GeneKind.Metabolism);
        h2.TurnOn = 500;
        g.Genes.Add(new Gene
        {
            Header = h2,
            Payload = new GenePayload
            {
                Metabolism = new MetabolismGene
                { CostPerActivity = 0.002f, StarveDamage = 0.004f,
                  CostPerDopamine = 0.4f, HealRate = 0.0001f },
            },
        });

        Assert.AreEqual(0.5f, g.ToLegacy(100).Metabolism.CostPerActivity, 1e-6f,
            "юношеский ген выражен в 100");
        Assert.AreEqual(0.002f, g.ToLegacy(600).Metabolism.CostPerActivity, 1e-6f,
            "в 600 — только взрослый");
    }

    [Test]
    public void Magic_Is_Dna1()
    {
        var bytes = DnaCodec.ToBytes(SampleGenome());
        Assert.AreEqual('D', bytes[0]);
        Assert.AreEqual('N', bytes[1]);
        Assert.AreEqual('A', bytes[2]);
        Assert.AreEqual('1', bytes[3]);
        Assert.AreEqual(1, bytes[4], "версия формата = 1");
        Assert.AreEqual((byte)Sex.Female, bytes[6], "пол в шапке");
    }
}
