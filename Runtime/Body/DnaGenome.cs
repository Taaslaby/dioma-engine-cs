// ДНК-машина: типизированный геном в духе Creatures — гены с шапками
// (класс + вариант + окно онтогенеза + сцепка с полом + мутабельность),
// байтовый формат «DNA1» для сейвов, кроссовер двух родителей.
// Порт `creature::dna` (Rust) байт-в-байт: один геном — одни байты.
//
// Чем это отличается от `Genome` (legacy): Genome хранит ВЫРАЖЕННЫЙ фенотип
// (веса + метаболизм + инстинкты) и не знает про пол и онтогенез. DnaGenome
// хранит ГЕНЫ — с окнами включения/выключения по возрасту и сцепкой с полом.
// Адаптеры ниже переводят туда-обратно: legacy-геном сворачивается в ДНК
// (по одному гену на класс), ДНК разворачивается в legacy для существа.
//
// Незнакомый класс (из будущего файла) читается как Raw: данные сохраняются
// как есть, кроссовер не смешивает, мутация не трогает — геном из будущего
// не портится прошлой сборкой.

using System;
using System.Collections.Generic;
using System.IO;
using Dioma.Body;
using Dioma.Spikes;

namespace Dioma.Body
{
    /// Класс гена. Порядок кодов — контракт формата DNA1.
    public enum GeneKind
    {
        Steering = 0,
        Rules = 1,
        Instincts = 2,
        Metabolism = 3,
        Regulatory = 4,
        Coat = 5,
        Eye = 6,
        Memory = 7,
    }

    /// Сцепка гена с полом (как в Creatures: гены на половых хромосомах
    /// выражены только у одного пола).
    public enum SexLink : byte
    {
        Ignore = 0,
        Male = 1,
        Female = 2,
    }

    /// Пол существа (1/2 — коды формата, как C3).
    public enum Sex : byte
    {
        Male = 1,
        Female = 2,
    }

    public static class SexOps
    {
        /// Пол по сиду рождения: не мутирует, выбирается заново.
        public static Sex FromSeed(ulong seed) =>
            (seed & 1) == 0 ? Sex.Male : Sex.Female;

        public static bool Ok(this SexLink link, Sex sex) => link switch
        {
            SexLink.Ignore => true,
            SexLink.Male => sex == Sex.Male,
            SexLink.Female => sex == Sex.Female,
            _ => false,
        };
    }

    /// Шапка гена: класс + онтогенез + сцепка + мутабельность.
    public struct GeneHeader
    {
        public GeneKind Kind;
        /// Версия/подвид payload.
        public ushort Variant;
        /// Тик мира, с которого ген выражен.
        public ulong TurnOn;
        /// Тик, с которого ген гаснет; ulong.MaxValue = бессрочно.
        public ulong TurnOff;
        public SexLink SexLink;
        /// Участвует ли в мутации/кроссовере.
        public bool Mutable;

        public static GeneHeader New(GeneKind kind) => new GeneHeader
        {
            Kind = kind,
            Variant = 0,
            TurnOn = 0,
            TurnOff = ulong.MaxValue,
            SexLink = SexLink.Ignore,
            Mutable = true,
        };

        public bool ExpressedAt(ulong age) => age >= TurnOn && age < TurnOff;
    }

    /// Payload гена: у каждого класса своя форма. Raw — незнакомый класс:
    /// данные как есть, идентификатор в Variant шапки.
    public class GenePayload
    {
        /// Steering: 8 f64 + growSteps — параметры роста коннектома.
        public SteeringData Steering;
        /// Rules: список генов правил Пифии (template + 8 f32).
        public List<RuleData> Rules;
        /// Instincts: список (inputs, dopamine).
        public List<Instinct> Instincts;
        /// Metabolism: 4 ручки тела.
        public MetabolismGene Metabolism;
        /// Regulatory: системные + региональные регуляторные гены.
        public RegGenome Regulatory;
        /// Coat: rgb 0..1 (рендер-ген).
        public (float R, float G, float B) Coat;
        /// Eye: вариант модели + гейн.
        public (ushort Variant, float Gain) Eye;
        /// Memory: вариант + tau.
        public (ushort Variant, float Tau) Memory;
        /// Raw: незнакомый класс — float'ы как есть.
        public float[] Raw;
    }

    public sealed class Gene
    {
        public GeneHeader Header;
        public GenePayload Payload;
    }

    /// Данные гена роста (Steering из Прометея: параметры выращивания).
    public struct SteeringData
    {
        public double NoiseWeight, GradientWeight, OldDirWeight;
        public double TurnRateDeg, StopConcentration, BumpMax, BumpSigma;
        public double SynapseDist;
        public ulong GrowSteps;
    }

