// Вид из JSON: тот же `SpeciesSpec`, что собирает builder, только описанный
// файлом. Добавить вид = положить файл; правок движка не требуется.
//
// Правила валидации НЕ дублируются: загрузчик разбирает JSON в builder, а
// проверяет `SpeciesSpec.Validate()` — один источник истины на оба входа.
//
// {
//   "name": "fish",
//   "sense":  [ { "name": "smell.left", "region": "SL", "current": 4, "required": true } ],
//   "motors": [ { "name": "tail", "region": "TAIL" } ],
//   "actuators": [
//     { "name": "steer", "kind": "difference", "pools": ["left","right"], "scale": 0.25, "gain": "turn" },
//     { "name": "drive", "kind": "sum", "pools": ["tail"], "scale": 0.0016667 },
//     { "name": "eat",   "kind": "ingest", "senseChannel": "food.near" }
//   ],
//   "rewards": [
//     { "name": "eat", "trigger": "ate", "dopamine": 1, "costsGlucose": true, "delayed": true }
//   ],
//   "reflexes": [ ["SL","ML"] ]
// }

using System;
using System.Collections.Generic;
using Dioma.Data;

namespace Dioma.Body
{
    public static class SpeciesJson
    {
        /// Разобрать вид из JSON-текста.
        public static SpeciesSpec Parse(string text)
        {
            var root = Json.Obj(Json.Parse(text));
            string name = root.TryGetValue("name", out var n) ? Json.Str(n) : null;
            var b = SpeciesBuilder.Create(name);

            if (root.TryGetValue("sense", out var senseObj))
                foreach (var item in Json.Arr(senseObj))
                {
                    var o = Json.Obj(item);
                    // Регион может отсутствовать: такой канал в мозг не
                    // подаётся, его читает только тело (например "food.near").
                    string region = o.TryGetValue("region", out var r) && r != null
                        ? Json.Str(r) : null;
                    b.Sense(Req(o, "name"), region,
                        o.TryGetValue("current", out var c) ? Json.F(c) : 0f,
                        o.TryGetValue("required", out var q) && Json.Bool(q));
                }

            if (root.TryGetValue("motors", out var motorsObj))
                foreach (var item in Json.Arr(motorsObj))
                {
                    var o = Json.Obj(item);
                    b.Motor(Req(o, "name"), Req(o, "region"),
                        !o.TryGetValue("required", out var q) || Json.Bool(q));
                }

            if (root.TryGetValue("actuators", out var actObj))
                foreach (var item in Json.Arr(actObj))
                    b.Actuate(ParseActuator(Json.Obj(item)));

            if (root.TryGetValue("rewards", out var rewObj))
                foreach (var item in Json.Arr(rewObj))
                    b.Reward(ParseReward(Json.Obj(item)));

            if (root.TryGetValue("reflexes", out var reflexObj))
                foreach (var item in Json.Arr(reflexObj))
                {
                    var pair = Json.Arr(item);
                    if (pair.Count != 2)
                        throw new FormatException(
                            $"вид '{name}': рефлекс должен быть парой [пре, пост], а не {pair.Count} элементами");
                    b.Reflex(Json.Str(pair[0]), Json.Str(pair[1]));
                }

            return b.Build();   // вся валидация внутри SpeciesSpec
        }

        static Actuator ParseActuator(Dictionary<string, object> o)
        {
            string name = Req(o, "name");
            string kind = Req(o, "kind").ToLowerInvariant();
            var pools = Strings(o, "pools");
            float scale = o.TryGetValue("scale", out var s) ? Json.F(s) : 1f;
            var gain = ParseGain(o.TryGetValue("gain", out var g) ? Json.Str(g) : null, kind);

            switch (kind)
            {
                case "difference":
                    if (pools.Length != 2)
                        throw new FormatException(
                            $"актуатор '{name}': difference требует ровно два пула");
                    return Actuator.Difference(name, pools[0], pools[1], scale, gain);
                case "sum":
                    return Actuator.Sum(name, pools, scale, gain);
                case "frombody":
                case "body":
                    return Actuator.FromBody(name, gain, scale);
                case "threshold":
                    if (pools.Length != 1)
                        throw new FormatException(
                            $"актуатор '{name}': threshold требует один пул");
                    return Actuator.Threshold(name, pools[0],
                        o.TryGetValue("level", out var l) ? Json.F(l) : 1f);
                case "ingest":
                    return Actuator.Ingest(name, Req(o, "senseChannel"));
                default:
                    throw new FormatException(
                        $"актуатор '{name}': неизвестный тип '{kind}'; есть " +
                        "difference, sum, fromBody, threshold, ingest");
            }
        }

        /// Гейн по умолчанию зависит от типа: руль модулируется маневренностью,
        /// тяга — скоростью. Явное значение в JSON перекрывает.
        static BodyGain ParseGain(string text, string kind)
        {
            if (text == null)
                return kind == "difference" ? BodyGain.Turn
                     : kind == "sum" ? BodyGain.Speed
                     : BodyGain.None;
            switch (text.ToLowerInvariant())
            {
                case "none": return BodyGain.None;
                case "turn": return BodyGain.Turn;
                case "speed": return BodyGain.Speed;
                case "motortonic": case "tonic": return BodyGain.MotorTonic;
                default:
                    throw new FormatException(
                        $"неизвестный гейн '{text}'; есть none, turn, speed, motorTonic");
            }
        }

        static RewardRule ParseReward(Dictionary<string, object> o)
        {
            string name = Req(o, "name");
            string trig = Req(o, "trigger").ToLowerInvariant();
            RewardTrigger trigger;
            switch (trig)
            {
                case "ate": trigger = RewardTrigger.Ate; break;
                case "blocked": trigger = RewardTrigger.Blocked; break;
                case "channeldropped": case "dropped": trigger = RewardTrigger.ChannelDropped; break;
                case "channelrose": case "rose": trigger = RewardTrigger.ChannelRose; break;
                case "channelabove": case "above": trigger = RewardTrigger.ChannelAbove; break;
                default:
                    throw new FormatException(
                        $"правило '{name}': неизвестный триггер '{trig}'; есть " +
                        "ate, blocked, channelDropped, channelRose, channelAbove");
            }

            // "channel" и "channels" — одно и то же поле: один канал не должен
            // требовать массива, а несколько агрегируются максимумом.
            var channels = Strings(o, "channels");
            if (channels.Length == 0 && o.TryGetValue("channel", out var single) && single != null)
                channels = new[] { Json.Str(single) };

            return new RewardRule(name, trigger,
                o.TryGetValue("dopamine", out var d) ? Json.F(d) : 0f,
                channels,
                o.TryGetValue("threshold", out var t) ? Json.F(t) : 0f,
                o.TryGetValue("proportional", out var p) && Json.Bool(p),
                o.TryGetValue("costsGlucose", out var c) && Json.Bool(c),
                o.TryGetValue("delayed", out var l) && Json.Bool(l));
        }

        static string Req(Dictionary<string, object> o, string key)
        {
            if (!o.TryGetValue(key, out var v) || v == null)
                throw new FormatException($"нет обязательного поля '{key}'");
            return Json.Str(v);
        }

        static string[] Strings(Dictionary<string, object> o, string key)
        {
            if (!o.TryGetValue(key, out var v) || v == null)
                return Array.Empty<string>();
            var arr = Json.Arr(v);
            var result = new string[arr.Count];
            for (int i = 0; i < arr.Count; i++) result[i] = Json.Str(arr[i]);
            return result;
        }
    }
}
