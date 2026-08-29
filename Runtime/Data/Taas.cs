// Формат `.taas` v2 — сохранения и снапшоты. Порт `taas-format` (Rust).
//
//   MAGIC "TAAS2" (5) | HEADER_LEN u64 LE (8) | HEADER JSON | SECTION_DATA
//
// Файл — набор ИМЕНОВАННЫХ типизированных секций: читателю достаточно знать
// имя, тип и длину он берёт из манифеста. Каждая секция подписана CRC32 по
// распакованным данным, так что битый файл обнаруживается при чтении, а не
// проявляется потом странным поведением существа.
//
// Сжатие zstd из эталона здесь НЕ поддерживается на запись (в Unity нет
// zstd без внешней зависимости); при чтении сжатая секция вызывает понятную
// ошибку вместо тихого мусора. Для сохранений это не проблема: веса
// 32k синапсов — это 128 КБ.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Dioma.Data
{
    public enum SectionKind
    {
        F32, I32, I64, U64, U8, Str, Json, Blob,
    }

    public static class SectionKindExt
    {
        public static string Name(this SectionKind k) => k switch
        {
            SectionKind.F32 => "f32",
            SectionKind.I32 => "i32",
            SectionKind.I64 => "i64",
            SectionKind.U64 => "u64",
            SectionKind.U8 => "u8",
            SectionKind.Str => "str",
            SectionKind.Json => "json",
            _ => "blob",
        };

        public static SectionKind Parse(string s) => s switch
        {
            "f32" => SectionKind.F32,
            "i32" => SectionKind.I32,
            "i64" => SectionKind.I64,
            "u64" => SectionKind.U64,
            "u8" => SectionKind.U8,
            "str" => SectionKind.Str,
            "json" => SectionKind.Json,
            "blob" => SectionKind.Blob,
            _ => throw new FormatException($"неизвестный тип секции: {s}"),
        };

        /// Размер элемента для числовых типов; 0 для str/json/blob
        /// (у них `count` — длина в байтах).
        public static int ElemSize(this SectionKind k) => k switch
        {
            SectionKind.F32 => 4,
            SectionKind.I32 => 4,
            SectionKind.I64 => 8,
            SectionKind.U64 => 8,
            SectionKind.U8 => 1,
            _ => 0,
        };
    }

    public sealed class Section
    {
        public string Name;
        public SectionKind Kind;
        /// Для числовых типов — число элементов; иначе длина в байтах.
        public int Count;
        public long Offset;
        public long Size;
        public uint? Crc32;
        public string Compression;
        public long? UncompressedSize;
    }

    public static class Taas
    {
        public static readonly byte[] Magic = Encoding.ASCII.GetBytes("TAAS2");
        public const string Version = "2.0.0";
    }

    public sealed class TaasWriter
    {
        readonly List<Section> _sections = new List<Section>();
        readonly MemoryStream _payload = new MemoryStream();
        readonly Dictionary<string, string> _metadata = new Dictionary<string, string>();
        string _game;

        /// Чем/для чего писан файл («dioma», «arena») — при загрузке видно,
        /// какой мир открывать.
        public TaasWriter Game(string game)
        {
            _game = game;
            return this;
        }

        public TaasWriter Metadata(string key, string value)
        {
            _metadata[key] = value;
            return this;
        }

        public TaasWriter Push(string name, SectionKind kind, int count, byte[] data)
        {
            _sections.Add(new Section
            {
                Name = name,
                Kind = kind,
                Count = count,
                Offset = _payload.Length,
                Size = data.Length,
                Crc32 = Data.Crc32.Compute(data),
            });
            _payload.Write(data, 0, data.Length);
            return this;
        }

        public TaasWriter F32(string name, float[] data)
        {
            var bytes = new byte[data.Length * 4];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            return Push(name, SectionKind.F32, data.Length, bytes);
        }

        public TaasWriter I32(string name, int[] data)
        {
            var bytes = new byte[data.Length * 4];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            return Push(name, SectionKind.I32, data.Length, bytes);
        }

        public TaasWriter I64(string name, long[] data)
        {
            var bytes = new byte[data.Length * 8];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            return Push(name, SectionKind.I64, data.Length, bytes);
        }

        public TaasWriter U8(string name, byte[] data) =>
            Push(name, SectionKind.U8, data.Length, data);

        public TaasWriter Str(string name, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            return Push(name, SectionKind.Str, bytes.Length, bytes);
        }

        /// JSON-секция: строку формирует вызывающий (у движка нет
        /// сериализатора — только парсер).
        public TaasWriter JsonRaw(string name, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            return Push(name, SectionKind.Json, bytes.Length, bytes);
        }

        public byte[] ToBytes()
        {
            var header = new StringBuilder();
            header.Append("{\"version\":\"").Append(Taas.Version).Append("\",");
            header.Append("\"created\":\"").Append(
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")).Append('"');
            if (!string.IsNullOrEmpty(_game))
                header.Append(",\"game\":\"").Append(JsonEscape(_game)).Append('"');
            header.Append(",\"sections\":[");
            for (int i = 0; i < _sections.Count; i++)
            {
                var s = _sections[i];
                if (i > 0) header.Append(',');
                header.Append("{\"name\":\"").Append(JsonEscape(s.Name))
                      .Append("\",\"kind\":\"").Append(s.Kind.Name())
                      .Append("\",\"count\":").Append(s.Count)
                      .Append(",\"offset\":").Append(s.Offset)
                      .Append(",\"size\":").Append(s.Size);
                if (s.Crc32.HasValue)
                    header.Append(",\"crc32\":").Append(s.Crc32.Value);
                header.Append('}');
            }
            header.Append("],\"metadata\":{");
            bool first = true;
            foreach (var kv in _metadata)
            {
                if (!first) header.Append(',');
                first = false;
                header.Append('"').Append(JsonEscape(kv.Key)).Append("\":\"")
                      .Append(JsonEscape(kv.Value)).Append('"');
            }
            header.Append("}}");

            var headerBytes = Encoding.UTF8.GetBytes(header.ToString());
            var payload = _payload.ToArray();
            var outBytes = new byte[Taas.Magic.Length + 8 + headerBytes.Length + payload.Length];
            int pos = 0;
            Buffer.BlockCopy(Taas.Magic, 0, outBytes, pos, Taas.Magic.Length);
            pos += Taas.Magic.Length;
            Buffer.BlockCopy(BitConverter.GetBytes((ulong)headerBytes.Length), 0, outBytes, pos, 8);
            pos += 8;
            Buffer.BlockCopy(headerBytes, 0, outBytes, pos, headerBytes.Length);
            pos += headerBytes.Length;
            Buffer.BlockCopy(payload, 0, outBytes, pos, payload.Length);
            return outBytes;
        }

        public void ToFile(string path) => File.WriteAllBytes(path, ToBytes());

        static string JsonEscape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    public sealed class TaasReader
    {
        readonly byte[] _bytes;
        readonly int _dataOffset;
        readonly Dictionary<string, Section> _byName = new Dictionary<string, Section>();

        public readonly string Version;
        public readonly string Created;
        public readonly string Game;
        public readonly Dictionary<string, string> Metadata = new Dictionary<string, string>();
        public readonly List<Section> Sections = new List<Section>();

        public TaasReader(byte[] bytes)
        {
            _bytes = bytes;
            if (bytes.Length < Taas.Magic.Length + 8)
                throw new FormatException("файл короче заголовка");
            for (int i = 0; i < Taas.Magic.Length; i++)
                if (bytes[i] != Taas.Magic[i])
                    throw new FormatException(
                        $"неверная магия: ожидалось TAAS2, получено " +
                        $"{Encoding.ASCII.GetString(bytes, 0, Math.Min(5, bytes.Length))}");

            long headerLen = BitConverter.ToInt64(bytes, Taas.Magic.Length);
            int headerStart = Taas.Magic.Length + 8;
            if (headerLen < 0 || headerStart + headerLen > bytes.Length)
                throw new FormatException("заголовок обрезан");
            string headerJson = Encoding.UTF8.GetString(bytes, headerStart, (int)headerLen);
            _dataOffset = headerStart + (int)headerLen;

            var root = Json.Obj(Json.Parse(headerJson));
            Version = Json.Str(root["version"]);
            Created = root.TryGetValue("created", out var c) ? Json.Str(c) : null;
            Game = root.TryGetValue("game", out var g) ? Json.Str(g) : null;
            if (root.TryGetValue("metadata", out var m))
                foreach (var kv in Json.Obj(m))
                    Metadata[kv.Key] = Json.Str(kv.Value);

            foreach (var item in Json.Arr(root["sections"]))
            {
                var o = Json.Obj(item);
                var section = new Section
                {
                    Name = Json.Str(o["name"]),
                    Kind = SectionKindExt.Parse(Json.Str(o["kind"])),
                    Count = (int)Json.Num(o["count"]),
                    Offset = (long)Json.Num(o["offset"]),
                    Size = (long)Json.Num(o["size"]),
                    Crc32 = o.TryGetValue("crc32", out var crc) ? (uint)Json.Num(crc) : (uint?)null,
                    Compression = o.TryGetValue("compression", out var comp) ? Json.Str(comp) : null,
                };
                if (_dataOffset + section.Offset + section.Size > bytes.Length)
                    throw new FormatException(
                        $"секция «{section.Name}» выходит за пределы файла");
                Sections.Add(section);
                _byName[section.Name] = section;
            }
        }

        public static TaasReader FromFile(string path) =>
            new TaasReader(File.ReadAllBytes(path));

        public bool Has(string name) => _byName.ContainsKey(name);

        public Section Find(string name) =>
            _byName.TryGetValue(name, out var s) ? s : null;

        /// Сырые байты секции с проверкой CRC. Порча файла обнаруживается
        /// здесь — лучше явная ошибка, чем существо с мусором в весах.
        public byte[] Raw(string name)
        {
            var s = _byName.TryGetValue(name, out var found)
                ? found
                : throw new KeyNotFoundException($"в файле нет секции «{name}»");
            if (!string.IsNullOrEmpty(s.Compression))
                throw new NotSupportedException(
                    $"секция «{name}» сжата ({s.Compression}) — распаковка не поддерживается");

            var data = new byte[s.Size];
            Buffer.BlockCopy(_bytes, (int)(_dataOffset + s.Offset), data, 0, (int)s.Size);
            if (s.Crc32.HasValue)
            {
                uint actual = Data.Crc32.Compute(data);
                if (actual != s.Crc32.Value)
                    throw new InvalidDataException(
                        $"секция «{name}»: CRC32 не сходится " +
                        $"(ожидалось {s.Crc32.Value:x8}, получено {actual:x8})");
            }
            return data;
        }

        public float[] F32(string name)
        {
            var raw = Raw(name);
            var v = new float[raw.Length / 4];
            Buffer.BlockCopy(raw, 0, v, 0, v.Length * 4);
            return v;
        }

        public int[] I32(string name)
        {
            var raw = Raw(name);
            var v = new int[raw.Length / 4];
            Buffer.BlockCopy(raw, 0, v, 0, v.Length * 4);
            return v;
        }

        public long[] I64(string name)
        {
            var raw = Raw(name);
            var v = new long[raw.Length / 8];
            Buffer.BlockCopy(raw, 0, v, 0, v.Length * 8);
            return v;
        }

        public byte[] U8(string name) => Raw(name);

        public string Text(string name) => Encoding.UTF8.GetString(Raw(name));

        /// Короткая сводка файла (диагностика, аналог `inspect_taas`).
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append($"TAAS2 v{Version}");
            if (!string.IsNullOrEmpty(Game)) sb.Append($", игра «{Game}»");
            sb.Append($", секций {Sections.Count}, байт {_bytes.Length}");
            foreach (var s in Sections)
                sb.Append($"\n  {s.Name}: {s.Kind.Name()}×{s.Count} ({s.Size} B)");
            return sb.ToString();
        }
    }
}
