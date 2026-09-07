using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.FileProviders;

namespace Wordbook;

public class DictClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    public async Task<JsonObject> LookupAsync(string word)
    {
        var result = new JsonObject
        {
            ["ok"] = true,
            ["found"] = false,
            ["phonetic"] = "",
            ["translation"] = "",
            ["definitions"] = new JsonArray(),
            ["audio"] = "",
        };
        var dictTask = FetchDictAsync(word, result);
        var transTask = FetchTranslationAsync(word, result);
        try { await Task.WhenAll(dictTask, transTask); } catch { /* 单项失败不致命 */ }
        return result;
    }

    private static async Task FetchDictAsync(string word, JsonObject target)
    {
        try
        {
            using var res = await Http.GetAsync("https://api.dictionaryapi.dev/api/v2/entries/en/" + Uri.EscapeDataString(word));
            if (!res.IsSuccessStatusCode) return;
            var node = JsonNode.Parse(await res.Content.ReadAsStringAsync());
            if (node is not JsonArray arr) return;
            var defs = (JsonArray)target["definitions"]!;
            var exs = new List<string>();
            foreach (var item in arr)
            {
                if (item is not JsonObject entry) continue;
                if (string.IsNullOrEmpty(target["phonetic"]?.GetValue<string>()))
                {
                    var ph = entry["phonetic"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrEmpty(ph) && entry["phonetics"] is JsonArray ps)
                    {
                        var p = ps.FirstOrDefault(x => x is JsonObject po && !string.IsNullOrEmpty(po["text"]?.GetValue<string>()));
                        if (p is JsonObject po2) ph = po2["text"]!.GetValue<string>();
                    }
                    if (!string.IsNullOrEmpty(ph)) target["phonetic"] = ph;
                }
                if (string.IsNullOrEmpty(target["audio"]?.GetValue<string>()) && entry["phonetics"] is JsonArray ph2)
                {
                    var a = ph2.FirstOrDefault(x => x is JsonObject po && !string.IsNullOrEmpty(po["audio"]?.GetValue<string>())
                        && po["audio"]!.GetValue<string>().Contains(".mp3", StringComparison.OrdinalIgnoreCase));
                    if (a is JsonObject ao) target["audio"] = ao["audio"]!.GetValue<string>();
                }
                if (entry["meanings"] is JsonArray meanings)
                {
                    foreach (var m in meanings)
                    {
                        if (m is not JsonObject mo) continue;
                        var pos = mo["partOfSpeech"]?.GetValue<string>() ?? "";
                        if (mo["definitions"] is not JsonArray ds) continue;
                        foreach (var d in ds)
                        {
                            if (d is not JsonObject do2 || string.IsNullOrEmpty(do2["definition"]?.GetValue<string>())) continue;
                            defs.Add((pos.Length > 0 ? pos + ". " : "") + do2["definition"]!.GetValue<string>());
                            if (!string.IsNullOrEmpty(do2["example"]?.GetValue<string>()) && exs.Count < 3)
                                exs.Add(do2["example"]!.GetValue<string>());
                            if (defs.Count >= 6) break;
                        }
                        if (defs.Count >= 6) break;
                    }
                }
                if (defs.Count >= 6) break;
            }
            if (defs.Count > 0) target["found"] = true;
        }
        catch { /* 忽略 */ }
    }

    private static async Task FetchTranslationAsync(string word, JsonObject target)
    {
        try
        {
            var url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(word) + "&langpair=en|zh-CN";
            using var res = await Http.GetAsync(url);
            if (!res.IsSuccessStatusCode) return;
            var node = JsonNode.Parse(await res.Content.ReadAsStringAsync());
            var t = node?["responseData"]?["translatedText"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(t)) return;
            t = t.Replace("&quot;", "\"").Trim();
            if (t.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase)) return;
            if (t.Length > 120) t = t[..120];
            if (t.Length > 0 && !t.Equals(word, StringComparison.OrdinalIgnoreCase)) target["translation"] = t;
        }
        catch { /* 忽略 */ }
    }
}

