using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Muse2Api;

public static class Endpoints
{
    public const string Version = "0.3.0-dotnet.1";

    public static void Map(WebApplication app, AppState s)
    {
        // ---------- health (public) ----------
        app.MapGet("/healthz", () => Results.Json(new { ok = true, version = Version }));
        app.MapGet("/readyz", async () =>
        {
            var driver = await s.Driver.Health();
            var pool = s.Pool.Stats();
            var ready = driver.GetValueOrDefault("ok") is true
                && (pool["available"] > 0 || !s.Driver.RequiresAccount);
            return Results.Json(new
            {
                ready,
                driver = driver.ToDictionary(kv => kv.Key, kv => kv.Value),
                accounts = pool,
            });
        });

        // ---------- models ----------
        app.MapGet("/v1/models", (HttpContext ctx) =>
        {
            RequireApi(ctx, s);
            var data = new JsonArray();
            foreach (var m in ModelRegistry.Models.Values)
                data.Add(new JsonObject
                {
                    ["id"] = m.Id, ["object"] = "model",
                    ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["owned_by"] = m.OwnedBy, ["kind"] = m.Kind,
                });
            foreach (var (alias, target) in ModelRegistry.Aliases)
                data.Add(new JsonObject
                {
                    ["id"] = alias, ["object"] = "model",
                    ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["owned_by"] = "muse",
                    ["kind"] = ModelRegistry.Models[target].Kind, ["alias_of"] = target,
                });
            return Results.Json(new JsonObject { ["object"] = "list", ["data"] = data });
        });

        // ---------- chat ----------
        app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
            await Chat(ctx, s));
        app.MapPost("/v1/responses", async (HttpContext ctx) =>
            await Responses(ctx, s));

        // ---------- images / videos / media ----------
        app.MapPost("/v1/images/generations", async (HttpContext ctx) =>
            await ImagesGenerate(ctx, s));
        app.MapGet("/v1/images/generations/{taskId}", (HttpContext ctx, string taskId) =>
        {
            RequireApi(ctx, s);
            var task = s.Tasks.Get(taskId);
            if (task is null || task.Kind != "image")
                throw MuseException.NotFound($"image task '{taskId}' not found");
            ctx.Items["model"] = task.Request.GetValueOrDefault("model")?.ToString();
            return Results.Json(ImageTaskView(task));
        });
        app.MapPost("/v1/images/edits", async (HttpContext ctx) =>
            await ImagesEdit(ctx, s));
        app.MapPost("/v1/videos", async (HttpContext ctx) =>
            await VideoCreate(ctx, s));
        app.MapGet("/v1/videos/{taskId}", (HttpContext ctx, string taskId) =>
        {
            RequireApi(ctx, s);
            var task = s.Tasks.Get(taskId);
            if (task is null || task.Kind != "video")
                throw MuseException.NotFound($"video task '{taskId}' not found");
            ctx.Items["model"] = task.Request.GetValueOrDefault("model")?.ToString();
            return Results.Json(VideoTaskView(task));
        });
        app.MapGet("/v1/media/{name}", (HttpContext ctx, string name) =>
        {
            var path = s.Media.PathOf(name);
            ctx.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.File(path, MediaStore.MimeOf(name),
                enableRangeProcessing: true);
        });

        app.MapGet("/dashboard", () => Results.Content(DashboardHtml, "text/html"));

