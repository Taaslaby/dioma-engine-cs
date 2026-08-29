// Минимальный JSON-парсер для ассетов диомы (коннектом + золотой след).
// Без внешних зависимостей: JsonUtility не умеет вложенные массивы чисел,
// а Newtonsoft в проект не тянем. Числа — double, потом в float.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Dioma.Spikes
{
    public static class Json
    {
        public static object Parse(string text)
        {
            var pos = 0;
            var value = ParseValue(text, ref pos);
            SkipWs(text, ref pos);
            if (pos != text.Length)
                throw new FormatException($"JSON: мусор после значения, offset {pos}");
            return value;
        }

        public static Dictionary<string, object> Obj(object o) =>
            (Dictionary<string, object>)o;

        public static List<object> Arr(object o) => (List<object>)o;

        public static double Num(object o) => (double)o;

        public static float F(object o) => (float)(double)o;

        public static string Str(object o) => (string)o;

        public static bool Bool(object o) => (bool)o;

        static void SkipWs(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\n' || s[pos] == '\r'))
                pos++;
        }

        static object ParseValue(string s, ref int pos)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length)
                throw new FormatException("JSON: неожиданный конец");
            switch (s[pos])
            {
                case '{': return ParseObj(s, ref pos);
                case '[': return ParseArr(s, ref pos);
                case '"': return ParseString(s, ref pos);
                case 't': Expect(s, ref pos, "true"); return true;
                case 'f': Expect(s, ref pos, "false"); return false;
                case 'n': Expect(s, ref pos, "null"); return null;
                default: return ParseNumber(s, ref pos);
            }
        }

        static void Expect(string s, ref int pos, string lit)
        {
            if (pos + lit.Length > s.Length || string.CompareOrdinal(s, pos, lit, 0, lit.Length) != 0)
                throw new FormatException($"JSON: ожидалось «{lit}», offset {pos}");
            pos += lit.Length;
        }

        static object ParseObj(string s, ref int pos)
        {
            var dict = new Dictionary<string, object>();
            pos++; // {
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return dict; }
            while (true)
            {
                SkipWs(s, ref pos);
                var key = ParseString(s, ref pos);
                SkipWs(s, ref pos);
                if (s[pos] != ':') throw new FormatException($"JSON: ожидалось «:», offset {pos}");
                pos++;
                dict[key] = ParseValue(s, ref pos);
                SkipWs(s, ref pos);
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; return dict; }
                throw new FormatException($"JSON: ожидалось «,» или «}}», offset {pos}");
            }
        }

        static object ParseArr(string s, ref int pos)
        {
            var list = new List<object>();
            pos++; // [
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return list; }
            while (true)
            {
                list.Add(ParseValue(s, ref pos));
                SkipWs(s, ref pos);
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; return list; }
                throw new FormatException($"JSON: ожидалось «,» или «]», offset {pos}");
            }
        }

        static string ParseString(string s, ref int pos)
        {
            pos++; // "
            var sb = new StringBuilder();
            while (s[pos] != '"')
            {
                if (s[pos] == '\\')
                {
                    pos++;
                    switch (s[pos])
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'u':
                            sb.Append((char)int.Parse(s.Substring(pos + 1, 4), NumberStyles.HexNumber));
                            pos += 4;
                            break;
                        default: sb.Append(s[pos]); break;
                    }
                    pos++;
                }
                else
                {
                    sb.Append(s[pos]);
                    pos++;
                }
            }
            pos++; // "
            return sb.ToString();
        }

        static object ParseNumber(string s, ref int pos)
        {
            var start = pos;
            while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '-' || s[pos] == '+' ||
                                      s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E'))
                pos++;
            return double.Parse(s.Substring(start, pos - start), CultureInfo.InvariantCulture);
        }
    }
}