public class AiClient
{
    private readonly Config _cfg;
    private readonly HttpClient _http;

    public AiClient(Config cfg)
    {
        _cfg = cfg;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(75) };
    }

    public bool Enabled => _cfg.AiEnabled;

    public async Task<(bool Ok, Dictionary<string, JsonObject> Map, string Error)> ExplainAsync(
        string sentence, List<(string Id, string Word)> items)
    {
        _cfg.Reload();
        if (!Enabled) return (false, new Dictionary<string, JsonObject>(), "未配置 DeepSeek API key");
        var payload = new JsonObject
        {
            ["model"] = _cfg.DeepSeekModel,
            ["temperature"] = 0.3,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = "你是一位耐心的英语老师。用户会给你一句英文原句（可能为空）和要学习的单词列表。" +
                        "如果原句不为空，请解释每个单词在“这一句里”的意思；如果原句为空，就按这个词的一般词义和常见用法讲解。" +
                        "只输出一个 JSON 对象，不要输出 markdown 代码块或任何解释。" +
                        "JSON 结构：{\"words\":[{\"word\":\"原词小写\",\"inContext\":\"中文：这个词（在这个句子里）是什么意思\",\"why\":\"中文：为什么是这个意思（词性/搭配/语境；无原句时讲词义来源）\",\"rephrase\":\"换一种更简单的说法；无原句时给一个简单的英文例句或同义表达\",\"tip\":\"中文记忆技巧：词根/联想/相近词\"}]}。" +
                        "要求：每个字段都是自然流畅的中文（rephrase 可含英文），每个词条总共不超过 5 行，不啰嗦、不空话。",
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = BuildUserPrompt(sentence, items),
                },
            },
        };
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, _cfg.DeepSeekBase + "/chat/completions")
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.DeepSeekKey);
            using var res = await _http.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                return (false, new Dictionary<string, JsonObject>(), "接口返回 " + (int)res.StatusCode + ": " + Truncate(text, 160));
            var node = JsonNode.Parse(text);
            var content = node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            return ParseAnswer(content);
        }
        catch (Exception ex)
        {
            return (false, new Dictionary<string, JsonObject>(), ex.Message);
        }
    }

    private static string BuildUserPrompt(string sentence, List<(string Id, string Word)> items)
    {
        var words = items.Select(x => x.Word).ToList();
        var head = string.IsNullOrWhiteSpace(sentence)
            ? "用户没有提供原句，请直接讲解这些单词的一般含义和用法。"
            : "原句：" + sentence;
        return head + "\n\n要学习的单词：" + string.Join("、", words) +
            "\n\n请按上面要求的 JSON 结构返回，word 字段与给出的小写单词一一对应。";
    }

    private static (bool, Dictionary<string, JsonObject>, string) ParseAnswer(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return (false, new Dictionary<string, JsonObject>(), "AI 返回为空");
        var s = content.Trim();
        var i = s.IndexOf('{');
        var j = s.LastIndexOf('}');
        if (i >= 0 && j > i) s = s[i..(j + 1)];
        try
        {
            var node = JsonNode.Parse(s);
            var arr = node?["words"] as JsonArray;
            if (arr == null) return (false, new Dictionary<string, JsonObject>(), "AI 返回格式无法解析");
            var map = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in arr)
            {
                if (item is not JsonObject o) continue;
                var word = (o["word"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
                if (word.Length == 0) continue;
                var clean = new JsonObject();
                foreach (var k in new[] { "inContext", "why", "rephrase", "tip" })
                    clean[k] = (o[k]?.GetValue<string>() ?? "").Trim();
                map[word] = clean;
            }
            return (true, map, "");
        }
        catch (Exception ex)
        {
            return (false, new Dictionary<string, JsonObject>(), "AI 返回解析失败：" + ex.Message);
        }
    }

    private static string Truncate(string s, int n)
    {
        return s.Length <= n ? s : s[..n] + "…";
    }
}