    /// Данные гена правила: шаблон + 8 параметров.
    public struct RuleData
    {
        public byte Template;
        public float[] P;
    }

    /// ДНК-геном: список генов + пол.
    public sealed class DnaGenome
    {
        public const ushort FormatVersion = 1;
        public const string Magic = "DNA1";

        public List<Gene> Genes = new List<Gene>();
        public Sex Sex;

        // ── Сигмы мутаций (константы Rust) ──────────────────────────
        public const float CoatSigma = 0.05f;
        public const float EyeGainSigma = 0.05f;
        public const float MemTauSigma = 0.05f;
        public const float RegSigma = 0.05f;

        /// Мутация: каждый mutable-ген дрейфует по своему классу.
        public DnaGenome Mutate(XorShift rng)
        {
            var child = new DnaGenome { Sex = Sex };
            foreach (var gene in Genes)
            {
                if (!gene.Header.Mutable)
                {
                    child.Genes.Add(gene);
                    continue;
                }
                var g = new Gene { Header = gene.Header, Payload = gene.Payload };
                switch (gene.Header.Kind)
                {
                    case GeneKind.Coat:
                        var c = gene.Payload.Coat;
                        g.Payload.Coat = (
                            Math.Clamp(c.R + (float)rng.Gauss() * CoatSigma, 0f, 1f),
                            Math.Clamp(c.G + (float)rng.Gauss() * CoatSigma, 0f, 1f),
                            Math.Clamp(c.B + (float)rng.Gauss() * CoatSigma, 0f, 1f));
                        break;
                    case GeneKind.Eye:
                        var e = gene.Payload.Eye;
                        g.Payload.Eye = (e.Variant,
                            MathF.Max(e.Gain + (float)rng.Gauss() * EyeGainSigma, 0f));
                        break;
                    case GeneKind.Memory:
                        var m = gene.Payload.Memory;
                        g.Payload.Memory = (m.Variant,
                            MathF.Max(m.Tau * (1f + (float)rng.Gauss() * MemTauSigma), 0f));
                        break;
                    case GeneKind.Regulatory:
                        g.Payload.Regulatory =
                            gene.Payload.Regulatory.Mutate(rng, RegSigma);
                        break;
                    default:
                        break;  // Steering/Rules/Instincts/Metabolism: как в Rust,
                                // дрейфуют адаптерами legacy-генома
                }
                child.Genes.Add(g);
            }
            return child;
        }

        /// Кроссовер: гены группируются по классу, слоты выравниваются по
        /// индексу, конфликт решается по сцепке с полом ребёнка и
        /// мутабельности. Raw и не-mutable наследуются от A целиком.
        public DnaGenome Crossover(DnaGenome other, Sex childSex, ulong seed)
        {
            var rng = new XorShift(seed);
            var child = new DnaGenome { Sex = childSex };
            foreach (var (kind, listA, listB) in AlignedKinds(other))
            {
                int n = Math.Max(listA.Count, listB.Count);
                for (int i = 0; i < n; i++)
                {
                    var resolved = ResolveSlot(
                        i < listA.Count ? listA[i] : null,
                        i < listB.Count ? listB[i] : null, childSex, rng);
                    if (resolved != null)
                        child.Genes.Add(resolved);
                }
            }
            return child;
        }

        IEnumerable<(GeneKind, List<Gene>, List<Gene>)> AlignedKinds(DnaGenome other)
        {
            // Порядок первого появления (как group_by_kind в Rust).
            var seen = new HashSet<GeneKind>();
            foreach (var g in Genes)
            {
                if (!seen.Add(g.Header.Kind)) continue;
                yield return (g.Header.Kind, OfKind(Genes, g.Header.Kind),
                    OfKind(other.Genes, g.Header.Kind));
            }
            foreach (var g in other.Genes)
            {
                if (seen.Contains(g.Header.Kind)) continue;
                seen.Add(g.Header.Kind);
                yield return (g.Header.Kind, OfKind(Genes, g.Header.Kind),
                    OfKind(other.Genes, g.Header.Kind));
            }
        }

        static List<Gene> OfKind(List<Gene> genes, GeneKind kind)
        {
            var list = new List<Gene>();
            foreach (var g in genes)
                if (g.Header.Kind == kind) list.Add(g);
            return list;
        }

