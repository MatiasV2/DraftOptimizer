using System.Collections.ObjectModel;

namespace DraftOptimizer.Core;

public static class IncrementalCollection
{
    public static void Synchronize<T>(ObservableCollection<T> current, IReadOnlyList<T> desired) where T : class
    {
        var keep = desired.ToHashSet(ReferenceEqualityComparer.Instance);
        for (int i = current.Count - 1; i >= 0; i--) if (!keep.Contains(current[i])) current.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < current.Count && ReferenceEquals(current[i], desired[i])) continue;
            int existing = -1;
            for (int j = i + 1; j < current.Count; j++) if (ReferenceEquals(current[j], desired[i])) { existing = j; break; }
            if (existing >= 0) current.Move(existing, i); else current.Insert(i, desired[i]);
        }
    }
}