public class Pipeline
{
    private readonly DataStore _store;
    private readonly DictClient _dict;
    private readonly AiClient _ai;

    public Pipeline(DataStore store, DictClient dict, AiClient ai)
    {
        _store = store;
        _dict = dict;
        _ai = ai;
    }

    public void ProcessNewAsync(CreateOutcome outcome)
    {
        if (outcome == null || outcome.Words.Count == 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var needAi = _ai.Enabled;
                var newWords = outcome.Words
                    .Where(w => outcome.NewIds.Contains(w["id"]!.GetValue<string>()))
                    .ToList();
                var aiWords = outcome.Words
                    .Where(w => AiStatus(w) != "done")
                    .ToList();
                if (!needAi)
                {
                    foreach (var w in aiWords.Where(w => AiStatus(w) == "pending"))
                    {
                        var id = w["id"]!.GetValue<string>();
                        await _store.MutateWordAsync(id, x =>
                        {
                            if (x["ai"] is not JsonObject a)
                            {
                                a = new JsonObject();
                                x["ai"] = a;
                            }
                            a["status"] = "none";
                            a["error"] = "未配置 DeepSeek key";
                        });
                    }
                }
                var lookups = newWords.Select(async w =>
                {
                    var id = w["id"]!.GetValue<string>();
                    var word = w["word"]!.GetValue<string>();
                    var dict = await _dict.LookupAsync(word);
                    await _store.MutateWordAsync(id, x =>
                    {
                        x["phonetic"] = dict["phonetic"]?.GetValue<string>() ?? "";
                        if (string.IsNullOrEmpty(x["translation"]?.GetValue<string>()))
                            x["translation"] = dict["translation"]?.GetValue<string>() ?? "";
                        if (dict["definitions"] is JsonArray defs && defs.Count > 0)
                            x["definitions"] = (JsonArray)defs.DeepClone();
                        if (string.IsNullOrEmpty(x["audio"]?.GetValue<string>()))
                            x["audio"] = dict["audio"]?.GetValue<string>() ?? "";
                    });
                }).ToArray();
                var aiTasks = new List<Task>();
                if (needAi && aiWords.Count > 0)
                {
                    var groups = aiWords.GroupBy(w => w["sentence"]?.GetValue<string>() ?? "");
                    foreach (var g in groups)
                    {
                        var items = g
                            .Select(w => (w["id"]!.GetValue<string>(), w["word"]!.GetValue<string>()))
                            .ToList();
                        aiTasks.Add(ExplainCoreAsync(g.Key ?? "", items));
                    }
                }
                var all = lookups.Concat(aiTasks).ToArray();
                await Task.WhenAll(all);
            }
            catch { /* 后台失败由页面状态展示 */ }
        });
    }

    private static string AiStatus(JsonObject w)
    {
        return w["ai"]?["status"]?.GetValue<string>() ?? "none";
    }

    public async Task<JsonObject> ExplainSingleAsync(string id)
    {
        var snap = await _store.GetSnapshotAsync();
        var arr = (JsonArray)snap["words"]!;
        var w = arr.FirstOrDefault(x => x is JsonObject o && string.Equals(o["id"]?.GetValue<string>(), id, StringComparison.Ordinal));
        if (w is not JsonObject word) return null;
        var sentence = word["sentence"]?.GetValue<string>() ?? "";
        var wd = word["word"]!.GetValue<string>();
        await ExplainCoreAsync(sentence, new List<(string, string)> { (id, wd) });
        return await GetWordAsync(id);
    }

    public async Task<JsonObject> LookupSingleAsync(string id)
    {
        var snap = await _store.GetSnapshotAsync();
        var arr = (JsonArray)snap["words"]!;
        var w = arr.FirstOrDefault(x => x is JsonObject o && string.Equals(o["id"]?.GetValue<string>(), id, StringComparison.Ordinal));
        if (w is not JsonObject word) return null;
        var wd = word["word"]!.GetValue<string>();
        var dict = await _dict.LookupAsync(wd);
        await _store.MutateWordAsync(id, x =>
        {
            x["phonetic"] = dict["phonetic"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrEmpty(dict["translation"]?.GetValue<string>()))
                x["translation"] = dict["translation"]!.GetValue<string>();
            x["definitions"] = (JsonArray)(dict["definitions"] ?? new JsonArray()).DeepClone();
            if (!string.IsNullOrEmpty(dict["audio"]?.GetValue<string>()))
                x["audio"] = dict["audio"]!.GetValue<string>();
        });
        return await GetWordAsync(id);
    }

    private async Task ExplainCoreAsync(string sentence, List<(string Id, string Word)> items)
    {
        var (ok, map, error) = await _ai.ExplainAsync(sentence, items);
        foreach (var (id, word) in items)
        {
            var key = word.Trim().ToLowerInvariant();
            if (ok && map.TryGetValue(key, out var ai))
            {
                await _store.MutateWordAsync(id, x =>
                {
                    x["ai"] = new JsonObject
                    {
                        ["status"] = "done",
                        ["inContext"] = ai["inContext"]?.GetValue<string>() ?? "",
                        ["why"] = ai["why"]?.GetValue<string>() ?? "",
                        ["rephrase"] = ai["rephrase"]?.GetValue<string>() ?? "",
                        ["tip"] = ai["tip"]?.GetValue<string>() ?? "",
                        ["error"] = "",
                    };
                });
            }
            else
            {
                await _store.MutateWordAsync(id, x =>
                {
                    if (x["ai"] is not JsonObject ai0)
                    {
                        ai0 = new JsonObject();
                        x["ai"] = ai0;
                    }
                    ai0["status"] = "error";
                    ai0["error"] = error ?? "AI 讲解失败";
                });
            }
        }
    }

    private async Task<JsonObject> GetWordAsync(string id)
    {
        var snap = await _store.GetSnapshotAsync();
        var arr = (JsonArray)snap["words"]!;
        var w = arr.FirstOrDefault(x => x is JsonObject o && string.Equals(o["id"]?.GetValue<string>(), id, StringComparison.Ordinal));
        return w as JsonObject;
    }
}