        static Gene ResolveSlot(Gene a, Gene b, Sex childSex, XorShift rng)
        {
            if (a == null && b == null) return null;
            if (a == null) return b.Header.SexLink.Ok(childSex) ? b : null;
            if (b == null) return a.Header.SexLink.Ok(childSex) ? a : null;
            bool aOk = a.Header.SexLink.Ok(childSex);
            bool bOk = b.Header.SexLink.Ok(childSex);
            if (!aOk && !bOk) return null;
            if (!aOk) return b;
            if (!bOk) return a;
            // Оба доступны: Raw и не-мутабельные наследуются от A целиком.
            if (!a.Header.Mutable || !b.Header.Mutable) return a;
            if (a.Header.Kind is GeneKind.Coat or GeneKind.Eye or GeneKind.Memory)
            {
                // Новые классы: по-канальное/по-полевое смешивание.
                var mixed = new Gene { Header = a.Header, Payload = a.Payload };
                if (rng.Unit() < 0.5)
                    mixed.Payload.Coat = b.Payload.Coat;
                if (rng.Unit() < 0.5)
                    mixed.Payload.Eye = b.Payload.Eye;
                if (rng.Unit() < 0.5)
                    mixed.Payload.Memory = b.Payload.Memory;
                return mixed;
            }
            return rng.Unit() < 0.5 ? a : b;
        }

        /// Гены, выраженные в данном возрасте у данного пола.
        public IEnumerable<Gene> ExpressedAt(ulong age)
        {
            foreach (var g in Genes)
                if (g.Header.ExpressedAt(age) && g.Header.SexLink.Ok(Sex))
                    yield return g;
        }
    }

    /// Ошибки разбора «DNA1».
    public enum DnaError
    {
        None = 0,
        BadMagic,
        UnsupportedVersion,
        InvalidSex,
        InvalidSexLink,
        Truncated,
    }

    /// Байтовый формат «DNA1»: magic + версия + пол + число генов + гены.
    /// Полная симметрия Save/Load, побайтовая детерминированность.
    public static class DnaCodec
    {
        public static byte[] ToBytes(DnaGenome g)
        {
            var out_ = new MemoryStream();
            Write(out_, MagicBytes());
            WriteU16(out_, DnaGenome.FormatVersion);
            out_.WriteByte((byte)g.Sex);
            WriteU32(out_, (uint)g.Genes.Count);
            foreach (var gene in g.Genes) WriteGene(out_, gene);
            return out_.ToArray();
        }

        public static DnaGenome FromBytes(byte[] bytes, out DnaError error)
        {
            error = DnaError.Truncated;
            if (bytes.Length < 10) return null;
            int pos = 0;
            if (bytes[0] != 'D' || bytes[1] != 'N' || bytes[2] != 'A' || bytes[3] != '1')
            {
                error = DnaError.BadMagic;
                return null;
            }
            pos = 4;
            ushort version = ReadU16(bytes, ref pos);
            if (version != DnaGenome.FormatVersion)
            {
                error = DnaError.UnsupportedVersion;
                return null;
            }
            byte sexB = bytes[pos++];
            if (sexB != 1 && sexB != 2)
            {
                error = DnaError.InvalidSex;
                return null;
            }
            var genome = new DnaGenome { Sex = (Sex)sexB };
            int n = (int)ReadU32(bytes, ref pos);
            try
            {
                for (int i = 0; i < n; i++)
                    if (!ReadGene(bytes, ref pos, genome, out error)) return null;
            }
            catch (DnaTruncatedException)
            {
                error = DnaError.Truncated;
                return null;
            }
            error = DnaError.None;
            return genome;
        }

        static byte[] MagicBytes() => new byte[] { (byte)'D', (byte)'N', (byte)'A', (byte)'1' };

