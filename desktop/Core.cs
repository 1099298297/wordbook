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
        var dataOverride = Environment.GetEnvironmentVariable("WORDBOOK_DATA_DIR");
        var dataDir = string.IsNullOrWhiteSpace(dataOverride)
            ? Path.Combine(root, "data")
            : dataOverride.Trim();
        var cfg = new Config { Root = root, DataFile = Path.Combine(dataDir, "words.json") };
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

public class SceneRef
{
    public string WordId { get; set; } = "";
    public string SceneId { get; set; } = "";
    public string Word { get; set; } = "";
    public string Text { get; set; } = "";
}

public class CreateOutcome
{
    public List<JsonObject> Words { get; } = new();
    public HashSet<string> NewIds { get; } = new(StringComparer.Ordinal);
    public List<SceneRef> ScenesToExplain { get; } = new();
    public int Added { get; set; }
    public int Hit { get; set; }
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
            ["version"] = 3,
            ["meta"] = new JsonObject { ["updatedAt"] = DateTimeOffset.Now.ToUnixTimeMilliseconds() },
            ["words"] = new JsonArray(),
        };
    }

    private static string NewId(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..12];

    private static JsonObject NewScene(string text, long addedAt)
    {
        return new JsonObject
        {
            ["id"] = NewId("s"),
            ["text"] = (text ?? "").Trim(),
            ["addedAt"] = addedAt,
            ["updatedAt"] = addedAt,
            ["ai"] = NewAi("pending"),
        };
    }

    private static JsonObject NewAi(string status, string error = "")
    {
        return new JsonObject
        {
            ["status"] = status,
            ["inContext"] = "",
            ["why"] = "",
            ["rephrase"] = "",
            ["tip"] = "",
            ["error"] = error,
        };
    }

    private static void NormalizeWord(JsonObject w, long now)
    {
        if (string.IsNullOrWhiteSpace(w["id"]?.GetValue<string>()))
            w["id"] = NewId("w");
        var createdAt = w["createdAt"] is JsonValue cv && cv.TryGetValue<long>(out var ca) ? ca : now;

        if (w["sentences"] is not JsonArray scenes)
        {
            scenes = new JsonArray();
            w["sentences"] = scenes;
            var legacy = w["sentence"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrWhiteSpace(legacy))
                scenes.Add(NewScene(legacy, createdAt));
        }

        // 旧版数据迁移：字符串句子 -> 场景对象；缺字段的场景补齐
        for (var i = 0; i < scenes.Count; i++)
        {
            var item = scenes[i];
            if (item is JsonValue sv && sv.TryGetValue<string>(out var text))
            {
                scenes[i] = NewScene(text, Math.Min(now, createdAt + (i + 1L) * 1000));
            }
            else if (item is JsonObject scene)
            {
                if (string.IsNullOrWhiteSpace(scene["id"]?.GetValue<string>()))
                    scene["id"] = NewId("s");
                if (scene["text"] == null) scene["text"] = "";
                if (scene["addedAt"] == null) scene["addedAt"] = createdAt + (i + 1L) * 1000;
                if (scene["updatedAt"] == null) scene["updatedAt"] = scene["addedAt"]!.DeepClone();
                if (scene["ai"] is not JsonObject ai)
                    scene["ai"] = NewAi("none", "旧数据未讲解");
            }
        }

        // 老版本整词级 AI 讲解迁移给对应句子
        var wordAi = w["ai"] as JsonObject;
        if (wordAi != null && (wordAi["status"]?.GetValue<string>() ?? "") == "done")
        {
            var target = scenes.FirstOrDefault(s => s is JsonObject so &&
                string.Equals(so["text"]?.GetValue<string>(), w["sentence"]?.GetValue<string>(), StringComparison.Ordinal));
            if (target is JsonObject t && t["ai"] is JsonObject ta &&
                (ta["status"]?.GetValue<string>() ?? "") != "done")
                t["ai"] = (JsonObject)wordAi.DeepClone();
        }
        w.Remove("ai");

        if (scenes.Count == 0)
            scenes.Add(NewScene("", createdAt));
        RefreshLatestSentence(w);
    }

    private static void RefreshLatestSentence(JsonObject w)
    {
        if (w["sentences"] is not JsonArray scenes) return;
        JsonObject latest = null;
        long latestAt = long.MinValue;
        foreach (var s in scenes)
        {
            if (s is not JsonObject so) continue;
            var at = so["addedAt"] is JsonValue v && v.TryGetValue<long>(out var t) ? t : 0;
            if (at >= latestAt)
            {
                latestAt = at;
                latest = so;
            }
        }
        w["sentence"] = latest?["text"]?.GetValue<string>() ?? "";
    }

    private static void MergeDuplicates(JsonObject data)
    {
        var arr = (JsonArray)data["words"]!;
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is JsonObject w) NormalizeWord(w, now);
        }

        var seen = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonObject w) continue;
            var key = w["word"]?.GetValue<string>() ?? "";
            if (key.Length == 0) continue;
            if (!seen.TryGetValue(key, out var keep))
            {
                seen[key] = w;
                continue;
            }

            var scenes = (JsonArray)keep["sentences"]!;
            if (w["sentences"] is JsonArray more)
            {
                foreach (var s in more)
                {
                    if (s is not JsonObject so) continue;
                    var text = so["text"]?.GetValue<string>() ?? "";
                    var exists = scenes.Any(x => x is JsonObject k &&
                        string.Equals(k["text"]?.GetValue<string>(), text, StringComparison.OrdinalIgnoreCase));
                    if (!exists) scenes.Add((JsonObject)so.DeepClone());
                }
            }
            if (string.IsNullOrWhiteSpace(keep["translation"]?.GetValue<string>())
                && !string.IsNullOrWhiteSpace(w["translation"]?.GetValue<string>()))
                keep["translation"] = w["translation"]!.GetValue<string>();
            if ((keep["definitions"] as JsonArray)?.Count == 0 && w["definitions"] is JsonArray defs)
                keep["definitions"] = (JsonArray)defs.DeepClone();
            if (string.IsNullOrWhiteSpace(keep["phonetic"]?.GetValue<string>())
                && !string.IsNullOrWhiteSpace(w["phonetic"]?.GetValue<string>()))
                keep["phonetic"] = w["phonetic"]!.GetValue<string>();
            RefreshLatestSentence(keep);
            keep["updatedAt"] = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            arr.RemoveAt(i);
            i--;
        }
    }

    private JsonObject LoadUnsafe()
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(_file));
            if (node is JsonObject o && o["words"] is JsonArray)
            {
                MergeDuplicates(o);
                return o;
            }
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
        SaveSync(data);
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

    public async Task<CreateOutcome> CreateWordsAsync(List<(string Word, string Sentence)> items)
    {
        await _gate.WaitAsync();
        try
        {
            var data = LoadUnsafe();
            var arr = (JsonArray)data["words"]!;
            var outcome = new CreateOutcome();
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            foreach (var (word, sentence) in items)
            {
                var cleanWord = word.Trim();
                var cleanSentence = (sentence ?? "").Trim();
                var idx = -1;
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonObject o &&
                        string.Equals(o["word"]?.GetValue<string>(), cleanWord, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }
                if (idx >= 0)
                {
                    var w = (JsonObject)arr[idx]!;
                    NormalizeWord(w, now);
                    var scenes = (JsonArray)w["sentences"]!;
                    var exists = cleanSentence.Length > 0 && scenes.Any(s => s is JsonObject so &&
                        string.Equals(so["text"]?.GetValue<string>(), cleanSentence, StringComparison.OrdinalIgnoreCase));
                    if (cleanSentence.Length > 0 && !exists)
                    {
                        var scene = NewScene(cleanSentence, now);
                        scenes.Insert(0, scene);
                        outcome.ScenesToExplain.Add(new SceneRef
                        {
                            WordId = w["id"]!.GetValue<string>(),
                            SceneId = scene["id"]!.GetValue<string>(),
                            Word = cleanWord,
                            Text = cleanSentence,
                        });
                    }
                    RefreshLatestSentence(w);
                    w["updatedAt"] = now;
                    outcome.Hit++;
                    outcome.Words.Add((JsonObject)w.DeepClone());
                }
                else
                {
                    var w = NewWord(cleanWord, cleanSentence, now);
                    arr.Add(w);
                    outcome.Added++;
                    outcome.NewIds.Add(w["id"]!.GetValue<string>());
                    var scene = (JsonObject)((JsonArray)w["sentences"]!)[0]!;
                    outcome.ScenesToExplain.Add(new SceneRef
                    {
                        WordId = w["id"]!.GetValue<string>(),
                        SceneId = scene["id"]!.GetValue<string>(),
                        Word = cleanWord,
                        Text = cleanSentence,
                    });
                    outcome.Words.Add((JsonObject)w.DeepClone());
                }
            }
            SaveUnsafe(data);
            return outcome;
        }
        finally { _gate.Release(); }
    }

    private static JsonObject NewWord(string word, string sentence, long now)
    {
        var scene = NewScene(sentence, now);
        var scenes = new JsonArray { scene };
        return new JsonObject
        {
            ["id"] = NewId("w"),
            ["word"] = word,
            ["phonetic"] = "",
            ["translation"] = "",
            ["definitions"] = new JsonArray(),
            ["audio"] = "",
            ["sentence"] = sentence,
            ["sentences"] = scenes,
            ["note"] = "",
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
            CopyScalar(patch, word, "note");
            CopyScalar(patch, word, "audio");
            if (patch["definitions"] is JsonArray defs) word["definitions"] = (JsonArray)defs.DeepClone();
            NormalizeWord(word, DateTimeOffset.Now.ToUnixTimeMilliseconds());
            word["updatedAt"] = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            SaveUnsafe(data);
            return (JsonObject)word.DeepClone();
        }
        finally { _gate.Release(); }
    }

    public async Task<(bool Ok, JsonObject Word, string Error)> PatchSceneAsync(string wordId, string sceneId, string text)
    {
        await _gate.WaitAsync();
        try
        {
            var data = LoadUnsafe();
            var arr = (JsonArray)data["words"]!;
            var w = arr.FirstOrDefault(x => x is JsonObject o && string.Equals(o["id"]?.GetValue<string>(), wordId, StringComparison.Ordinal));
            if (w is not JsonObject word) return (false, null, "单词不存在");
            if (word["sentences"] is not JsonArray scenes) return (false, null, "词条没有句子");
            var scene = scenes.FirstOrDefault(s => s is JsonObject so &&
                string.Equals(so["id"]?.GetValue<string>(), sceneId, StringComparison.Ordinal));
            if (scene is not JsonObject so2) return (false, null, "句子不存在");
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var clean = (text ?? "").Trim();
            so2["text"] = clean;
            so2["updatedAt"] = now;
            if (so2["ai"] is JsonObject ai && (ai["status"]?.GetValue<string>() ?? "") == "done")
            {
                ai["status"] = "error";
                ai["error"] = "句子已修改，请重新讲解";
            }
            NormalizeWord(word, now);
            word["updatedAt"] = now;
            SaveUnsafe(data);
            return (true, (JsonObject)word.DeepClone(), "");
        }
        finally { _gate.Release(); }
    }

    public async Task<(bool Ok, JsonObject Word, string Error)> DeleteSceneAsync(string wordId, string sceneId)
    {
        await _gate.WaitAsync();
        try
        {
            var data = LoadUnsafe();
            var arr = (JsonArray)data["words"]!;
            var w = arr.FirstOrDefault(x => x is JsonObject o && string.Equals(o["id"]?.GetValue<string>(), wordId, StringComparison.Ordinal));
            if (w is not JsonObject word) return (false, null, "单词不存在");
            if (word["sentences"] is not JsonArray scenes) return (false, null, "词条没有句子");
            if (scenes.Count <= 1) return (false, null, "这是最后一句，删除整词请用词条删除");
            var idx = -1;
            for (var i = 0; i < scenes.Count; i++)
            {
                if (scenes[i] is JsonObject so &&
                    string.Equals(so["id"]?.GetValue<string>(), sceneId, StringComparison.Ordinal))
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0) return (false, null, "句子不存在");
            scenes.RemoveAt(idx);
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            NormalizeWord(word, now);
            word["updatedAt"] = now;
            SaveUnsafe(data);
            return (true, (JsonObject)word.DeepClone(), "");
        }
        finally { _gate.Release(); }
    }

    public async Task<JsonObject> GetWordAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            var data = LoadUnsafe();
            var w = ((JsonArray)data["words"]!).FirstOrDefault(x => x is JsonObject o &&
                string.Equals(o["id"]?.GetValue<string>(), id, StringComparison.Ordinal));
            return w is JsonObject wo ? (JsonObject)wo.DeepClone() : null;
        }
        finally { _gate.Release(); }
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

    public async Task MutateSceneAsync(string wordId, string sceneId, Action<JsonObject> mutate)
    {
        await _gate.WaitAsync();
        try
        {
            var data = LoadUnsafe();
            var arr = (JsonArray)data["words"]!;
            var w = arr.FirstOrDefault(x => x is JsonObject o && string.Equals(o["id"]?.GetValue<string>(), wordId, StringComparison.Ordinal));
            if (w is not JsonObject word) return;
            if (word["sentences"] is not JsonArray scenes) return;
            var scene = scenes.FirstOrDefault(s => s is JsonObject so &&
                string.Equals(so["id"]?.GetValue<string>(), sceneId, StringComparison.Ordinal));
            if (scene is not JsonObject so2) return;
            mutate(so2);
            so2["updatedAt"] = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            word["updatedAt"] = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            SaveUnsafe(data);
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
                if (mode != "replace" && arr.Any(x => x is JsonObject z &&
                    string.Equals(z["word"]?.GetValue<string>(), word, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }
                var w = o.DeepClone() as JsonObject;
                if (w == null) continue;
                NormalizeWord(w, now);
                arr.Add(w);
                added++;
            }
            SaveUnsafe(data);
            return new JsonObject
            {
                ["ok"] = true,
                ["added"] = added,
                ["skipped"] = skipped,
                ["total"] = arr.Count,
                ["words"] = (JsonArray)arr.DeepClone(),
            };
        }
        finally { _gate.Release(); }
    }

    private static void CopyScalar(JsonObject from, JsonObject to, string key)
    {
        if (from[key] != null && from[key] is not JsonArray && from[key] is not JsonObject)
            to[key] = from[key]?.DeepClone();
    }
}
