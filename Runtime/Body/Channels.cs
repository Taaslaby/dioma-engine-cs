// Именованные каналы тела: вид сам объявляет, что он чувствует и чем
// действует, а движок работает с плотными массивами по индексам.
//
// Строковые имена разрешаются ОДИН РАЗ — при создании вида. В тике идут
// только индексы: `frame[smellL] = 0.7f`. Тик существа исполняется 20 раз в
// секунду на каждую особь, поиск по словарю там недопустим.
//
//   var smellL = species.Sense.Index("smell.left");   // один раз
//   frame[smellL] = 0.7f;                             // каждый тик

using System;
using System.Collections.Generic;

namespace Dioma.Body
{
    /// Набор имён каналов: имя ↔ индекс. Порядок объявления и есть порядок
    /// индексов, поэтому он воспроизводим и его можно сериализовать.
    public sealed class ChannelSet
    {
        readonly string[] _names;
        readonly Dictionary<string, int> _index;

        public ChannelSet(IReadOnlyList<string> names)
        {
            if (names == null) throw new ArgumentNullException(nameof(names));
            _names = new string[names.Count];
            _index = new Dictionary<string, int>(names.Count, StringComparer.Ordinal);
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                if (string.IsNullOrEmpty(name))
                    throw new ArgumentException($"канал #{i} без имени");
                if (!_index.ContainsKey(name))
                {
                    _index[name] = i;
                }
                else
                {
                    throw new ArgumentException($"дубликат имени канала: '{name}'");
                }
                _names[i] = name;
            }
        }

        public int Count => _names.Length;

        public string NameOf(int index) => _names[index];

        public IReadOnlyList<string> Names => _names;

        /// Индекс канала; −1 если такого нет. Разрешать имена в тике нельзя —
        /// только при создании вида или мира.
        public int IndexOf(string name) =>
            name != null && _index.TryGetValue(name, out int i) ? i : -1;

        /// Индекс канала или внятная ошибка со списком доступных имён.
        public int Index(string name)
        {
            int i = IndexOf(name);
            if (i < 0)
                throw new ArgumentException(
                    $"нет канала '{name}'; есть: {string.Join(", ", _names)}");
            return i;
        }

        public bool Has(string name) => IndexOf(name) >= 0;
    }

    /// Кадр ощущений одного тика: плотный массив по индексам ChannelSet.
    /// Мир заполняет, существо читает. Переиспользуется между тиками —
    /// аллокаций в горячем цикле нет.
    public sealed class SenseFrame
    {
        public readonly ChannelSet Channels;
        public readonly float[] Values;

        /// Мир столкнулся с препятствием в прошлом тике. Общий для всех видов
        /// факт: боль приходит на тик позже, пока eligibility ещё горячая.
        public bool Blocked;

        public SenseFrame(ChannelSet channels)
        {
            Channels = channels ?? throw new ArgumentNullException(nameof(channels));
            Values = new float[channels.Count];
        }

        public float this[int index]
        {
            get => Values[index];
            set => Values[index] = value;
        }

        public void Clear()
        {
            Array.Clear(Values, 0, Values.Length);
            Blocked = false;
        }

        /// Насытить канал единицей: несколько источников не дают тока больше
        /// одного (несколько ягод пахнут как одна).
        public void Saturate(int index) =>
            Values[index] = MathF.Min(Values[index], 1f);

        public void SaturateAll()
        {
            for (int i = 0; i < Values.Length; i++)
                Values[i] = MathF.Min(Values[i], 1f);
        }
    }

    /// Кадр действия одного тика: что существо делает миром.
    /// Непрерывные каналы (руль, тяга) и дискретные (кусать, хватать).
    public sealed class ActionFrame
    {
        public readonly ChannelSet Channels;
        public readonly float[] Values;

        /// Существо спит: мир не должен его двигать/кормить.
        public bool Asleep;

        public ActionFrame(ChannelSet channels)
        {
            Channels = channels ?? throw new ArgumentNullException(nameof(channels));
            Values = new float[channels.Count];
        }

        public float this[int index]
        {
            get => Values[index];
            set => Values[index] = value;
        }

        /// Дискретное действие: канал взведён (> 0.5). Порог, а не «не ноль»:
        /// непрерывный канал в тонусе не должен читаться как приказ.
        public bool IsSet(int index) => Values[index] > 0.5f;

        public void Clear()
        {
            Array.Clear(Values, 0, Values.Length);
            Asleep = false;
        }
    }
}