        static void Write(MemoryStream s, byte[] b) => s.Write(b, 0, b.Length);
        static void WriteU16(MemoryStream s, ushort v) =>
            s.Write(new[] { (byte)v, (byte)(v >> 8) }, 0, 2);
        static void WriteU32(MemoryStream s, uint v) =>
            s.Write(new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) }, 0, 4);
        static void WriteU64(MemoryStream s, ulong v)
        {
            var b = new byte[8];
            for (int i = 0; i < 8; i++) b[i] = (byte)(v >> (8 * i));
            s.Write(b, 0, 8);
        }
        static void WriteF32(MemoryStream s, float v) =>
            WriteU32(s, unchecked((uint)BitConverter.SingleToInt32Bits(v)));
        static void WriteF64(MemoryStream s, double v) =>
            WriteU64(s, (ulong)BitConverter.DoubleToInt64Bits(v));

        /// Байтов больше нет: обрыв вместо IndexOutOfRange. Читатели
        /// вызывают это перед каждым доступом, читающем >1 байта.
        static void Need(byte[] b, ref int pos, int n)
        {
            if (pos + n > b.Length)
                throw new DnaTruncatedException();
        }

        class DnaTruncatedException : Exception { }

        static ushort ReadU16(byte[] b, ref int pos)
        {
            Need(b, ref pos, 2);
            ushort v = (ushort)(b[pos] | (b[pos + 1] << 8));
            pos += 2;
            return v;
        }
        static uint ReadU32(byte[] b, ref int pos)
        {
            Need(b, ref pos, 4);
            uint v = (uint)(b[pos] | (b[pos + 1] << 8) |
                            (b[pos + 2] << 16) | (b[pos + 3] << 24));
            pos += 4;
            return v;
        }
        static ulong ReadU64(byte[] b, ref int pos)
        {
            Need(b, ref pos, 8);
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)b[pos + i] << (8 * i);
            pos += 8;
            return v;
        }
        static float ReadF32(byte[] b, ref int pos) =>
            BitConverter.Int32BitsToSingle((int)ReadU32(b, ref pos));
        static double ReadF64(byte[] b, ref int pos) =>
            BitConverter.Int64BitsToDouble((long)ReadU64(b, ref pos));
        static byte ReadU8(byte[] b, ref int pos)
        {
            Need(b, ref pos, 1);
            return b[pos++];
        }

        static void WriteGene(MemoryStream s, Gene g)
        {
            WriteU16(s, (ushort)g.Header.Kind);
            WriteU16(s, g.Header.Variant);
            WriteU64(s, g.Header.TurnOn);
            WriteU64(s, g.Header.TurnOff);
            s.WriteByte((byte)g.Header.SexLink);
            s.WriteByte(g.Header.Mutable ? (byte)1 : (byte)0);

            switch (g.Header.Kind)
            {
                case GeneKind.Steering:
                    var st = g.Payload.Steering;
                    WriteF64(s, st.NoiseWeight);
                    WriteF64(s, st.GradientWeight);
                    WriteF64(s, st.OldDirWeight);
                    WriteF64(s, st.TurnRateDeg);
                    WriteF64(s, st.StopConcentration);
                    WriteF64(s, st.BumpMax);
                    WriteF64(s, st.BumpSigma);
                    WriteF64(s, st.SynapseDist);
                    WriteU64(s, st.GrowSteps);
                    break;
                case GeneKind.Rules:
                    WriteU32(s, (uint)g.Payload.Rules.Count);
                    foreach (var r in g.Payload.Rules)
                    {
                        s.WriteByte(r.Template);
                        foreach (var p in r.P) WriteF32(s, p);
                    }
                    break;
                case GeneKind.Instincts:
                    WriteU32(s, (uint)g.Payload.Instincts.Count);
                    foreach (var i in g.Payload.Instincts)
                    {
                        WriteU32(s, (uint)i.Inputs.Count);
                        foreach (var (idx, drive) in i.Inputs)
                        {
                            WriteU64(s, (ulong)idx);
                            WriteF32(s, drive);
                        }
                        WriteF32(s, i.Dopamine);
                    }
                    break;
                case GeneKind.Metabolism:
                    var m = g.Payload.Metabolism;
                    WriteF32(s, m.CostPerActivity);
                    WriteF32(s, m.StarveDamage);
                    WriteF32(s, m.CostPerDopamine);
                    WriteF32(s, m.HealRate);
                    break;
                case GeneKind.Regulatory:
                    var reg = g.Payload.Regulatory;
                    foreach (var slot in reg.Systemic) WriteRegGene(s, slot);
                    WriteU32(s, (uint)reg.Regional.Count);
                    foreach (var rg in reg.Regional) WriteRegGene(s, rg);
                    break;
                case GeneKind.Coat:
                    WriteF32(s, g.Payload.Coat.R);
                    WriteF32(s, g.Payload.Coat.G);
                    WriteF32(s, g.Payload.Coat.B);
                    break;
                case GeneKind.Eye:
                    WriteU16(s, g.Payload.Eye.Variant);
                    WriteF32(s, g.Payload.Eye.Gain);
                    break;
                case GeneKind.Memory:
                    WriteU16(s, g.Payload.Memory.Variant);
                    WriteF32(s, g.Payload.Memory.Tau);
                    break;
            }
        }

        static void WriteRegGene(MemoryStream s, RegGene rg)
        {
            s.WriteByte((byte)rg.Target);
            WriteU64(s, (ulong)rg.Region);
            WriteF32(s, rg.Strength);
            s.WriteByte(rg.Dominant ? (byte)1 : (byte)0);
            WriteU64(s, (ulong)rg.OnTick);
            WriteU64(s, (ulong)rg.OffTick);
        }

        static bool ReadGene(byte[] b, ref int pos, DnaGenome into, out DnaError error)
        {
            error = DnaError.Truncated;
            var kind = (GeneKind)ReadU16(b, ref pos);
            var header = new GeneHeader
            {
                Kind = kind,
                Variant = ReadU16(b, ref pos),
                TurnOn = ReadU64(b, ref pos),
                TurnOff = ReadU64(b, ref pos),
                SexLink = (SexLink)ReadU8(b, ref pos),
                Mutable = ReadU8(b, ref pos) != 0,
            };
            var payload = new GenePayload();
            switch (kind)
            {
                case GeneKind.Steering:
                    payload.Steering = new SteeringData
                    {
                        NoiseWeight = ReadF64(b, ref pos),
                        GradientWeight = ReadF64(b, ref pos),
                        OldDirWeight = ReadF64(b, ref pos),
                        TurnRateDeg = ReadF64(b, ref pos),
                        StopConcentration = ReadF64(b, ref pos),
                        BumpMax = ReadF64(b, ref pos),
                        BumpSigma = ReadF64(b, ref pos),
                        SynapseDist = ReadF64(b, ref pos),
                        GrowSteps = ReadU64(b, ref pos),
                    };
                    break;
                case GeneKind.Rules:
                {
                    int n = (int)ReadU32(b, ref pos);
                    var list = new List<RuleData>(n);
                    for (int i = 0; i < n; i++)
                    {
                        var r = new RuleData { Template = ReadU8(b, ref pos), P = new float[8] };
                        for (int k = 0; k < 8; k++) r.P[k] = ReadF32(b, ref pos);
                        list.Add(r);
                    }
                    payload.Rules = list;
                    break;
                }
                case GeneKind.Instincts:
                {
                    int n = (int)ReadU32(b, ref pos);
                    var list = new List<Instinct>(n);
                    for (int i = 0; i < n; i++)
                    {
                        int m = (int)ReadU32(b, ref pos);
                        var inputs = new List<(int, float)>(m);
                        for (int j = 0; j < m; j++)
                            inputs.Add(((int)ReadU64(b, ref pos), ReadF32(b, ref pos)));
                        list.Add(new Instinct(inputs, ReadF32(b, ref pos)));
                    }
                    payload.Instincts = list;
                    break;
                }
                case GeneKind.Metabolism:
                    payload.Metabolism = new MetabolismGene
                    {
                        CostPerActivity = ReadF32(b, ref pos),
                        StarveDamage = ReadF32(b, ref pos),
                        CostPerDopamine = ReadF32(b, ref pos),
                        HealRate = ReadF32(b, ref pos),
                    };
                    break;
                case GeneKind.Regulatory:
                {
                    var reg = RegGenome.Wildtype();
                    for (int i = 0; i < RegTargets.N; i++)
                        reg.Systemic[i] = ReadRegGene(b, ref pos);
                    int n = (int)ReadU32(b, ref pos);
                    for (int i = 0; i < n; i++)
                        reg.Regional.Add(ReadRegGene(b, ref pos));
                    payload.Regulatory = reg;
                    break;
                }
                case GeneKind.Coat:
                    payload.Coat = (ReadF32(b, ref pos), ReadF32(b, ref pos),
                                   ReadF32(b, ref pos));
                    break;
                case GeneKind.Eye:
                    payload.Eye = (ReadU16(b, ref pos), ReadF32(b, ref pos));
                    break;
                case GeneKind.Memory:
                    payload.Memory = (ReadU16(b, ref pos), ReadF32(b, ref pos));
                    break;
                default:
                    // Незнакомый класс: float'ы как есть (Raw-семантика).
                    int nRaw = (int)ReadU32(b, ref pos);
                    var data = new float[nRaw];
                    for (int i = 0; i < nRaw; i++) data[i] = ReadF32(b, ref pos);
                    payload.Raw = data;
                    break;
            }
            into.Genes.Add(new Gene { Header = header, Payload = payload });
            error = DnaError.None;
            return true;
        }

        static RegGene ReadRegGene(byte[] b, ref int pos)
        {
            var target = (RegTarget)ReadU8(b, ref pos);
            var g = new RegGene
            {
                Target = target,
                Region = (int)ReadU64(b, ref pos),
                Strength = ReadF32(b, ref pos),
                Dominant = ReadU8(b, ref pos) != 0,
                OnTick = (int)ReadU64(b, ref pos),
                OffTick = (int)ReadU64(b, ref pos),
            };
            return g;
        }
    }
}
