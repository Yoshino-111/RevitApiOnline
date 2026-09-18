#if NET48
namespace FamilyMEP.Plugin.Compatibility;

internal static class LegacyCollections
{
    public static bool StartsWith(this string source, char value) => source.Length > 0 && source[0] == value;
    public static string Replace(this string source, string oldValue, string newValue, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(oldValue)) throw new ArgumentException("Replacement text cannot be empty.", nameof(oldValue));
        var result = new System.Text.StringBuilder();
        int start=0, found;
        while ((found=source.IndexOf(oldValue,start,comparison)) >= 0)
        {
            result.Append(source,start,found-start).Append(newValue);
            start=found+oldValue.Length;
        }
        return result.Append(source,start,source.Length-start).ToString();
    }
    public static IEnumerable<T> DistinctBy<T, K>(this IEnumerable<T> source, Func<T, K> key)
    {
        var seen = new HashSet<K>();
        foreach (T item in source) if (seen.Add(key(item))) yield return item;
    }
    public static IEnumerable<T> TakeLast<T>(this IEnumerable<T> source, int count)
    {
        if (count <= 0) yield break;
        var queue = new Queue<T>();
        foreach (T item in source) { queue.Enqueue(item); if (queue.Count > count) queue.Dequeue(); }
        foreach (T item in queue) yield return item;
    }
    public static V GetValueOrDefault<K,V>(this IReadOnlyDictionary<K,V> source, K key, V fallback = default!) => source.TryGetValue(key, out V value) ? value : fallback;
    public static bool TryAdd<K,V>(this IDictionary<K,V> source, K key, V value)
    {
        if (source.ContainsKey(key)) return false;
        source.Add(key, value); return true;
    }
    public static bool Remove<K,V>(this IDictionary<K,V> source, K key, out V value)
    {
        if (!source.TryGetValue(key, out value)) return false;
        return source.Remove(key);
    }
    public static void Deconstruct<K,V>(this KeyValuePair<K,V> pair, out K key, out V value) { key=pair.Key; value=pair.Value; }
}
#endif
