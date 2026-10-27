namespace Sts2LanCoop.Core;

public static class ItemPreview
{
    // Stable IDs (including the summary's upgrade suffix) define identity; localized labels do not.
    public static string Format(IEnumerable<string> items, IReadOnlyDictionary<string, string> titles, int limit = 6)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        var groups = items.GroupBy(id => id, StringComparer.Ordinal).ToArray();
        if (groups.Length == 0) return "无";
        var shown = groups.Take(limit).Select(group => titles.GetValueOrDefault(group.Key, group.Key) +
            (group.Count() > 1 ? $"({group.Count()})" : ""));
        return string.Join("、", shown) + (groups.Length > limit ? "等……" : "");
    }
}
