using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wordbook;

public class Config
{
    public string Root { get; private set; } = "";
    public string DataFile { get; private set; } = "";
    public int Port { get; private set; } = 17812;
    public string DeepSeekKey { get; private set; } = "";
    public string DeepSeekBase { get; private set; } = "https://api.deepseek.com";
    public string DeepSeekModel { get; private set; } = "deepseek-chat";
    public bool AiEnabled => !string.IsNullOrWhiteSpace(DeepSeekKey);
    public string HomeUrl => $"http://127.0.0.1:{Port}";

    public static Config Load()
    {
        var root = FindRoot();
        var cfg = new Config { Root = root, DataFile = Path.Combine(root, "data", "words.json") };
        cfg.Reload();
        return cfg;
    }

    public void Reload()
    {
        var envFile = Path.Combine(Root, ".env.local");
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(envFile))
        {
            foreach (var raw in File.ReadAllLines(envFile))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                vars[line[..i].Trim()] = line[(i + 1)..].Trim();
            }
        }
        string Pick(string name, string def)
        {
            var e = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(e)) return e.Trim();
            return vars.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : def;
        }
        if (int.TryParse(Pick("WORDBOOK_PORT", "17812"), out var port) && port is >= 1024 and <= 65535)
            Port = port;
        DeepSeekKey = Pick("DEEPSEEK_API_KEY", "");
        DeepSeekBase = Pick("DEEPSEEK_BASE_URL", "https://api.deepseek.com").TrimEnd('/');
        DeepSeekModel = Pick("DEEPSEEK_MODEL", "deepseek-chat");
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "index.html"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}