public static class WebHost
{
    public static WebApplication Build(Config cfg, DataStore store, DictClient dict, AiClient ai, Pipeline pipe)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(cfg.HomeUrl);
        var app = builder.Build();

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(cfg.Root),
            ServeUnknownFileTypes = true,
        });

        app.MapGet("/", () => Results.File(Path.Combine(cfg.Root, "index.html"), "text/html; charset=utf-8"));

        app.MapGet("/api/ping", () => Results.Json(new { ok = true, service = "wordbook", ai = cfg.AiEnabled }));
        app.MapGet("/api/config", () =>
        {
            cfg.Reload();
            return Results.Json(new { ok = true, aiConfigured = cfg.AiEnabled, model = cfg.AiEnabled ? cfg.DeepSeekModel : "" });
        });
        app.MapGet("/api/data", async () => Results.Json(await store.GetSnapshotAsync()));

        app.MapPost("/api/words/batch", async (HttpContext cx) =>
        {
            var body = await ReadBodyAsync(cx);
            var items = new List<(string, string)>();
            if (body?["items"] is JsonArray arr)
            {
                foreach (var it in arr)
                {
                    if (it is not JsonObject o) continue;
                    var word = (o["word"]?.GetValue<string>() ?? "").Trim();
                    if (word.Length == 0) continue;
                    items.Add((word, o["sentence"]?.GetValue<string>() ?? ""));
                }
            }
            if (items.Count == 0) return JsonResult(new { ok = false, error = "没有有效的单词" }, 400);
            var outcome = await store.CreateWordsAsync(items);
            pipe.ProcessNewAsync(outcome);
            return JsonResult(new { ok = true, words = outcome.Words, added = outcome.Added, hit = outcome.Hit });
        });

        app.MapPost("/api/words", async (HttpContext cx) =>
        {
            var body = await ReadBodyAsync(cx);
            var word = (body?["word"]?.GetValue<string>() ?? "").Trim();
            if (word.Length == 0) return JsonResult(new { ok = false, error = "单词不能为空" }, 400);
            var sentence = body?["sentence"]?.GetValue<string>() ?? "";
            var outcome = await store.CreateWordsAsync(new List<(string, string)> { (word, sentence) });
            pipe.ProcessNewAsync(outcome);
            return JsonResult(new { ok = true, word = outcome.Words[0], added = outcome.Added, hit = outcome.Hit });
        });

        app.MapPatch("/api/words/{id}", async (HttpContext cx, string id) =>
        {
            var body = await ReadBodyAsync(cx);
            if (body == null) return JsonResult(new { ok = false, error = "请求体无效" }, 400);
            var w = await store.PatchWordAsync(id, body);
            if (w == null) return JsonResult(new { ok = false, error = "单词不存在" }, 404);
            return JsonResult(new { ok = true, word = w });
        });

        app.MapDelete("/api/words/{id}", async (string id) =>
        {
            var ok = await store.DeleteWordAsync(id);
            return ok ? Results.Json(new { ok = true }) : Results.Json(new { ok = false, error = "单词不存在" }, statusCode: 404);
        });

        app.MapPost("/api/words/{id}/explain", async (string id) =>
        {
            var w = await pipe.ExplainSingleAsync(id);
            return w == null ? Results.Json(new { ok = false, error = "单词不存在" }, statusCode: 404)
                : Results.Json(new { ok = true, word = w });
        });

        app.MapPost("/api/words/{id}/lookup", async (string id) =>
        {
            var w = await pipe.LookupSingleAsync(id);
            return w == null ? Results.Json(new { ok = false, error = "单词不存在" }, statusCode: 404)
                : Results.Json(new { ok = true, word = w });
        });

        app.MapPost("/api/import", async (HttpContext cx) =>
        {
            var body = await ReadBodyAsync(cx);
            var mode = body?["mode"]?.GetValue<string>() == "replace" ? "replace" : "merge";
            var words = body?["words"] as JsonArray ?? new JsonArray();
            var r = await store.ImportAsync(mode, words);
            return Results.Json(r);
        });

        app.MapGet("/api/export", async () =>
        {
            var data = await store.GetSnapshotAsync();
            var bytes = System.Text.Encoding.UTF8.GetBytes(data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return Results.File(bytes, "application/json", $"wordbook-backup-{DateTime.Now:yyyy-MM-dd}.json");
        });

        app.MapFallback(async (HttpContext cx) =>
        {
            var path = cx.Request.Path.Value ?? "/";
            if (path == "/") path = "/index.html";
            var full = Path.GetFullPath(Path.Combine(cfg.Root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
            var rootFull = Path.GetFullPath(cfg.Root) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                cx.Response.StatusCode = 404;
                await cx.Response.WriteAsJsonAsync(new { ok = false, error = "未找到资源" });
                return;
            }
            cx.Response.Headers["Cache-Control"] = "no-store";
            await cx.Response.SendFileAsync(full);
        });

        return app;
    }

    private static async Task<JsonObject> ReadBodyAsync(HttpContext cx)
    {
        try
        {
            using var reader = new StreamReader(cx.Request.Body);
            var text = await reader.ReadToEndAsync();
            return JsonNode.Parse(text) as JsonObject;
        }
        catch { return null; }
    }

    private static IResult JsonResult(object o, int status = 200)
    {
        return Results.Json(o, statusCode: status);
    }
}
