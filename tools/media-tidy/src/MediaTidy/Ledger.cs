using System.Text.Json;

namespace MediaTidy;

/// <summary>Remembers which downloads were filed, so a film deleted from the library is never put back,
/// and which problems were already logged, so scheduled runs don't repeat them.</summary>
internal sealed class Ledger
{
    private readonly Dictionary<string, DateTimeOffset> _filed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _reported = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;

    private Ledger(string path) => _path = path;

    public static Ledger Load(string path)
    {
        var ledger = new Ledger(path);
        if (File.Exists(path))
        {
            var data = JsonSerializer.Deserialize<Data>(File.ReadAllText(path)) ?? new Data();
            foreach (var (entry, at) in data.Filed) ledger._filed[entry] = at;
            foreach (var (entry, reason) in data.Reported) ledger._reported[entry] = reason;
        }

        return ledger;
    }

    public bool IsFiled(string entry) => _filed.ContainsKey(entry);

    public void MarkFiled(string entry, DateTimeOffset at)
    {
        _filed[entry] = at;
        _reported.Remove(entry);
    }

    /// <summary>True the first time a given problem is seen for an entry.</summary>
    public bool ShouldReport(string entry, string reason)
    {
        if (_reported.TryGetValue(entry, out var previous) && previous == reason)
        {
            return false;
        }

        _reported[entry] = reason;
        return true;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var data = new Data { Filed = new(_filed), Reported = new(_reported) };
        File.WriteAllText(_path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class Data
    {
        public Dictionary<string, DateTimeOffset> Filed { get; init; } = [];
        public Dictionary<string, string> Reported { get; init; } = [];
    }
}