public class DataStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _file;

    public DataStore(Config cfg) : this(cfg.DataFile) { }

    public DataStore(string file)
    {
        _file = file;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (!File.Exists(_file)) SaveSync(Empty());
    }

    private static JsonObject Empty()
    {
        return new JsonObject
        {
            ["version"] = 2,
            ["meta"] = new JsonObject { ["updatedAt"] = DateTimeOffset.Now.ToUnixTimeMilliseconds() },
            ["words"] = new JsonArray(),
        };
    }

    private JsonObject LoadUnsafe()
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(_file));
            if (node is JsonObject o && o["words"] is JsonArray) return o;
        }
        catch
        {
            var bad = _file + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Copy(_file, bad, true); } catch { /* 忽略 */ }
        }
        var fresh = Empty();
        SaveSync(fresh);
        return fresh;
    }

    private void SaveUnsafe(JsonObject data)
    {
        data["meta"] = new JsonObject { ["updatedAt"] = DateTimeOffset.Now.ToUnixTimeMilliseconds() };
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _file, true);
    }

    private void SaveSync(JsonObject data)
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _file, true);
    }

    public async Task<JsonObject> GetSnapshotAsync()
    {
        await _gate.WaitAsync();
        try { return (JsonObject)LoadUnsafe().DeepClone(); }
        finally { _gate.Release(); }
    }

    public async Task<JsonArray> GetWordsAsync()
    {
        await _gate.WaitAsync();
        try { return (JsonArray)LoadUnsafe()["words"]!.DeepClone(); }
        finally { _gate.Release(); }
    }

    public async Task<List<JsonObject>> CreateWordsAsync(List<(string Word, string Sentence)> items)
    {
        await _gate.WaitAsync();
        try
        {
            var data = LoadUnsafe();
            var arr = (JsonArray)data["words"]!;
            var created = new List<JsonObject>();
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            foreach (var (word, sentence) in items)
            {
                var w = NewWord(word, sentence, now);
                arr.Add(w);
                created.Add((JsonObject)w.DeepClone());
            }
            SaveUnsafe(data);
            return created;
        }
        finally { _gate.Release(); }
    }

    private static JsonObject NewWord(string word, string sentence, long now)
    {
        return new JsonObject
        {
            ["id"] = "w-" + Guid.NewGuid().ToString("N")[..12],
            ["word"] = word.Trim(),
            ["phonetic"] = "",
            ["translation"] = "",
            ["definitions"] = new JsonArray(),
            ["audio"] = "",
            ["sentence"] = sentence.Trim(),
            ["note"] = "",
            ["ai"] = new JsonObject { ["status"] = "pending", ["inContext"] = "", ["why"] = "", ["rephrase"] = "", ["tip"] = "", ["error"] = "" },
            ["createdAt"] = now,
            ["updatedAt"] = now,
        };
    }

    public async Task<JsonObject> PatchWordAsync(string id, JsonObject patch)
    {
        await _gate.WaitAsync();
        try
        {
            var data = LoadUnsafe();
            var arr = (JsonArray)data["words"]!;
            var w = arr.FirstOrDefault(x => x is JsonObject o && string.Equals(o["id"]?.GetValue<string>(), id, StringComparison.Ordinal));
            if (w is not JsonObject word) return null;

            CopyScalar(patch, word, "word");
            CopyScalar(patch, word, "phonetic");
            CopyScalar(patch, word, "translation");
            CopyScalar(patch, word, "sentence");
            CopyScalar(patch, word, "note");
            CopyScalar(patch, word, "audio");
            if (patch["definitions"] is JsonArray defs) word["definitions"] = (JsonArray)defs.DeepClone();
            if (patch["ai"] is JsonObject ai)
            {
                var target = word["ai"] as JsonObject ?? new JsonObject();
                foreach (var k in new[] { "status", "inContext", "why", "rephrase", "tip", "error" })
                    if (ai[k] != null) target[k] = ai[k]?.DeepClone();
                word["ai"] = target;
            }
            word["updatedAt"] = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            SaveUnsafe(data);
            return (JsonObject)word.DeepClone();
        }
        finally { _gate.Release(); }
    }

    private static void CopyScalar(JsonObject from, JsonObject to, string key)
    {
        if (from[key] != null && from[key] is not JsonArray && from[key] is not JsonObject)
            to[key] = from[key]?.DeepClone();
    }

    public async Task<bool> DeleteWordAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            var data = LoadUnsafe();
            var arr = (JsonArray)data["words"]!;
            var idx = -1;
            for (var i = 0; i < arr.Count; i++)
            {
                if (arr[i] is JsonObject o && string.Equals(o["id"]?.GetValue<string>(), id, StringComparison.Ordinal)) { idx = i; break; }
            }
            if (idx < 0) return false;
            arr.RemoveAt(idx);
            SaveUnsafe(data);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<JsonObject> ImportAsync(string mode, JsonArray input)
    {
        await _gate.WaitAsync();
        try
        {
            var data = LoadUnsafe();
            var arr = (JsonArray)data["words"]!;
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            int added = 0, skipped = 0;
            if (mode == "replace")
            {
                arr.Clear();
                added = 0; skipped = 0;
            }
            foreach (var item in input)
            {
                if (item is not JsonObject o) continue;
                var word = o["word"]?.GetValue<string>()?.Trim() ?? "";
                if (word.Length == 0) continue;
                if (mode == "replace")
                {
                    arr.Add(NormalizeImported(o, word, now));
                    added++;
                    continue;
                }
                var dup = arr.Any(x => x is JsonObject z &&
                    string.Equals(z["word"]?.GetValue<string>(), word, StringComparison.OrdinalIgnoreCase));
                if (dup) { skipped++; continue; }
                arr.Add(NormalizeImported(o, word, now));
                added++;
            }
            SaveUnsafe(data);
            var result = new JsonObject
            {
                ["ok"] = true,
                ["added"] = added,
                ["skipped"] = skipped,
                ["total"] = arr.Count,
                ["words"] = (JsonArray)arr.DeepClone(),
            };
            return result;
        }
        finally { _gate.Release(); }
    }

    private static JsonObject NormalizeImported(JsonObject o, string word, long now)
    {
        var w = NewWord(word, o["sentence"]?.GetValue<string>() ?? "", now);
        w["id"] = o["id"]?.GetValue<string>() ?? w["id"]?.GetValue<string>();
        if (o["phonetic"] != null) w["phonetic"] = o["phonetic"]!.DeepClone();
        if (o["translation"] != null) w["translation"] = o["translation"]!.DeepClone();
        if (o["definitions"] is JsonArray defs) w["definitions"] = (JsonArray)defs.DeepClone();
        if (o["note"] != null) w["note"] = o["note"]!.DeepClone();
        if (o["audio"] != null) w["audio"] = o["audio"]!.DeepClone();
        if (o["ai"] is JsonObject ai) w["ai"] = (JsonObject)ai.DeepClone();
        if (o["createdAt"] != null) w["createdAt"] = o["createdAt"]!.DeepClone();
        w["updatedAt"] = now;
        return w;
    }

    public async Task MutateWordAsync(string id, Action<JsonObject> mutate)
    {
        await _gate.WaitAsync();
        try
        {
            var data = LoadUnsafe();
            var arr = (JsonArray)data["words"]!;
            var w = arr.FirstOrDefault(x => x is JsonObject o && string.Equals(o["id"]?.GetValue<string>(), id, StringComparison.Ordinal));
            if (w is JsonObject word)
            {
                mutate(word);
                word["updatedAt"] = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                SaveUnsafe(data);
            }
        }
        finally { _gate.Release(); }
    }
}
