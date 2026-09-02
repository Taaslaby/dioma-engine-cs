using System.Collections;
using System.Collections.Generic;

namespace Dioma.Spikes
{
    /// Read-only int-представление компактной byte-истории. Создаётся один
    /// раз вместе с мозгом и сохраняет публичный контракт SpikeHistory.
    sealed class SpikeHistoryView : IReadOnlyList<int>
    {
        readonly byte[] _history;

        public SpikeHistoryView(byte[] history) => _history = history;

        public int Count => _history.Length;
        public int this[int index] => _history[index];

        public IEnumerator<int> GetEnumerator()
        {
            for (int i = 0; i < _history.Length; i++)
                yield return _history[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
