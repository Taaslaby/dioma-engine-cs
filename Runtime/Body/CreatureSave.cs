// Сохранение существа в `.taas`: то, что должно переживать сессию.
//
// Что пишем и почему именно это:
//   - `brain.weights` — выученное за жизнь (главное);
//   - `brain.birth` — снимок Болдуина: без него после загрузки существо
//     «забудет», что нажило, и потомкам передавать будет нечего;
//   - `blood.conc` — голод, глюкоза, гормоны: тело продолжает с того же
//     состояния, а не сытым и бодрым;
//   - `genome` (JSON) — метаболизм, инстинкты, счётчики жизни.
//
// Что НЕ пишем: мембранные потенциалы и синаптические токи. Это состояние
// сотни миллисекунд — оно восстанавливается за первые тики само, а хранить
// его значило бы привязать файл к версии модели нейрона.
//
// Топология не пишется: она полностью определяется коннектомом, а его файл
// (или сид роста) остаётся ассетом. В сохранении лежит ссылка — имя
// коннектома и число синапсов, по которому загрузка проверяет совпадение.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dioma.Bio;
using Dioma.Data;
using Dioma.Spikes;

namespace Dioma.Body
{
    public static class CreatureSave
    {
        public const string SectionWeights = "brain.weights";
        public const string SectionBirth = "brain.birth";
        public const string SectionBlood = "blood.conc";
        public const string SectionGenome = "genome";

        /// Сериализовать существо. `connectome` — имя ассета коннектома,
        /// по которому его найдут при загрузке.
        public static byte[] Save(Creature creature, string connectome)
        {
            var w = new TaasWriter();
            w.Game("dioma");
            w.Metadata("connectome", connectome);
            w.Metadata("synapses", creature.Brain.SynapseCount.ToString(CultureInfo.InvariantCulture));
            w.Metadata("neurons", creature.Brain.N.ToString(CultureInfo.InvariantCulture));
            w.Metadata("foodEaten", creature.FoodEaten.ToString(CultureInfo.InvariantCulture));
            w.Metadata("nights", creature.Nights.ToString(CultureInfo.InvariantCulture));

            w.F32(SectionWeights, creature.Brain.CsrValues());
            w.F32(SectionBirth, creature.Brain.BirthWeights());
            w.F32(SectionBlood, creature.Blood.Conc);
            w.JsonRaw(SectionGenome, GenomeJson(creature));
            return w.ToBytes();
        }

        /// Восстановить существо на уже загруженном коннектоме. Мозг обязан
        /// быть того же размера — иначе веса лягут не на те синапсы, и
        /// существо станет чужим себе.
        public static Creature Load(byte[] bytes, DiomaBrain brain)
        {
            var r = new TaasReader(bytes);
            if (r.Metadata.TryGetValue("synapses", out var synapsesStr) &&
                int.TryParse(synapsesStr, NumberStyles.Integer, CultureInfo.InvariantCulture,
                             out int synapses) &&
                synapses != brain.SynapseCount)
                throw new InvalidOperationException(
                    $"сохранение под {synapses} синапсов, а у мозга {brain.SynapseCount} — " +
                    "нужен тот же коннектом");

            var genome = ParseGenome(r.Text(SectionGenome));
            // Веса рождения кладём как наследство: конструктор существа
            // применит их и снимет с них снимок Болдуина.
            genome.Weights = r.F32(SectionBirth);
            var blood = Blood.Creature();
            var conc = r.F32(SectionBlood);
            for (int i = 0; i < Chem.N && i < conc.Length; i++)
                blood.Conc[i] = conc[i];

            var creature = new Creature(brain, genome, blood);
            // Поверх снимка — выученное на момент сохранения. Порядок важен:
            // сначала рождение (иначе снимок затрёт прогресс), потом жизнь.
            brain.SetCsrValues(r.F32(SectionWeights));
            return creature;
        }

        public static string Describe(byte[] bytes) => new TaasReader(bytes).Describe();

        // ── Геном ⇄ JSON (у движка есть парсер, но нет сериализатора) ──

        static string GenomeJson(Creature creature)
        {
            var g = creature.Genome;
            var sb = new StringBuilder();
            sb.Append("{\"metabolism\":{");
            sb.Append("\"costPerActivity\":").Append(F(g.Metabolism.CostPerActivity));
            sb.Append(",\"starveDamage\":").Append(F(g.Metabolism.StarveDamage));
            sb.Append(",\"costPerDopamine\":").Append(F(g.Metabolism.CostPerDopamine));
            sb.Append(",\"healRate\":").Append(F(g.Metabolism.HealRate));
            sb.Append("},\"instincts\":[");
            for (int i = 0; i < g.Instincts.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var inst = g.Instincts[i];
                sb.Append("{\"dopamine\":").Append(F(inst.Dopamine)).Append(",\"inputs\":[");
                for (int k = 0; k < inst.Inputs.Count; k++)
                {
                    if (k > 0) sb.Append(',');
                    sb.Append('[').Append(inst.Inputs[k].Neuron).Append(',')
                      .Append(F(inst.Inputs[k].Drive)).Append(']');
                }
                sb.Append("]}");
            }
            sb.Append("],\"life\":{\"foodEaten\":").Append(creature.FoodEaten)
              .Append(",\"wallHits\":").Append(creature.WallHits)
              .Append(",\"nights\":").Append(creature.Nights).Append("}}");
            return sb.ToString();
        }

        static Genome ParseGenome(string json)
        {
            var root = Json.Obj(Json.Parse(json));
            var genome = Genome.Newborn();

            var m = Json.Obj(root["metabolism"]);
            genome.Metabolism = new MetabolismGene
            {
                CostPerActivity = Json.F(m["costPerActivity"]),
                StarveDamage = Json.F(m["starveDamage"]),
                CostPerDopamine = Json.F(m["costPerDopamine"]),
                HealRate = Json.F(m["healRate"]),
            };

            foreach (var item in Json.Arr(root["instincts"]))
            {
                var o = Json.Obj(item);
                var inputs = new List<(int, float)>();
                foreach (var pair in Json.Arr(o["inputs"]))
                {
                    var p = Json.Arr(pair);
                    inputs.Add(((int)Json.Num(p[0]), Json.F(p[1])));
                }
                genome.Instincts.Add(new Instinct(inputs, Json.F(o["dopamine"])));
            }
            return genome;
        }

        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