        // ---------- admin ----------
        MapAdmin(app, s);
    }

    // ---------- auth ----------
    public static string Bearer(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth[7..].Trim();
        return ctx.Request.Headers["X-Api-Key"].ToString().Trim();
    }

    private static (string Id, string Name) RequireApi(HttpContext ctx, AppState s)
    {
        var provided = Bearer(ctx);
        if (string.IsNullOrEmpty(provided)) throw MuseException.Unauthorized();
        if (TimingEquals(provided, s.Settings.EffectiveAdminKey))
            return Identify(ctx, "admin", "admin");
        if (TimingEquals(provided, s.Settings.ApiKey))
            return Identify(ctx, "legacy", "legacy");
        var key = s.Keys.Verify(provided);
        if (key is null) throw MuseException.Unauthorized();
        s.Keys.Touch(key);
        return Identify(ctx, key.Id, key.Name);
    }

    private static void RequireAdmin(HttpContext ctx, AppState s)
    {
        var provided = Bearer(ctx);
        if (string.IsNullOrEmpty(provided) || !TimingEquals(provided, s.Settings.EffectiveAdminKey))
            throw MuseException.Unauthorized();
        ctx.Items["key_id"] = "admin";
        ctx.Items["key_name"] = "admin";
    }

    private static (string, string) Identify(HttpContext ctx, string id, string name)
    {
        ctx.Items["key_id"] = id;
        ctx.Items["key_name"] = name;
        return (id, name);
    }

    private static bool TimingEquals(string a, string b)
    {
        if (string.IsNullOrEmpty(b)) return false;
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ab.Length != bb.Length) return false;
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }

    // ---------- chat ----------
    private static async Task Chat(HttpContext ctx, AppState s)
    {
        RequireApi(ctx, s);
        var body = await JsonHelp.ReadBody(ctx.Request);
        var modelName = JsonHelp.Str(body["model"]);
        ctx.Items["model"] = modelName;
        var spec = ModelRegistry.Resolve(modelName, "chat");
        ctx.Items["model"] = modelName ?? spec.Id;
        var messages = body["messages"] as JsonArray
            ?? throw MuseException.Invalid("messages must be a non-empty list");
        if (messages.Count == 0) throw MuseException.Invalid("messages must be a non-empty list");
        var msgObjs = messages.OfType<JsonObject>().ToList();
        if (msgObjs.Count != messages.Count)
            throw MuseException.Invalid("messages must be objects");
        var flat = Prompt.Flatten(msgObjs);
        if (string.IsNullOrEmpty(flat.Text) && flat.Images.Count == 0)
            throw MuseException.Invalid("messages contain no text or images");
        var images = await JsonHelp.LoadImageRefs(flat.Images);
        var req = new ChatRequest(flat.Text, spec.Id, images,
            JsonHelp.Str(body["user"]), Prompt.MessageTurns(msgObjs), ctx.RequestAborted);
        var stream = JsonHelp.Bool(body["stream"]);
        var completionId = "chatcmpl-" + Guid.NewGuid().ToString("N")[..24];
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var outModel = modelName ?? spec.Id;

        if (!stream)
        {
            var text = "";
            var (chatStream, chatAccount) = s.Gateway.ChatStreamWithAccount(req);
            ctx.Items["account_id"] = chatAccount;
            await foreach (var d in chatStream) text += d;
            await ctx.Response.WriteAsJsonAsync(new JsonObject
            {
                ["id"] = completionId, ["object"] = "chat.completion", ["created"] = created,
                ["model"] = outModel,
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = text },
                    ["finish_reason"] = "stop",
                }),
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = JsonHelp.ApproxTokens(flat.Text),
                    ["completion_tokens"] = JsonHelp.ApproxTokens(text),
                    ["total_tokens"] = JsonHelp.ApproxTokens(flat.Text) + JsonHelp.ApproxTokens(text),
                },
            });
            return;
        }

        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        await using var writer = new StreamWriter(ctx.Response.Body, leaveOpen: true);
        JsonObject Chunk(JsonObject delta, string? finish) => new()
        {
            ["id"] = completionId, ["object"] = "chat.completion.chunk", ["created"] = created,
            ["model"] = outModel,
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0, ["delta"] = delta,
                ["finish_reason"] = finish is null ? null : JsonValue.Create(finish),
            }),
        };
        async Task Sse(JsonNode payload)
            => await writer.WriteAsync("data: " + payload.ToJsonString() + "\n\n");
        var (chatStream2, chatAccount2) = s.Gateway.ChatStreamWithAccount(req);
        ctx.Items["account_id"] = chatAccount2;
        var enumerator = chatStream2.GetAsyncEnumerator(ctx.RequestAborted);
        string first;
        var heartbeat = s.Settings.StreamHeartbeatInterval;
        if (heartbeat <= 0)
        {
            try
            {
                if (!await enumerator.MoveNextAsync()) first = "";
                else first = enumerator.Current;
            }
            catch (MuseException)
            {
                await enumerator.DisposeAsync();
                throw;
            }
        }
        else
        {
            // Port of Python C4: with a heartbeat interval set, the 200 goes
            // out immediately and ": keepalive" SSE comments are sent while
            // the first upstream token is pending, so clients/proxies do not
            // drop the idle SSE connection. Upstream errors then surface as
            // SSE error events instead of HTTP codes, because headers have
            // already been committed by the first heartbeat.
            first = "";
            var moveNext = enumerator.MoveNextAsync();
            Exception? firstError = null;
            var hasFirst = false;
            while (!moveNext.IsCompleted)
            {
                var delay = Task.Delay(TimeSpan.FromSeconds(heartbeat), ctx.RequestAborted);
                var done = await Task.WhenAny(moveNext.AsTask(), delay);
                if (done == moveNext.AsTask()) break;
                if (ctx.RequestAborted.IsCancellationRequested) break;
                await writer.WriteAsync(": keepalive\n\n");
                await writer.FlushAsync();
            }
            if (ctx.RequestAborted.IsCancellationRequested)
            {
                await enumerator.DisposeAsync();
                return;
            }
            try
            {
                if (await moveNext) { first = enumerator.Current; hasFirst = true; }
            }
            catch (Exception ex) { firstError = ex; }
            if (firstError is not null)
            {
                await enumerator.DisposeAsync();
                var errBody = firstError is MuseException mex ? mex.ToBody()
                    : new MuseException("internal error").ToBody();
                await Sse(System.Text.Json.Nodes.JsonNode.Parse(
                    System.Text.Json.JsonSerializer.Serialize(errBody))!);
                await writer.WriteAsync("data: [DONE]\n\n");
                await writer.FlushAsync();
                return;
            }
            if (!hasFirst) first = "";
        }
        var produced = first;
        await Sse(Chunk(new JsonObject { ["role"] = "assistant", ["content"] = produced }, null));
        await writer.FlushAsync();
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                produced += enumerator.Current;
                await Sse(Chunk(new JsonObject { ["content"] = enumerator.Current }, null));
                await writer.FlushAsync();
            }
        }
        finally { await enumerator.DisposeAsync(); }
        await Sse(Chunk(new JsonObject(), "stop"));
        if ((body["stream_options"] as JsonObject)?["include_usage"] is JsonValue uv
            && uv.TryGetValue<bool>(out var include) && include)
        {
            await Sse(new JsonObject
            {
                ["id"] = completionId, ["object"] = "chat.completion.chunk", ["created"] = created,
                ["model"] = outModel, ["choices"] = new JsonArray(),
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = JsonHelp.ApproxTokens(flat.Text),
                    ["completion_tokens"] = JsonHelp.ApproxTokens(produced),
                    ["total_tokens"] = JsonHelp.ApproxTokens(flat.Text) + JsonHelp.ApproxTokens(produced),
                },
            });
        }
        await writer.WriteAsync("data: [DONE]\n\n");
        await writer.FlushAsync();
    }

    // ---------- responses ----------
    private static async Task Responses(HttpContext ctx, AppState s)
    {
        RequireApi(ctx, s);
        var body = await JsonHelp.ReadBody(ctx.Request);
        var modelName = JsonHelp.Str(body["model"]);
        ctx.Items["model"] = modelName;
        var spec = ModelRegistry.Resolve(modelName, "chat");
        ctx.Items["model"] = modelName ?? spec.Id;
        var messages = ResponsesToMessages(body);
        var flat = Prompt.Flatten(messages);
        if (string.IsNullOrEmpty(flat.Text) && flat.Images.Count == 0)
            throw MuseException.Invalid("input contains no text or images");
        var images = await JsonHelp.LoadImageRefs(flat.Images);
        var req = new ChatRequest(flat.Text, spec.Id, images,
            JsonHelp.Str(body["user"]), Prompt.MessageTurns(messages), ctx.RequestAborted);
        var respId = "resp_" + Guid.NewGuid().ToString("N")[..24];
        var msgId = "msg_" + Guid.NewGuid().ToString("N")[..24];
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var outModel = modelName ?? spec.Id;

        JsonObject MessageItem(string text) => new()
        {
            ["id"] = msgId, ["type"] = "message", ["status"] = "completed", ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject
                { ["type"] = "output_text", ["annotations"] = new JsonArray(), ["text"] = text }),
        };
        JsonObject Usage(string output) => new()
        {
            ["input_tokens"] = JsonHelp.ApproxTokens(flat.Text),
            ["output_tokens"] = JsonHelp.ApproxTokens(output),
            ["total_tokens"] = JsonHelp.ApproxTokens(flat.Text) + JsonHelp.ApproxTokens(output),
            ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 },
            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 },
        };
        JsonObject ResponseObject(string text, string status) => new()
        {
            ["id"] = respId, ["object"] = "response", ["created_at"] = created,
            ["model"] = outModel, ["status"] = status,
            ["output"] = status == "completed" ? new JsonArray(MessageItem(text)) : new JsonArray(),
            ["usage"] = Usage(text), ["parallel_tool_calls"] = true,
            ["tool_choice"] = "auto", ["tools"] = new JsonArray(),
        };

        if (!JsonHelp.Bool(body["stream"]))
        {
            var text = "";
            var (chatStream, chatAccount) = s.Gateway.ChatStreamWithAccount(req);
            ctx.Items["account_id"] = chatAccount;
            await foreach (var d in chatStream) text += d;
            await ctx.Response.WriteAsJsonAsync(ResponseObject(text, "completed"));
            return;
        }

        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        await using var writer = new StreamWriter(ctx.Response.Body, leaveOpen: true);
        var seq = 0;
        async Task Sse(JsonObject payload)
        {
            payload["sequence_number"] = seq++;
            await writer.WriteAsync("data: " + payload.ToJsonString() + "\n\n");
            await writer.FlushAsync();
        }
        var (chatStream2, chatAccount2) = s.Gateway.ChatStreamWithAccount(req);
        ctx.Items["account_id"] = chatAccount2;
        var enumerator = chatStream2.GetAsyncEnumerator(ctx.RequestAborted);
        string first = "";
        try { if (await enumerator.MoveNextAsync()) first = enumerator.Current; }
        catch (MuseException) { await enumerator.DisposeAsync(); throw; }
        await Sse(new JsonObject { ["type"] = "response.created", ["response"] = ResponseObject("", "in_progress") });
        await Sse(new JsonObject { ["type"] = "response.in_progress", ["response"] = ResponseObject("", "in_progress") });
        await Sse(new JsonObject
        {
            ["type"] = "response.output_item.added", ["output_index"] = 0,
            ["item"] = new JsonObject
            {
                ["id"] = msgId, ["type"] = "message", ["status"] = "in_progress",
                ["role"] = "assistant", ["content"] = new JsonArray(),
            },
        });
        await Sse(new JsonObject
        {
            ["type"] = "response.content_part.added", ["item_id"] = msgId, ["output_index"] = 0,
            ["content_index"] = 0,
            ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray() },
        });
        var produced = first;
        if (produced.Length > 0)
            await Sse(new JsonObject
            {
                ["type"] = "response.output_text.delta", ["item_id"] = msgId, ["output_index"] = 0,
                ["content_index"] = 0, ["delta"] = produced, ["logprobs"] = new JsonArray(),
            });
        try
        {
            while (await enumerator.MoveNextAsync())
            {
                produced += enumerator.Current;
                await Sse(new JsonObject
                {
                    ["type"] = "response.output_text.delta", ["item_id"] = msgId, ["output_index"] = 0,
                    ["content_index"] = 0, ["delta"] = enumerator.Current, ["logprobs"] = new JsonArray(),
                });
            }
        }
        finally { await enumerator.DisposeAsync(); }
        await Sse(new JsonObject
        {
            ["type"] = "response.output_item.done", ["output_index"] = 0,
            ["item"] = MessageItem(produced),
        });
        await Sse(new JsonObject { ["type"] = "response.completed", ["response"] = ResponseObject(produced, "completed") });
    }

    private static List<JsonObject> ResponsesToMessages(JsonObject body)
    {
        var messages = new List<JsonObject>();
        if (JsonHelp.Str(body["instructions"]) is { } instructions)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = instructions });
        var input = body["input"];
        var toolItems = new HashSet<string>
            { "function_call", "function_call_output", "reasoning", "computer_call",
              "file_search_call", "web_search_call" };
        if (input is null || (input is JsonValue v && v.TryGetValue<string>(out var sv) && string.IsNullOrWhiteSpace(sv)))
        {
            // no user input
        }
        else if (input is JsonValue sv2 && sv2.TryGetValue<string>(out var s))
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = s });
        else if (input is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not JsonObject obj)
                    throw MuseException.Invalid("input items must be objects");
                var type = JsonHelp.Str(obj["type"]);
                if (type is not null && toolItems.Contains(type))
                    throw MuseException.Invalid(
                        $"input item type '{type}' is not supported: muse.ai has no function calling");
                if (obj.ContainsKey("role") || type is "message" or null)
                    messages.Add(new JsonObject
                    {
                        ["role"] = JsonHelp.Str(obj["role"]) ?? "user",
                        ["content"] = obj["content"]?.DeepClone(),
                    });
                else
                    throw MuseException.Invalid($"unsupported input item type '{type}'");
            }
        }
        else throw MuseException.Invalid("input must be a string or a list of items");
        return messages;
    }

    // ---------- images ----------
    private static JsonObject ImageTaskView(BgTask task) => new()
    {
        ["id"] = task.Id, ["object"] = "image.task", ["status"] = task.Status,
        ["progress"] = task.Progress, ["created_at"] = (long)task.CreatedAt,
        ["model"] = task.Request.GetValueOrDefault("model")?.ToString(),
        ["result"] = task.Result is null ? null : DictToJson(task.Result),
        ["error"] = task.Error is null ? null : DictToJson(task.Error),
    };

    private static JsonObject VideoTaskView(BgTask task) => new()
    {
        ["id"] = task.Id, ["object"] = "video", ["status"] = task.Status,
        ["progress"] = task.Progress, ["created_at"] = (long)task.CreatedAt,
        ["model"] = task.Request.GetValueOrDefault("model")?.ToString(),
        ["result"] = task.Result is null ? null : DictToJson(task.Result),
        ["error"] = task.Error is null ? null : DictToJson(task.Error),
    };

    private static async Task ImagesGenerate(HttpContext ctx, AppState s)
    {
        RequireApi(ctx, s);
        var body = await JsonHelp.ReadBody(ctx.Request);
        var prompt = JsonHelp.Str(body["prompt"])
            ?? throw MuseException.Invalid("prompt is required");
        var spec = ModelRegistry.Resolve(JsonHelp.Str(body["model"]), "image");
        ctx.Items["model"] = body["model"]?.ToString() ?? spec.Id;
        var n = Math.Clamp(JsonHelp.Int(body["n"], 1), 1, 4);
        if (body["n"] is not null && (JsonHelp.Int(body["n"], 1) < 1 || JsonHelp.Int(body["n"], 1) > 4))
            throw MuseException.Invalid("n must be between 1 and 4");
        var refs = new List<string>();
        if (body["image"] is JsonArray arr) refs.AddRange(arr.Select(x => JsonHelp.Str(x) ?? ""));
        else if (JsonHelp.Str(body["image"]) is { } single) refs.Add(single);
        if (refs.Count > 4)
            throw MuseException.Invalid("at most 4 reference images are supported");
        var loaded = await JsonHelp.LoadImageRefs(refs.Where(r => r.Length > 0));
        var format = JsonHelp.Str(body["response_format"]) ?? "url";
        var isAsync = JsonHelp.Bool(body["async"]);
        var baseUrl = JsonHelp.BaseUrl(ctx.Request, s.Settings);

        async Task<JsonObject> Run()
        {
            var (results, imageAccount) = await s.Gateway.GenerateImage(new ImageRequest(
                prompt, spec.Id, JsonHelp.Str(body["size"]), n, loaded));
            ctx.Items["account_id"] = imageAccount;
            var data = new JsonArray();
            foreach (var r in results)
            {
                var item = new JsonObject { ["revised_prompt"] = r.RevisedPrompt };
                if (format == "b64_json") item["b64_json"] = Convert.ToBase64String(r.Data);
                else item["url"] = $"{baseUrl}/v1/media/{s.Media.Save(r.Data, r.Mime, "img")}";
                data.Add(item);
            }
            return new JsonObject
            {
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["data"] = data,
            };
        }

        if (!isAsync)
        {
            await ctx.Response.WriteAsJsonAsync(await Run());
            return;
        }
        var task = s.Tasks.Submit("image", new Dictionary<string, object?>
        {
            ["model"] = spec.Id, ["prompt"] = prompt, ["size"] = JsonHelp.Str(body["size"]),
            ["n"] = n, ["references"] = refs.Count,
        }, async _ => { var r = await Run(); return new Dictionary<string, System.Text.Json.Nodes.JsonNode?> { ["data"] = r["data"]?.DeepClone() }; });
        ctx.Items["task_id"] = task.Id;
        await ctx.Response.WriteAsJsonAsync(ImageTaskView(task));
    }

    private static async Task ImagesEdit(HttpContext ctx, AppState s)
    {
        RequireApi(ctx, s);
        var form = await ctx.Request.ReadFormAsync();
        if (form.ContainsKey("mask"))
            throw MuseException.Invalid("mask is not supported; muse.ai cannot inpaint a region");
        var files = form.Files.Where(f => f.Name is "image" or "image[]").ToList();
        if (files.Count == 0)
            throw MuseException.Invalid("at least one image file is required (field 'image' or 'image[]')");
        var prompt = form["prompt"].ToString();
        if (string.IsNullOrEmpty(prompt)) throw MuseException.Invalid("prompt is required");
        var nText = form["n"].ToString();
        var n = string.IsNullOrEmpty(nText) ? 1 : int.TryParse(nText, out var nv) ? nv : -1;
        if (n < 1 || n > 4) throw MuseException.Invalid("n must be between 1 and 4");
        var format = form["response_format"].ToString();
        if (string.IsNullOrEmpty(format)) format = "url";
        var spec = ModelRegistry.Resolve(form["model"].ToString() is { Length: > 0 } m ? m : null, "image");
        var refs = new List<InputImage>();
        foreach (var f in files)
        {
            using var ms = new MemoryStream();
            await f.CopyToAsync(ms);
            var mime = f.ContentType;
            if (!mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                mime = f.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || f.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                    ? "image/jpeg" : "image/png";
            refs.Add(new InputImage(ms.ToArray(), mime));
        }
        var (results, editAccount) = await s.Gateway.GenerateImage(new ImageRequest(
            prompt, spec.Id, form["size"].ToString() is { Length: > 0 } sz ? sz : null, n, refs));
        ctx.Items["account_id"] = editAccount;
        var baseUrl = JsonHelp.BaseUrl(ctx.Request, s.Settings);
        var data = new JsonArray();
        foreach (var r in results)
        {
            var item = new JsonObject { ["revised_prompt"] = r.RevisedPrompt };
            if (format == "b64_json") item["b64_json"] = Convert.ToBase64String(r.Data);
            else item["url"] = $"{baseUrl}/v1/media/{s.Media.Save(r.Data, r.Mime, "img")}";
            data.Add(item);
        }
        await ctx.Response.WriteAsJsonAsync(new JsonObject
        {
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["data"] = data,
        });
    }

    private static async Task VideoCreate(HttpContext ctx, AppState s)
    {
        RequireApi(ctx, s);
        var body = await JsonHelp.ReadBody(ctx.Request);
        var prompt = JsonHelp.Str(body["prompt"])
            ?? throw MuseException.Invalid("prompt is required");
        var spec = ModelRegistry.Resolve(JsonHelp.Str(body["model"]), "video");
        ctx.Items["model"] = body["model"]?.ToString() ?? spec.Id;
        InputImage? firstFrame = null;
        var frameRef = JsonHelp.Str(body["image"]) ?? JsonHelp.Str(body["input_reference"]);
        if (frameRef is not null) firstFrame = await MediaHelp.LoadImageRef(frameRef);
        var baseUrl = JsonHelp.BaseUrl(ctx.Request, s.Settings);
        var duration = body["duration"] is not null ? JsonHelp.Int(body["duration"]) : JsonHelp.Int(body["seconds"]);
        if (duration is < 1 or > 60 && (body["duration"] is not null || body["seconds"] is not null))
            throw MuseException.Invalid("duration must be between 1 and 60");
        var task = s.Tasks.Submit("video", new Dictionary<string, object?>
        {
            ["model"] = spec.Id, ["prompt"] = prompt, ["size"] = JsonHelp.Str(body["size"]),
            ["duration"] = duration == 0 ? null : duration, ["has_first_frame"] = firstFrame is not null,
        }, async progress =>
        {
            var (result, _) = await s.Gateway.GenerateVideo(new VideoRequest(
                prompt, spec.Id, JsonHelp.Str(body["size"]), duration == 0 ? null : duration,
                firstFrame, progress));
            var name = s.Media.Save(result.Data, result.Mime, "vid");
            return new Dictionary<string, System.Text.Json.Nodes.JsonNode?>
            {
                ["url"] = $"{baseUrl}/v1/media/{name}", ["mime"] = result.Mime,
                ["width"] = result.Width is null ? null : System.Text.Json.Nodes.JsonValue.Create(result.Width.Value),
                ["height"] = result.Height is null ? null : System.Text.Json.Nodes.JsonValue.Create(result.Height.Value),
            };
        });
        ctx.Items["task_id"] = task.Id;
        await ctx.Response.WriteAsJsonAsync(VideoTaskView(task));
    }

    // ---------- admin ----------
    private static void MapAdmin(WebApplication app, AppState s)
    {
        var admin = app.MapGroup("/admin");
        admin.MapGet("/status", (HttpContext ctx) =>
        {
            RequireAdmin(ctx, s);
            return Results.Json(new JsonObject
            {
                ["version"] = Version,
                ["driver"] = JsonSerializer.SerializeToNode(s.Driver.Health().Result),
                ["capabilities"] = JsonSerializer.SerializeToNode(s.Driver.Capabilities),
                ["accounts"] = JsonSerializer.SerializeToNode(s.Pool.Stats()),
            });
        });
        admin.MapGet("/accounts", (HttpContext ctx) =>
        {
            RequireAdmin(ctx, s);
            var arr = new JsonArray();
            foreach (var a in s.Pool.All()) arr.Add(a.Public());
            return Results.Json(new JsonObject { ["data"] = arr });
        });
        admin.MapPost("/accounts", async (HttpContext ctx) =>
        {
            RequireAdmin(ctx, s);
            var body = await JsonHelp.ReadBody(ctx.Request);
            var acc = BuildAccount(body);
            s.Pool.Upsert(acc);
            return Results.Json(new JsonObject
            {
                ["account"] = acc.Public(),
                ["missing_cookies"] = new JsonArray(
                    Upstream.MissingSessionCookies(acc.Cookies).Select(c => (JsonNode?)c).ToArray()),
            });
        });
        admin.MapPost("/accounts/import", async (HttpContext ctx) =>
        {
            RequireAdmin(ctx, s);
            var body = await JsonHelp.ReadBody(ctx.Request);
            var arr = body["accounts"] as JsonArray
                ?? throw MuseException.Invalid("accounts must be a non-empty list");
            var results = new JsonArray();
            for (var i = 0; i < arr.Count; i++)
            {
                try
                {
                    var acc = BuildAccount(arr[i] as JsonObject
                        ?? throw MuseException.Invalid("account must be an object"));
                    s.Pool.Upsert(acc);
                    results.Add(new JsonObject
                    {
                        ["index"] = i, ["ok"] = true, ["account"] = acc.Public(),
                        ["missing_cookies"] = new JsonArray(
                            Upstream.MissingSessionCookies(acc.Cookies).Select(c => (JsonNode?)c).ToArray()),
                    });
                }
                catch (MuseException ex)
                {
                    results.Add(new JsonObject
                        { ["index"] = i, ["ok"] = false, ["error"] = ex.Message, ["code"] = ex.Code });
                }
            }
            return Results.Json(new JsonObject { ["data"] = results });
        });
        admin.MapPatch("/accounts/{id}", async (HttpContext ctx, string id) =>
        {
            RequireAdmin(ctx, s);
            var acc = s.Pool.Get(id) ?? throw MuseException.NotFound($"account '{id}' not found");
            var body = await JsonHelp.ReadBody(ctx.Request);
            if (JsonHelp.Str(body["label"]) is { } label) acc.Label = label;
            if (body["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var enabled)) acc.Enabled = enabled;
            if (body["cookies"] is JsonObject cookies)
            {
                acc.Cookies = cookies.ToDictionary(kv => kv.Key, kv => JsonHelp.Str(kv.Value) ?? "");
                acc.Status = AccountStatus.Active;
                acc.LastError = null;
            }
            if (body["cookie_expires"] is JsonObject expires)
                acc.CookieExpires = expires.ToDictionary(kv => kv.Key, kv => (long)JsonHelp.Int(kv.Value));
            s.Pool.Upsert(acc);
            return Results.Json(new JsonObject { ["account"] = acc.Public() });
        });
        admin.MapDelete("/accounts/{id}", (HttpContext ctx, string id) =>
        {
            RequireAdmin(ctx, s);
            if (!s.Pool.Remove(id)) throw MuseException.NotFound($"account '{id}' not found");
            return Results.Json(new JsonObject { ["deleted"] = id });
        });
        admin.MapPost("/accounts/{id}/reset", (HttpContext ctx, string id) =>
        {
            RequireAdmin(ctx, s);
            var acc = s.Pool.Get(id) ?? throw MuseException.NotFound($"account '{id}' not found");
            acc.Status = AccountStatus.Active;
            acc.CooldownUntil = 0;
            acc.LastError = null;
            acc.Meta.Remove("kind_blocked_until");
            acc.Meta.Remove("wake_blocked_until");
            s.Pool.Upsert(acc);
            return Results.Json(new JsonObject { ["account"] = acc.Public() });
        });
        admin.MapPost("/accounts/{id}/renew", async (HttpContext ctx, string id) =>
        {
            RequireAdmin(ctx, s);
            var acc = s.Pool.Get(id) ?? throw MuseException.NotFound($"account '{id}' not found");
            var info = await s.Driver.RenewSession(acc);
            // Merge, like Python keepalive.renew_account: a renewal that returns
            // only rotated cookies must not wipe the ones it did not return.
            // (The old replace-all code emptied a mock account on renew.)
            foreach (var (k, v) in info.Cookies) acc.Cookies[k] = v;
            foreach (var (k, v) in info.CookieExpires) acc.CookieExpires[k] = v;
            foreach (var (k, v) in info.Meta) acc.Meta[k] = v is null ? null : JsonValue.Create(v);
            s.Pool.NoteWake(acc, info.Meta);
            s.Pool.Upsert(acc);
            return Results.Json(new JsonObject
            {
                ["ok"] = info.Ok,
                ["account"] = acc.Public(),
                ["meta"] = JsonSerializer.SerializeToNode(info.Meta),
            });
        });
        admin.MapGet("/accounts/{id}/quota", async (HttpContext ctx, string id) =>
        {
            RequireAdmin(ctx, s);
            var acc = s.Pool.Get(id) ?? throw MuseException.NotFound($"account '{id}' not found");
            var snapshot = await s.Driver.Quota(acc);
            var quota = new JsonObject { ["checked_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            foreach (var (k, v) in snapshot) quota[k] = JsonSerializer.SerializeToNode(v);
            acc.Meta["quota"] = quota;
            s.Pool.Upsert(acc);
            return Results.Json(new JsonObject { ["account_id"] = acc.Id, ["quota"] = quota });
        });
        admin.MapGet("/extension.zip", (HttpContext ctx) =>
        {
            RequireAdmin(ctx, s);
            throw MuseException.NotFound("the browser extension is not bundled with this server");
        });
        admin.MapGet("/tasks", (HttpContext ctx) =>
        {
            RequireAdmin(ctx, s);
            var limit = int.TryParse(ctx.Request.Query["limit"], out var l) ? l : 100;
            var arr = new JsonArray();
            foreach (var t in s.Tasks.List(limit)) arr.Add(TaskJson(t));
            return Results.Json(new JsonObject { ["data"] = arr });
        });
        admin.MapPost("/tasks/{taskId}/cancel", (HttpContext ctx, string taskId) =>
        {
            RequireAdmin(ctx, s);
            return Results.Json(new JsonObject { ["cancelled"] = s.Tasks.Cancel(taskId) });
        });
        admin.MapGet("/keys", (HttpContext ctx) =>
        {
            RequireAdmin(ctx, s);
            var usage = s.Requests.KeyUsage().ToDictionary(u => u["key_id"]?.ToString() ?? "");
            JsonObject UsageFor(string id) => usage.TryGetValue(id, out var u)
                ? new JsonObject
                {
                    ["total"] = (int)(u["total"] ?? 0), ["requests_24h"] = (int)(u["requests_24h"] ?? 0),
                    ["errors_24h"] = (int)(u["errors_24h"] ?? 0),
                    ["last_request_at"] = u["last_request_at"] is null ? null
                        : JsonValue.Create((double)u["last_request_at"]!),
                }
                : new JsonObject
                    { ["total"] = 0, ["requests_24h"] = 0, ["errors_24h"] = 0, ["last_request_at"] = null };
            var data = new JsonArray();
            foreach (var k in s.Keys.All())
            {
                var pub = k.Public();
                pub["usage"] = UsageFor(k.Id);
                data.Add(pub);
            }
            var builtin = new JsonArray();
            foreach (var b in new[] { "legacy", "admin" })
            {
                var obj = new JsonObject { ["id"] = b, ["name"] = b };
                obj["usage"] = UsageFor(b);
                builtin.Add(obj);
            }
            return Results.Json(new JsonObject { ["data"] = data, ["builtin"] = builtin });
        });
        admin.MapGet("/keys/usage", (HttpContext ctx) =>
        {
            RequireAdmin(ctx, s);
            var arr = new JsonArray();
            foreach (var u in s.Requests.KeyUsage()) arr.Add(JsonSerializer.SerializeToNode(u));
            return Results.Json(new JsonObject
            {
                ["since"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 86400, ["data"] = arr,
            });
        });
        admin.MapPost("/keys", async (HttpContext ctx) =>
        {
            RequireAdmin(ctx, s);
            var body = await JsonHelp.ReadBody(ctx.Request);
            var name = JsonHelp.Str(body["name"]);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
                throw MuseException.Invalid("key name must be 1-100 characters");
            var (key, plaintext) = s.Keys.Create(name.Trim(), JsonHelp.Str(body["note"]) ?? "");
            return Results.Json(new JsonObject { ["key"] = key.Public(), ["api_key"] = plaintext });
        });
        admin.MapPatch("/keys/{keyId}", async (HttpContext ctx, string keyId) =>
        {
            RequireAdmin(ctx, s);
            var key = s.Keys.Get(keyId) ?? throw MuseException.NotFound($"key '{keyId}' not found");
            var body = await JsonHelp.ReadBody(ctx.Request);
            if (JsonHelp.Str(body["name"]) is { } name) key.Name = name.Trim();
            if (JsonHelp.Str(body["note"]) is { } note) key.Note = note;
            if (body["revoked"] is JsonValue rv && rv.TryGetValue<bool>(out var revoked)) key.Revoked = revoked;
            s.Keys.Save();
            return Results.Json(new JsonObject { ["key"] = key.Public() });
        });
        admin.MapDelete("/keys/{keyId}", (HttpContext ctx, string keyId) =>
        {
            RequireAdmin(ctx, s);
            if (!s.Keys.Remove(keyId)) throw MuseException.NotFound($"key '{keyId}' not found");
            return Results.Json(new JsonObject { ["deleted"] = keyId });
        });
        admin.MapGet("/requests", (HttpContext ctx) =>
        {
            RequireAdmin(ctx, s);
            var q = ctx.Request.Query;
            double? since = double.TryParse(q["since"], out var sv) ? sv : null;
            var limit = int.TryParse(q["limit"], out var l) ? Math.Clamp(l, 1, 500) : 100;
            var offset = int.TryParse(q["offset"], out var o) ? o : 0;
            var (rows, total) = s.Requests.Query(
                q["key_id"].ToString() is { Length: > 0 } a ? a : null,
                q["account_id"].ToString() is { Length: > 0 } b ? b : null,
                q["status"].ToString() is { Length: > 0 } c ? c : null,
                q["path"].ToString() is { Length: > 0 } d ? d : null,
                since, q.ContainsKey("hide_polls") && q["hide_polls"] != "false", limit, offset);
            var arr = new JsonArray();
            foreach (var r in rows) arr.Add(new JsonObject
            {
                ["ts"] = r.Ts, ["method"] = r.Method, ["path"] = r.Path,
                ["model"] = r.Model is null ? null : JsonValue.Create(r.Model),
                ["key_id"] = r.KeyId is null ? null : JsonValue.Create(r.KeyId),
                ["key_name"] = r.KeyName is null ? null : JsonValue.Create(r.KeyName),
                ["account_id"] = r.AccountId is null ? null : JsonValue.Create(r.AccountId),
                ["status_code"] = r.StatusCode, ["latency_ms"] = r.LatencyMs,
                ["stream"] = r.Stream, ["poll"] = r.Poll,
                ["error"] = r.Error is null ? null : JsonValue.Create(r.Error),
                ["client_ip"] = r.ClientIp is null ? null : JsonValue.Create(r.ClientIp),
                ["task_id"] = r.TaskId is null ? null : JsonValue.Create(r.TaskId),
            });
            return Results.Json(new JsonObject { ["data"] = arr, ["total"] = total });
        });
        admin.MapGet("/stats", (HttpContext ctx) =>
        {
            RequireAdmin(ctx, s);
            var window = ctx.Request.Query["window"].ToString();
            if (window is not ("1h" or "24h" or "7d")) window = "24h";
            var stats = s.Requests.Stats(window);
            stats["now"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return Results.Json(JsonSerializer.SerializeToNode(stats));
        });
        admin.MapGet("/metrics", (HttpContext ctx) =>
        {
            RequireAdmin(ctx, s);
            var window = ctx.Request.Query["window"].ToString();
            if (window is not ("1h" or "24h" or "7d")) window = "24h";
            return Results.Content(MetricsText(s, window), "text/plain; version=0.0.4");
        });
    }

    private static Account BuildAccount(JsonObject body)
    {
        var (cookies, expires) = ParseCookies(body["cookies"]);
        cookies = cookies.Where(kv => !string.IsNullOrEmpty(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        expires = expires.Where(kv => cookies.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (cookies.Count == 0) throw MuseException.Invalid("cookies must not be empty");
        var acc = new Account
        {
            Label = JsonHelp.Str(body["label"]) ?? "",
            Cookies = cookies, CookieExpires = expires,
            Enabled = body["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var e) ? e : true,
        };
        foreach (var (k, v) in expires.ToList())
            if (body["cookie_expires"] is JsonObject extra && extra[k] is not null)
                acc.CookieExpires[k] = JsonHelp.Int(extra[k]);
        if (body["cookie_expires"] is JsonObject extraAll)
            foreach (var (k, v) in extraAll)
                if (cookies.ContainsKey(k)) acc.CookieExpires[k] = JsonHelp.Int(v);
        return acc;
    }

    private static (Dictionary<string, string>, Dictionary<string, long>) ParseCookies(JsonNode? node)
    {
        if (node is JsonObject obj)
            return (obj.ToDictionary(kv => kv.Key, kv => JsonHelp.Str(kv.Value) ?? ""),
                new Dictionary<string, long>());
        if (node is JsonValue sv && sv.TryGetValue<string>(out var str))
        {
            var cookies = new Dictionary<string, string>();
            foreach (var part in str.Split(';'))
            {
                var p = part.Trim();
                if (p.Length == 0 || !p.Contains('=')) continue;
                var eq = p.IndexOf('=');
                cookies[p[..eq].Trim()] = p[(eq + 1)..].Trim();
            }
            return (cookies, new Dictionary<string, long>());
        }
        if (node is JsonArray arr)
        {
            var cookies = new Dictionary<string, string>();
            var expires = new Dictionary<string, long>();
            for (var i = 0; i < arr.Count; i++)
            {
                if (arr[i] is not JsonObject entry || JsonHelp.Str(entry["name"]) is not { } name)
                    throw MuseException.Invalid($"cookies[{i}] must be an object with 'name' and 'value'");
                cookies[name] = JsonHelp.Str(entry["value"]) ?? "";
                if (entry["expirationDate"] is JsonValue dv && dv.TryGetValue<double>(out var d))
                    expires[name] = (long)d;
            }
            return (cookies, expires);
        }
        throw MuseException.Invalid("cookies must be a dict, a raw 'Cookie' header string, or a cookie-export list");
    }

    private static JsonObject DictToJson(Dictionary<string, JsonNode?>? dict)
    {
        if (dict is null) return null!;
        var obj = new JsonObject();
        foreach (var (k, v) in dict) obj[k] = v?.DeepClone();
        return obj;
    }

    private static JsonObject TaskJson(BgTask t) => new()
    {
        ["id"] = t.Id, ["kind"] = t.Kind, ["status"] = t.Status, ["progress"] = t.Progress,
        ["created_at"] = t.CreatedAt, ["updated_at"] = t.UpdatedAt,
        ["request"] = JsonSerializer.SerializeToNode(t.Request),
        ["result"] = t.Result is null ? null : DictToJson(t.Result),
        ["error"] = t.Error is null ? null : DictToJson(t.Error),
    };

    private static string PromLabel(object? v)
        => (v?.ToString() ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    private static string MetricsText(AppState s, string window)
    {
        var data = s.Requests.Stats(window);
        var pool = s.Pool.Stats();
        var lines = new List<string>
        {
            "# HELP muse2api_info Gateway version info.",
            "# TYPE muse2api_info gauge",
            $"muse2api_info{{version=\"{Version}\"}} 1",
            "# HELP muse2api_requests_window_total Requests in the window (polls excluded).",
            "# TYPE muse2api_requests_window_total gauge",
            $"muse2api_requests_window_total{{window=\"{window}\"}} {data["total"]}",
            "# HELP muse2api_requests_window_errors Failed requests in the window.",
            "# TYPE muse2api_requests_window_errors gauge",
            $"muse2api_requests_window_errors{{window=\"{window}\"}} {data["errors"]}",
            "# HELP muse2api_requests_window_server_errors Requests with HTTP 5xx in the window.",
            "# TYPE muse2api_requests_window_server_errors gauge",
            $"muse2api_requests_window_server_errors{{window=\"{window}\"}} {data["server_errors"]}",
            "# HELP muse2api_requests_window_error_rate Failed requests as a fraction of the window total.",
            "# TYPE muse2api_requests_window_error_rate gauge",
            $"muse2api_requests_window_error_rate{{window=\"{window}\"}} {data["error_rate"]}",
        };
        if (data["latency_ms"] is Dictionary<string, object?> lat)
            foreach (var (name, value) in new[] { ("p50", lat["p50"]), ("p95", lat["p95"]) })
                if (value is not null)
                    lines.Add($"muse2api_request_latency_ms{{window=\"{window}\",quantile=\"{name}\"}} {value}");
        // Stats.By returns a unified row shape; pick the field that matches each group.
        if (data["by_status"] is List<Dictionary<string, object?>> byStatus)
            foreach (var row in byStatus)
                lines.Add($"muse2api_requests_by_status{{window=\"{window}\",status_code=\"{PromLabel(row["status_code"])}\"}} {row["count"]}");
        if (data["by_key"] is List<Dictionary<string, object?>> byKey)
            foreach (var row in byKey)
                lines.Add($"muse2api_requests_by_key{{window=\"{window}\",key_id=\"{PromLabel(row["key_id"])}\",key_name=\"{PromLabel(row["key_name"])}\"}} {row["count"]}");
        if (data["by_account"] is List<Dictionary<string, object?>> byAccount)
            foreach (var row in byAccount)
                lines.Add($"muse2api_requests_by_account{{window=\"{window}\",account_id=\"{PromLabel(row["account_id"])}\"}} {row["count"]}");
        if (data["by_model"] is List<Dictionary<string, object?>> byModel)
            foreach (var row in byModel)
                lines.Add($"muse2api_requests_by_model{{window=\"{window}\",model=\"{PromLabel(row["model"])}\"}} {row["count"]}");
        foreach (var (state, value) in pool)
            lines.Add($"muse2api_accounts{{state=\"{state}\"}} {value}");
        return string.Join("\n", lines) + "\n";
    }

    private const string DashboardHtml = """
        <!doctype html><html lang="en"><head><meta charset="utf-8">
        <title>muse2api (.NET) dashboard</title></head><body>
        <h1>muse2api (.NET port)</h1>
        <p>This is a minimal dashboard for the .NET port. Enter the admin key,
        then use the buttons. The full Python dashboard.html is not ported yet.</p>
        <label>Admin key <input id="key" type="password" size="40"></label>
        <button onclick="show('/admin/status')">Status</button>
        <button onclick="show('/admin/accounts')">Accounts</button>
        <button onclick="show('/admin/stats')">Stats</button>
        <button onclick="show('/admin/metrics')">Metrics</button>
        <pre id="out"></pre>
        <script>
        async function show(path) {
          const key = document.getElementById('key').value;
          const r = await fetch(path, { headers: { Authorization: 'Bearer ' + key } });
          document.getElementById('out').textContent = await r.text();
        }
        </script></body></html>
        """;
}
