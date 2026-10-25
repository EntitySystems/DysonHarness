using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: Responses stateful (direct store+delta) vs stateless (managed replay) + call_id fidelity.
/// </summary>
public class OpenAiResponsesStoreChainTests
{
    [Fact]
    public void Run()
    {
        AssertDirectFullStoresAndOptionalPreviousId();
        AssertDeltaIsOutputsOnlyWithPreviousIdAndInstructions();
        AssertCreateBodyEmitsStoreAndPreviousResponseId();
        AssertManagedStatelessStoreFalseNoPreviousId();
        AssertManagedFullReplayReasoningThenCallThenOutput();
        AssertCreateBodyIncludeEncryptedWhenStoreFalse();
        AssertCallIdNeverUsesItemIdOrGuid();
        AssertParseMergesCallIdNotFcId();
        AssertMissingToolCallErrorDetector();
        AssertSupportsResponsesServerChainingGate();
        AssertParallelToolsPreserveCompletedOutputOrder();
    }

    [Fact]
    public void SystemText_OmitsToolCatalog_ToolsArrayStillAttached()
    {
        var session = new DirectOpenAiSession();
        var system = OpenAiCacheFriendlyTranscriptBuilder.BuildSystemText(session);
        if (system.Contains("## Available MCP tools", StringComparison.Ordinal)
            || system.Contains("Input schema:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("System text must not include the MCP tool catalog.");
        }

        if (!system.Contains("Reply format:", StringComparison.Ordinal)
            || !system.Contains(session.SystemPrompt, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("System text must keep the mode prompt and reply format.");
        }

        var completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(session, "hi", null);
        var responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(session, "hi", null);
        if (!string.Equals(responses.Instructions, system, StringComparison.Ordinal))
            throw new InvalidOperationException("Responses instructions must be BuildSystemText.");

        var systemMessage = completions.Messages[0]?.ToJsonString() ?? "";
        if (!systemMessage.Contains("Reply format:", StringComparison.Ordinal)
            || systemMessage.Contains("## Available MCP tools", StringComparison.Ordinal)
            || systemMessage.Contains("Input schema:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Completions system message must omit the tool catalog.");
        }

        if (!completions.Tools.ToJsonString().Contains("\"name\":\"ReadFile\"", StringComparison.Ordinal)
            || !responses.Tools.ToJsonString().Contains("\"name\":\"ReadFile\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("tools[] must still list catalog tools.");
        }
    }

    [Fact]
    public void Claude_full_replay_shares_prefix_and_ephemeral_breakpoints()
    {
        var provider = MakeProvider("claude-sonnet-4-5", DysonManagedSources.CliProxyClaude);
        AssertClaudePlainUserTextIsStable(provider, responses: true);
        AssertClaudePlainUserTextIsStable(provider, responses: false);
        AssertClaudeHop(provider, responses: true);
        AssertClaudeHop(provider, responses: false);
    }

    [Fact]
    public void Xai_full_replay_shares_sanitized_prefix_without_cache_control()
    {
        var provider = MakeProvider("grok-4.7", DysonManagedSources.XaiGrok);
        var session = new OpenAiProviderSession(provider);
        var codexBlob = "gAAAA" + new string('A', 64);
        var reasoning = new JsonObject
        {
            ["type"] = "reasoning",
            ["id"] = "rs_1",
            ["encrypted_content"] = codexBlob,
            ["summary"] = new JsonArray
            {
                new JsonObject { ["type"] = "summary_text", ["text"] = "plan" },
            },
        };
        var trailingSummary = new JsonObject
        {
            ["type"] = "reasoning",
            ["id"] = "rs_2",
            ["summary"] = new JsonArray
            {
                new JsonObject { ["type"] = "summary_text", ["text"] = "more" },
            },
        };
        var round0 = ToolRound("c1", reasoning: [reasoning, trailingSummary]);
        var round1 = ToolRound("c2");

        var hop1 = OpenAiResponsesClient.BuildCreateBody(
            provider,
            OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                session, null, null, [round0], previousResponseId: "resp_ignored"));
        var hop2 = OpenAiResponsesClient.BuildCreateBody(
            provider,
            OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                session, null, null, [round0, round1], previousResponseId: "resp_ignored"));

        var input1 = (JsonArray)hop1["input"]!;
        AssertJsonPrefix(input1, (JsonArray)hop2["input"]!);
        Assert.Equal(1, input1.Count(n => n is JsonObject o && o["type"]?.GetValue<string>() == "reasoning"));
        var key = hop1["prompt_cache_key"]!.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(key));
        Assert.Equal(key, hop2["prompt_cache_key"]!.GetValue<string>());
        Assert.Equal(
            OpenAiCompatibleHttp.PromptCacheKey(session.PersistenceId, session.SystemPromptGeneration),
            key);
        var json1 = hop1.ToJsonString();
        var json2 = hop2.ToJsonString();
        Assert.DoesNotContain(codexBlob, json1, StringComparison.Ordinal);
        Assert.Contains("plan", json1, StringComparison.Ordinal);
        Assert.Contains("more", json1, StringComparison.Ordinal);
        Assert.DoesNotContain("cache_control", json1, StringComparison.Ordinal);
        Assert.DoesNotContain("cache_control", json2, StringComparison.Ordinal);
        Assert.DoesNotContain(
            OpenAiCacheFriendlyTranscriptBuilder.ClaudeCacheAnchorText,
            json2,
            StringComparison.Ordinal);
        Assert.False(hop1.ContainsKey("prompt_cache_options"));
        Assert.False(hop1.ContainsKey("previous_response_id"));
        Assert.False(hop1["store"]!.GetValue<bool>());
    }

    [Fact]
    public void Custom_and_codex_full_replay_share_prefix_without_claude_markers()
    {
        AssertNoClaudeMarkers(
            completions: true,
            slug: "qwen2.5",
            managedSource: null,
            apiMode: DysonOpenAiApiModes.Completions);
        AssertNoClaudeMarkers(
            completions: false,
            slug: "gpt-5.6-terra",
            managedSource: DysonManagedSources.CliProxyCodex,
            apiMode: DysonOpenAiApiModes.Responses);
        AssertNoClaudeMarkers(
            completions: false,
            slug: "grok-4",
            managedSource: DysonManagedSources.CliProxyGrok,
            apiMode: DysonOpenAiApiModes.Responses);
    }

    [Fact]
    public void Direct_gpt56_keeps_explicit_prompt_cache_without_claude_anchor()
    {
        var provider = MakeProvider("gpt-5.6-sol", managedSource: null);
        var session = new OpenAiProviderSession(provider);
        session.AddTurnForTest(PlainTurn("direct cache"));
        var built = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: [ToolRound("c1"), ToolRound("c2")],
            previousResponseId: "resp_keep");

        Assert.True(built.Store);
        Assert.Equal("resp_keep", built.PreviousResponseId);
        Assert.True(built.IncludeExplicitBreakpoints);
        Assert.True(OpenAiCompatibleHttp.SupportsExplicitPromptCache(provider));
        Assert.True(OpenAiCompatibleHttp.SupportsResponsesServerChaining(provider));

        var body = OpenAiResponsesClient.BuildCreateBody(provider, built);
        Assert.Equal("explicit", body["prompt_cache_options"]!["mode"]!.GetValue<string>());
        Assert.Equal("resp_keep", body["previous_response_id"]!.GetValue<string>());
        Assert.True(body["store"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(body["prompt_cache_key"]!.GetValue<string>()));
        var json = body.ToJsonString();
        Assert.DoesNotContain("cache_control", json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            OpenAiCacheFriendlyTranscriptBuilder.ClaudeCacheAnchorText,
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InFlight_data_url_from_round0_survives_the_next_full_rebuild()
    {
        const string marker = "data:image/png;base64,ROUND0PNGDATA";
        var session = new OpenAiProviderSession(MakeProvider("gpt-4o", managedSource: null));
        var call = new DysonToolCall
        {
            CallId = "img0",
            ToolName = "LoadBinary",
            Stage = 0,
            ArgumentsJson = "{}",
        };
        var result = new DysonToolCallResult
        {
            CallId = "img0",
            ToolName = "LoadBinary",
            Stage = 0,
            Content = """{"fileName":"shot.png"}""",
            BinaryAttachment = new DysonBinaryAttachment
            {
                FileName = "shot.png",
                Extension = ".png",
                MimeType = "image/png",
                Base64Data = "ROUND0PNGDATA",
            },
        };
        var round0 = new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound([call], [result]);
        var round1 = ToolRound("next1");

        var hop1Completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session, null, null, [round0]);
        var hop2Completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session, null, null, [round0, round1]);
        AssertJsonPrefix(hop1Completions.Messages, hop2Completions.Messages);
        Assert.Equal(1, CountOf(hop1Completions.Messages.ToJsonString(), marker));
        Assert.Equal(1, CountOf(hop2Completions.Messages.ToJsonString(), marker));

        var hop1Responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, null, null, [round0]);
        var hop2Responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, null, null, [round0, round1]);
        AssertJsonPrefix(hop1Responses.Input, hop2Responses.Input);
        Assert.Equal(1, CountOf(hop1Responses.Input.ToJsonString(), marker));
        Assert.Equal(1, CountOf(hop2Responses.Input.ToJsonString(), marker));

        var done = new OpenAiProviderSession(MakeProvider("gpt-4o", managedSource: null));
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "look",
            AssistantText = "done",
            StartedUtc = DateTime.UtcNow,
        };
        turn.ToolCalls.Add(call);
        turn.ResponseLog.Enqueue(result);
        turn.ToolRoundCallCounts.Add(1);
        done.AddTurnForTest(turn);

        var doneResponses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(done, null, null);
        var doneCompletions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(done, null, null);
        Assert.DoesNotContain(marker, doneResponses.Input.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(marker, doneCompletions.Messages.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("shot.png", doneResponses.Input.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("shot.png", doneCompletions.Messages.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InFlight_emitted_vision_wire_survives_later_url_and_file_id_writes()
    {
        AssertReplayKeepsFirstWire(
            ImageAttachment(remoteUrl: null, fileId: null),
            mutate: attachment =>
            {
                attachment.RemoteUrl = "https://s3.example.com/dyson/shot.png?X-Amz-Signature=later";
                attachment.FileId = "file-later";
            },
            mustContain: "data:image/png;base64,ROUND0PNGDATA",
            mustNotContain: "X-Amz-Signature=later");

        const string firstUrl = "https://s3.example.com/dyson/shot.png?X-Amz-Signature=first";
        const string resignedUrl = "https://s3.example.com/dyson/shot.png?X-Amz-Signature=resigned";
        AssertReplayKeepsFirstWire(
            ImageAttachment(firstUrl, fileId: null),
            mutate: attachment =>
            {
                attachment.RemoteUrl = resignedUrl;
                attachment.RemoteUrlExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
                attachment.FileId = "file-after-url";
            },
            mustContain: firstUrl,
            mustNotContain: "X-Amz-Signature=resigned");

        AssertReplayKeepsFirstWire(
            ImageAttachment(remoteUrl: null, fileId: "file-vision-1"),
            mutate: attachment =>
            {
                attachment.FileId = "file-vision-2";
                attachment.RemoteUrl = resignedUrl;
            },
            mustContain: "file-vision-1",
            mustNotContain: "file-vision-2",
            responsesOnly: true);

        AssertReplayKeepsFirstWire(
            new DysonBinaryAttachment
            {
                FileName = "dxcompiler.dll",
                Extension = ".dll",
                MimeType = "application/octet-stream",
                Base64Data = "DLLBYTES",
                FileId = "file-dll-1",
            },
            mutate: attachment => attachment.FileId = "file-dll-2",
            mustContain: "file-dll-1",
            mustNotContain: "file-dll-2",
            responsesOnly: true);

        AssertInFlightUserImageKeepsFirstUrl(firstUrl, resignedUrl);
        AssertInFlightUserImageDataUrlStaysDataUrl();
        await AssertCapturedWireSkipsFilesUpload();
    }

    private static void AssertClaudePlainUserTextIsStable(OpenAiCompatibleAgentProvider provider, bool responses)
    {
        var session = new OpenAiProviderSession(provider);
        session.AddTurnForTest(PlainTurn("text only"));
        var round0 = ToolRound("c1");
        var round1 = ToolRound("c2");
        JsonArray earlier;
        JsonArray later;
        if (responses)
        {
            earlier = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                session, null, null, [round0]).Input;
            later = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                session, null, null, [round0, round1]).Input;
        }
        else
        {
            earlier = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session, null, null, [round0]).Messages;
            later = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session, null, null, [round0, round1]).Messages;
        }

        AssertJsonPrefix(earlier, later);
        var message = UserMessageContaining(earlier, "text only");
        var part = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(message["content"])));
        Assert.Equal(responses ? "input_text" : "text", part["type"]!.GetValue<string>());
        Assert.Contains("text only", part["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("ephemeral", part["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal(message.ToJsonString(), UserMessageContaining(later, "text only").ToJsonString());
    }

    private static void AssertClaudeHop(OpenAiCompatibleAgentProvider provider, bool responses)
    {
        var session = new OpenAiProviderSession(provider);
        var turn = PlainTurn("map the cache");
        turn.AddUserImage(new DysonBinaryAttachment
        {
            FileName = "note.png",
            Extension = ".png",
            MimeType = "image/png",
            Base64Data = "NOTEPNG",
        });
        turn.RestoreReasoningLog(
        [
            new DysonReasoningSegment(
                DysonReasoningSegmentKind.UserComment,
                "steer here",
                RoundIndex: 0,
                DeliveredAfterToolCalls: 1),
        ]);
        session.AddTurnForTest(turn);

        var round0 = ToolRound("c1");
        var round1 = ToolRound("c2");
        var hop1 = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, null, null, [round0], previousResponseId: "resp_ignored");
        var hop2 = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, null, null, [round0, round1], previousResponseId: "resp_ignored");
        JsonArray earlier;
        JsonArray later;
        JsonArray tools;
        if (responses)
        {
            var body1 = OpenAiResponsesClient.BuildCreateBody(provider, hop1);
            var body2 = OpenAiResponsesClient.BuildCreateBody(provider, hop2);
            earlier = (JsonArray)body1["input"]!;
            later = (JsonArray)body2["input"]!;
            tools = (JsonArray)body2["tools"]!;
            var key = body1["prompt_cache_key"]!.GetValue<string>();
            Assert.False(string.IsNullOrEmpty(key));
            Assert.Equal(key, body2["prompt_cache_key"]!.GetValue<string>());
            Assert.False(body1.ContainsKey("prompt_cache_options"));
            Assert.False(body2.ContainsKey("previous_response_id"));
            Assert.False(body2["store"]!.GetValue<bool>());
            Assert.False(hop2.IncludeExplicitBreakpoints);
        }
        else
        {
            var first = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session, null, null, [round0]);
            var second = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session, null, null, [round0, round1]);
            earlier = first.Messages;
            later = second.Messages;
            tools = second.Tools;
            Assert.False(string.IsNullOrEmpty(first.PromptCacheKey));
            Assert.Equal(first.PromptCacheKey, second.PromptCacheKey);
            Assert.Equal(
                OpenAiCompatibleHttp.PromptCacheKey(session.PersistenceId, session.SystemPromptGeneration),
                first.PromptCacheKey);
            Assert.False(first.IncludeExplicitBreakpoints);
            Assert.False(second.IncludeExplicitBreakpoints);
        }

        AssertJsonPrefix(earlier, later);
        AssertOnlyLastToolHasEphemeralCache(tools);
        AssertCurrentTurnTextCached(earlier, "map the cache", responses);
        AssertCurrentTurnTextCached(later, "map the cache", responses);

        var anchors1 = IndexesOf(earlier, OpenAiCacheFriendlyTranscriptBuilder.ClaudeCacheAnchorText);
        var anchors2 = IndexesOf(later, OpenAiCacheFriendlyTranscriptBuilder.ClaudeCacheAnchorText);
        Assert.Single(anchors1);
        Assert.Equal(2, anchors2.Count);
        Assert.Equal(anchors1[0], anchors2[0]);
        AssertAnchorPart(earlier[anchors1[0]]!, responses);

        var afterRound0 = LastIndexOf(earlier, responses ? "\"call_id\":\"c1\"" : "\"tool_call_id\":\"c1\"");
        var commentAt = IndexOf(earlier, "steer here");
        Assert.True(afterRound0 >= 0 && anchors1[0] > afterRound0 && commentAt > anchors1[0]);
        var round2At = IndexOf(later, responses ? "\"call_id\":\"c2\"" : "\"id\":\"c2\"");
        Assert.True(round2At > commentAt);

        turn.AssistantText = "done";
        var finished = responses
            ? OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(session, null, null).Input.ToJsonString()
            : OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(session, null, null).Messages.ToJsonString();
        Assert.DoesNotContain(
            OpenAiCacheFriendlyTranscriptBuilder.ClaudeCacheAnchorText,
            finished,
            StringComparison.Ordinal);
        Assert.DoesNotContain("cache_control", finished, StringComparison.Ordinal);
    }

    private static void AssertNoClaudeMarkers(
        bool completions,
        string slug,
        string? managedSource,
        string apiMode)
    {
        var provider = MakeProvider(slug, managedSource, apiMode);
        var session = new OpenAiProviderSession(provider);
        session.AddTurnForTest(PlainTurn("plain prefix"));
        var round0 = ToolRound("c1");
        var round1 = ToolRound("c2");

        if (completions)
        {
            var hop1 = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(session, null, null, [round0]);
            var hop2 = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session, null, null, [round0, round1]);
            AssertJsonPrefix(hop1.Messages, hop2.Messages);
            Assert.False(hop1.IncludeExplicitBreakpoints);
            Assert.False(string.IsNullOrEmpty(hop1.PromptCacheKey));
            Assert.Equal(hop1.PromptCacheKey, hop2.PromptCacheKey);
            Assert.Equal(
                OpenAiCompatibleHttp.PromptCacheKey(session.PersistenceId, session.SystemPromptGeneration),
                hop1.PromptCacheKey);
            var json = hop2.Messages.ToJsonString() + hop2.Tools.ToJsonString();
            Assert.DoesNotContain("cache_control", json, StringComparison.Ordinal);
            Assert.DoesNotContain(
                OpenAiCacheFriendlyTranscriptBuilder.ClaudeCacheAnchorText,
                json,
                StringComparison.Ordinal);
            var user = UserMessageContaining(hop2.Messages, "plain prefix");
            Assert.True(user["content"] is JsonValue);
            Assert.Contains("plain prefix", user["content"]!.GetValue<string>(), StringComparison.Ordinal);
            return;
        }

        var built1 = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, null, null, [round0], previousResponseId: "resp_ignored");
        var built2 = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, null, null, [round0, round1], previousResponseId: "resp_ignored");
        var body1 = OpenAiResponsesClient.BuildCreateBody(provider, built1);
        var body2 = OpenAiResponsesClient.BuildCreateBody(provider, built2);
        AssertJsonPrefix((JsonArray)body1["input"]!, (JsonArray)body2["input"]!);
        Assert.False(built2.Store);
        Assert.Null(built2.PreviousResponseId);
        Assert.False(built2.IncludeExplicitBreakpoints);
        Assert.False(OpenAiCompatibleHttp.SupportsExplicitPromptCache(provider));
        var key = body1["prompt_cache_key"]!.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(key));
        Assert.Equal(key, body2["prompt_cache_key"]!.GetValue<string>());
        Assert.False(body2.ContainsKey("prompt_cache_options"));
        Assert.False(body2.ContainsKey("previous_response_id"));
        var json2 = body2.ToJsonString();
        Assert.DoesNotContain("cache_control", json2, StringComparison.Ordinal);
        Assert.DoesNotContain(
            OpenAiCacheFriendlyTranscriptBuilder.ClaudeCacheAnchorText,
            json2,
            StringComparison.Ordinal);
    }

    private static void AssertJsonPrefix(JsonArray earlier, JsonArray later)
    {
        Assert.True(
            later.Count > earlier.Count,
            $"Expected a longer replay ({earlier.Count} -> {later.Count}).");
        for (var i = 0; i < earlier.Count; i++)
            Assert.Equal(earlier[i]!.ToJsonString(), later[i]!.ToJsonString());
    }

    private static void AssertOnlyLastToolHasEphemeralCache(JsonArray tools)
    {
        Assert.True(tools.Count > 1);
        for (var i = 0; i < tools.Count; i++)
        {
            var tool = Assert.IsType<JsonObject>(tools[i]);
            if (i == tools.Count - 1)
                Assert.Equal("ephemeral", tool["cache_control"]!["type"]!.GetValue<string>());
            else
                Assert.False(tool.ContainsKey("cache_control"));
        }
    }

    private static void AssertCurrentTurnTextCached(JsonArray items, string instruction, bool responses)
    {
        var message = UserMessageContaining(items, instruction);
        var parts = Assert.IsType<JsonArray>(message["content"]);
        JsonObject? text = null;
        var sawImage = false;
        foreach (var part in parts)
        {
            var obj = Assert.IsType<JsonObject>(part);
            var type = obj["type"]?.GetValue<string>();
            if (type is "text" or "input_text")
                text = obj;
            if (type is "image_url" or "input_image")
            {
                sawImage = true;
                Assert.False(obj.ContainsKey("cache_control"));
            }
        }

        Assert.True(sawImage);
        Assert.NotNull(text);
        Assert.Equal(responses ? "input_text" : "text", text!["type"]!.GetValue<string>());
        Assert.Contains(instruction, text["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("ephemeral", text["cache_control"]!["type"]!.GetValue<string>());
    }

    private static void AssertAnchorPart(JsonNode node, bool responses)
    {
        var message = Assert.IsType<JsonObject>(node);
        Assert.Equal("user", message["role"]!.GetValue<string>());
        var part = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(message["content"])[0]);
        Assert.Equal(responses ? "input_text" : "text", part["type"]!.GetValue<string>());
        Assert.Equal(
            OpenAiCacheFriendlyTranscriptBuilder.ClaudeCacheAnchorText,
            part["text"]!.GetValue<string>());
        Assert.Equal("ephemeral", part["cache_control"]!["type"]!.GetValue<string>());
    }

    private static JsonObject UserMessageContaining(JsonArray items, string snippet)
    {
        foreach (var node in items)
        {
            if (node is JsonObject msg
                && msg["role"]?.GetValue<string>() == "user"
                && msg.ToJsonString().Contains(snippet, StringComparison.Ordinal))
            {
                return msg;
            }
        }

        throw new InvalidOperationException($"Missing user message containing '{snippet}'.");
    }

    private static int IndexOf(JsonArray items, string snippet)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i]!.ToJsonString().Contains(snippet, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static int LastIndexOf(JsonArray items, string snippet)
    {
        var found = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i]!.ToJsonString().Contains(snippet, StringComparison.Ordinal))
                found = i;
        }

        return found;
    }

    private static List<int> IndexesOf(JsonArray items, string snippet)
    {
        var found = new List<int>();
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i]!.ToJsonString().Contains(snippet, StringComparison.Ordinal))
                found.Add(i);
        }

        return found;
    }

    private static int CountOf(string json, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = json.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static void AssertReplayKeepsFirstWire(
        DysonBinaryAttachment attachment,
        Action<DysonBinaryAttachment> mutate,
        string mustContain,
        string mustNotContain,
        bool responsesOnly = false)
    {
        var session = new OpenAiProviderSession(MakeProvider("gpt-4o", null));
        var round0 = RoundWithAttachment(attachment, "img0");
        var round1 = ToolRound("next1");

        OpenAiCacheFriendlyTranscriptBuilder.BuiltCompletionsRequest? hop1Completions = null;
        if (!responsesOnly)
        {
            hop1Completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session, null, null, [round0]);
        }

        var hop1Responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, null, null, [round0]);
        mutate(attachment);

        if (hop1Completions is not null)
        {
            var hop2Completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session, null, null, [round0, round1]);
            AssertJsonPrefix(hop1Completions.Messages, hop2Completions.Messages);
            AssertWire(hop2Completions.Messages.ToJsonString(), mustContain, mustNotContain, "Completions");
        }

        var hop2Responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, null, null, [round0, round1]);
        AssertJsonPrefix(hop1Responses.Input, hop2Responses.Input);
        AssertWire(hop2Responses.Input.ToJsonString(), mustContain, mustNotContain, "Responses");
    }

    private static void AssertInFlightUserImageKeepsFirstUrl(string firstUrl, string resignedUrl)
    {
        var image = ImageAttachment(firstUrl, fileId: null);
        var session = new OpenAiProviderSession(MakeProvider("gpt-4o", null));
        var turn = PlainTurn("see the shot");
        turn.AddUserImage(image);
        session.AddTurnForTest(turn);
        var round0 = ToolRound("c1");
        var round1 = ToolRound("c2");

        var hop1Completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session, null, null, [round0]);
        var hop1Responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, null, null, [round0]);
        image.RemoteUrl = resignedUrl;
        image.FileId = "file-user-later";

        var hop2Completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session, null, null, [round0, round1]);
        var hop2Responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, null, null, [round0, round1]);
        AssertJsonPrefix(hop1Completions.Messages, hop2Completions.Messages);
        AssertJsonPrefix(hop1Responses.Input, hop2Responses.Input);
        AssertWire(hop2Completions.Messages.ToJsonString(), firstUrl, resignedUrl, "Completions user image");
        AssertWire(hop2Responses.Input.ToJsonString(), firstUrl, resignedUrl, "Responses user image");
        if (!hop2Completions.Messages.ToJsonString().Contains("- shot.png " + firstUrl, StringComparison.Ordinal)
            || !hop2Responses.Input.ToJsonString().Contains("- shot.png " + firstUrl, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Attached urls must keep the URL from the first in-flight emission.");
        }
    }

    private static void AssertInFlightUserImageDataUrlStaysDataUrl()
    {
        var image = ImageAttachment(remoteUrl: null, fileId: null);
        var session = new OpenAiProviderSession(MakeProvider("gpt-4o", null));
        var turn = PlainTurn("see the shot");
        turn.AddUserImage(image);
        session.AddTurnForTest(turn);
        var round0 = ToolRound("c1");
        var round1 = ToolRound("c2");
        var hop1Completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session, null, null, [round0]);
        var hop1Responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, null, null, [round0]);
        image.RemoteUrl = "https://s3.example.com/dyson/shot.png?X-Amz-Signature=later";
        image.FileId = "file-user-later";
        var hop2Completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session, null, null, [round0, round1]);
        var hop2Responses = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session, null, null, [round0, round1]);
        AssertJsonPrefix(hop1Completions.Messages, hop2Completions.Messages);
        AssertJsonPrefix(hop1Responses.Input, hop2Responses.Input);
        const string marker = "data:image/png;base64,ROUND0PNGDATA";
        AssertWire(hop2Completions.Messages.ToJsonString(), marker, "X-Amz-Signature=later", "Completions user data");
        AssertWire(hop2Responses.Input.ToJsonString(), marker, "file-user-later", "Responses user data");
        if (hop2Completions.Messages.ToJsonString().Contains("Attached urls:", StringComparison.Ordinal)
            || hop2Responses.Input.ToJsonString().Contains("Attached urls:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Attached urls must not appear after the first emission omitted them.");
        }
    }

    private static async Task AssertCapturedWireSkipsFilesUpload()
    {
        var attachment = new DysonBinaryAttachment
        {
            FileName = "shot.png",
            Extension = ".png",
            MimeType = "image/png",
            Base64Data = Convert.ToBase64String([1, 2, 3, 4]),
        };
        attachment.CaptureInFlightWire();
        var handler = new RecordingFilesHandler();
        using var http = new HttpClient(handler);
        await OpenAiFilesClient.EnsureBinaryFileIdsAsync(
            http,
            MakeProvider("gpt-4o", null),
            [attachment]);
        if (handler.Calls != 0 || !string.IsNullOrEmpty(attachment.FileId))
        {
            throw new InvalidOperationException(
                "Files upload must not replace a wire that was already emitted as a data URL.");
        }
    }

    private static void AssertWire(string json, string mustContain, string mustNotContain, string label)
    {
        if (!json.Contains(mustContain, StringComparison.Ordinal)
            || json.Contains(mustNotContain, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label} replay changed an already-emitted vision wire.");
        }
    }

    private static DysonBinaryAttachment ImageAttachment(string? remoteUrl, string? fileId) =>
        new()
        {
            FileName = "shot.png",
            Extension = ".png",
            MimeType = "image/png",
            Base64Data = "ROUND0PNGDATA",
            RemoteUrl = remoteUrl,
            FileId = fileId,
        };

    private static OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound RoundWithAttachment(
        DysonBinaryAttachment attachment,
        string callId) =>
        new(
            [
                new DysonToolCall
                {
                    CallId = callId,
                    ToolName = "LoadBinary",
                    Stage = 0,
                    ArgumentsJson = "{}",
                },
            ],
            [
                new DysonToolCallResult
                {
                    CallId = callId,
                    ToolName = "LoadBinary",
                    Stage = 0,
                    Content = """{"fileName":"shot.png"}""",
                    BinaryAttachment = attachment,
                },
            ]);

    private sealed class RecordingFilesHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"file-should-not-be-used"}"""),
            });
        }
    }

    private static OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound ToolRound(
        string callId,
        string toolName = "Grep",
        IReadOnlyList<JsonObject>? reasoning = null) =>
        new(
            [
                new DysonToolCall
                {
                    CallId = callId,
                    ToolName = toolName,
                    Stage = 0,
                    ArgumentsJson = "{}",
                },
            ],
            [
                new DysonToolCallResult
                {
                    CallId = callId,
                    ToolName = toolName,
                    Stage = 0,
                    Content = "ok",
                },
            ],
            reasoning);

    private static DysonAgentTurn PlainTurn(string instruction) => new()
    {
        Kind = DysonAgentTurnKind.Normal,
        Instruction = instruction,
        StartedUtc = DateTime.UtcNow,
    };

    private static void AssertDirectFullStoresAndOptionalPreviousId()
    {
        var session = new DirectOpenAiSession();

        var round0 = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: "hello",
            currentFilePaths: null,
            inFlightRounds: []);
        if (!round0.Store)
            throw new InvalidOperationException("Direct BuildResponsesFull must set Store=true for chaining.");
        if (round0.PreviousResponseId is not null)
            throw new InvalidOperationException("Round-0 full rebuild must omit previous_response_id.");

        const string priorId = "resp_prior_abc";
        var midLoop = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: "harness follow-up",
            currentFilePaths: null,
            inFlightRounds: [],
            previousResponseId: priorId);
        if (!midLoop.Store)
            throw new InvalidOperationException("Mid-loop full rebuild must keep Store=true.");
        if (!string.Equals(midLoop.PreviousResponseId, priorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected PreviousResponseId '{priorId}', got '{midLoop.PreviousResponseId ?? "null"}'.");
        }
    }

    private static void AssertDeltaIsOutputsOnlyWithPreviousIdAndInstructions()
    {
        var session = new DirectOpenAiSession();
        const string priorId = "resp_tool_hop";
        var results = new List<DysonToolCallResult>
        {
            new()
            {
                CallId = "call_xyz",
                ToolName = "GetDateTime",
                Stage = 0,
                Content = """{"utc":"2026-01-01T00:00:00Z"}""",
            },
        };

        var delta = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesDelta(
            session,
            priorId,
            results);

        if (!delta.Store)
            throw new InvalidOperationException("BuildResponsesDelta must set Store=true.");
        if (!string.Equals(delta.PreviousResponseId, priorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Delta PreviousResponseId mismatch: '{delta.PreviousResponseId ?? "null"}'.");
        }

        if (string.IsNullOrWhiteSpace(delta.Instructions))
            throw new InvalidOperationException("Delta must resend instructions (previous_response_id does not carry them).");

        foreach (var node in delta.Input)
        {
            if (node is not JsonObject obj)
                throw new InvalidOperationException("Delta input items must be objects.");
            var type = obj["type"]?.GetValue<string>();
            if (!string.Equals(type, "function_call_output", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Delta input must be outputs-only; got type '{type ?? "null"}'.");
            }
        }

        if (delta.Input.Count != 1)
            throw new InvalidOperationException($"Expected 1 function_call_output, got {delta.Input.Count}.");
    }

    private static void AssertCreateBodyEmitsStoreAndPreviousResponseId()
    {
        var provider = MakeProvider("gpt-5.6-sol", managedSource: null);
        var session = new OpenAiProviderSession(provider);
        const string priorId = "resp_create_body";

        var delta = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesDelta(
            session,
            priorId,
            [
                new DysonToolCallResult
                {
                    CallId = "call_1",
                    ToolName = "GetDateTime",
                    Stage = 0,
                    Content = "ok",
                },
            ]);

        var body = OpenAiResponsesClient.BuildCreateBody(provider, delta);
        if (body["store"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("CreateBody must emit store:true for tool-loop hop.");
        if (!string.Equals(body["previous_response_id"]?.GetValue<string>(), priorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "CreateBody must emit previous_response_id on tool-loop hop.");
        }

        if (string.IsNullOrWhiteSpace(body["instructions"]?.GetValue<string>()))
            throw new InvalidOperationException("CreateBody delta hop must include instructions.");

        var input = body["input"] as JsonArray
            ?? throw new InvalidOperationException("CreateBody missing input array.");
        foreach (var node in input)
        {
            if (node is not JsonObject obj
                || !string.Equals(obj["type"]?.GetValue<string>(), "function_call_output", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "CreateBody tool-loop hop input must remain outputs-only.");
            }
        }
    }

    private static void AssertManagedStatelessStoreFalseNoPreviousId()
    {
        var provider = MakeProvider("gpt-5.6-terra", DysonManagedSources.CliProxyCodex);
        var session = new OpenAiProviderSession(provider);

        if (OpenAiCompatibleHttp.SupportsResponsesServerChaining(provider))
            throw new InvalidOperationException("Managed provider must not support Responses server chaining.");

        var built = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: "hello",
            currentFilePaths: null,
            inFlightRounds: [],
            previousResponseId: "resp_should_be_ignored");

        if (built.Store)
            throw new InvalidOperationException("Managed BuildResponsesFull must set Store=false.");
        if (built.PreviousResponseId is not null)
            throw new InvalidOperationException("Managed full rebuild must never emit previous_response_id.");

        var body = OpenAiResponsesClient.BuildCreateBody(provider, built);
        if (body["store"]?.GetValue<bool>() != false)
            throw new InvalidOperationException("Managed CreateBody must emit store:false.");
        if (body.ContainsKey("previous_response_id"))
            throw new InvalidOperationException("Managed CreateBody must omit previous_response_id.");
    }

    private static void AssertManagedFullReplayReasoningThenCallThenOutput()
    {
        var provider = MakeProvider("gpt-5.6-terra", DysonManagedSources.CliProxyCodex);
        var session = new OpenAiProviderSession(provider);

        var reasoning = new JsonObject
        {
            ["type"] = "reasoning",
            ["id"] = "rs_1",
            ["encrypted_content"] = "enc-blob",
            ["summary"] = new JsonArray
            {
                new JsonObject { ["type"] = "summary_text", ["text"] = "plan" },
            },
        };

        var round = new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound(
            [
                new DysonToolCall
                {
                    CallId = "call_abc",
                    ToolName = "GetDateTime",
                    Stage = 0,
                    ArgumentsJson = """{"timezone":"utc"}""",
                },
            ],
            [
                new DysonToolCallResult
                {
                    CallId = "call_abc",
                    ToolName = "GetDateTime",
                    Stage = 0,
                    Content = "ok",
                },
            ],
            [reasoning]);

        var built = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: [round]);

        // Skip history user message for incomplete current turn — only in-flight items.
        var types = new List<string>();
        foreach (var node in built.Input)
        {
            if (node is JsonObject obj && obj["type"] is JsonValue)
                types.Add(obj["type"]!.GetValue<string>());
        }

        if (types is not ["reasoning", "function_call", "function_call_output"])
        {
            throw new InvalidOperationException(
                $"Expected reasoning→function_call→function_call_output, got [{string.Join(", ", types)}].");
        }

        if (built.Input[0] is not JsonObject first
            || first["encrypted_content"]?.GetValue<string>() != "enc-blob")
        {
            throw new InvalidOperationException("Replayed reasoning item must keep encrypted_content.");
        }

        if (built.Input[1] is not JsonObject call
            || call["call_id"]?.GetValue<string>() != "call_abc")
        {
            throw new InvalidOperationException("function_call must use call_* call_id.");
        }

        if (built.Input[2] is not JsonObject output
            || output["call_id"]?.GetValue<string>() != "call_abc")
        {
            throw new InvalidOperationException("function_call_output must use call_* call_id.");
        }
    }

    private static void AssertCreateBodyIncludeEncryptedWhenStoreFalse()
    {
        var provider = MakeProvider("gpt-5.6-sol", DysonManagedSources.CliProxyCodex);
        var built = new OpenAiCacheFriendlyTranscriptBuilder.BuiltResponsesRequest(
            Instructions: "sys",
            Input: [],
            Tools: [],
            PromptCacheKey: "dyson:test",
            IncludeExplicitBreakpoints: false,
            PreviousResponseId: null,
            Store: false);

        var body = OpenAiResponsesClient.BuildCreateBody(provider, built);
        var include = body["include"] as JsonArray
            ?? throw new InvalidOperationException("store:false body must include[] array.");

        var found = include.Any(n =>
            n is JsonValue v
            && v.TryGetValue<string>(out var s)
            && string.Equals(s, "reasoning.encrypted_content", StringComparison.Ordinal));
        if (!found)
            throw new InvalidOperationException("store:false must request reasoning.encrypted_content.");
    }

    private static void AssertCallIdNeverUsesItemIdOrGuid()
    {
        if (OpenAiCompatibleHttp.IsUsableResponsesCallId("call_abc123"))
        {
            // ok
        }
        else
        {
            throw new InvalidOperationException("call_* must be usable.");
        }

        if (OpenAiCompatibleHttp.IsUsableResponsesCallId("fc_itemOnly"))
            throw new InvalidOperationException("fc_* item id must not be usable as call_id.");

        if (OpenAiCompatibleHttp.IsUsableResponsesCallId(Guid.NewGuid().ToString("N")))
            throw new InvalidOperationException("Guid must not be usable as call_id.");

        if (OpenAiCompatibleHttp.IsUsableResponsesCallId(null)
            || OpenAiCompatibleHttp.IsUsableResponsesCallId(""))
        {
            throw new InvalidOperationException("Empty call_id must not be usable.");
        }
    }

    private static void AssertParseMergesCallIdNotFcId()
    {
        var json = JsonNode.Parse("""
            {
              "id": "resp_parse",
              "output": [
                {
                  "type": "reasoning",
                  "id": "rs_1",
                  "encrypted_content": "blob",
                  "summary": [{ "type": "summary_text", "text": "think" }]
                },
                {
                  "type": "function_call",
                  "id": "fc_should_not_leak",
                  "call_id": "call_real_id",
                  "name": "GetDateTime",
                  "arguments": "{\"timezone\":\"utc\",\"stage\":0}"
                },
                {
                  "type": "function_call",
                  "id": "fc_orphan_no_call_id",
                  "name": "ListDirectory",
                  "arguments": "{}"
                }
              ]
            }
            """) as JsonObject
            ?? throw new InvalidOperationException("Expected fixture JsonObject.");

        var parsed = OpenAiResponsesClient.Parse(json);
        if (parsed.IsError)
            throw new InvalidOperationException($"Parse failed: {parsed.Error}");

        if (parsed.Value.ToolCalls.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 tool call (skip fc-only), got {parsed.Value.ToolCalls.Count}.");
        }

        if (!string.Equals(parsed.Value.ToolCalls[0].CallId, "call_real_id", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected call_real_id, got '{parsed.Value.ToolCalls[0].CallId}'.");
        }

        if (parsed.Value.ReasoningOutputItems.Count != 1)
            throw new InvalidOperationException("Parse must capture raw reasoning output items.");

        if (parsed.Value.ReasoningOutputItems[0]["encrypted_content"]?.GetValue<string>() != "blob")
            throw new InvalidOperationException("Raw reasoning item must retain encrypted_content.");
    }

    private static void AssertMissingToolCallErrorDetector()
    {
        const string sample =
            "OpenAI API 400 Bad Request: {\"error\":{\"message\":\"No tool call found for function call output with call_id call_JtAobStc8e1Wdttwx8ucidah.\"}}";
        if (!OpenAiCompatibleHttp.IsMissingToolCallForOutputError(sample))
            throw new InvalidOperationException("Detector must match the known 400 message.");

        if (OpenAiCompatibleHttp.IsMissingToolCallForOutputError("OpenAI API 400 unrelated"))
            throw new InvalidOperationException("Detector must not match unrelated 400s.");
    }

    private static void AssertSupportsResponsesServerChainingGate()
    {
        var direct = MakeProvider("gpt-5.6-sol", managedSource: null);
        var managed = MakeProvider("gpt-5.6-sol", DysonManagedSources.CliProxyCodex);
        if (!OpenAiCompatibleHttp.SupportsResponsesServerChaining(direct))
            throw new InvalidOperationException("Direct provider must support Responses chaining.");
        if (OpenAiCompatibleHttp.SupportsResponsesServerChaining(managed))
            throw new InvalidOperationException("Managed provider must not support Responses chaining.");
    }

    private static void AssertParallelToolsPreserveCompletedOutputOrder()
    {
        var json = JsonNode.Parse("""
            {
              "id": "resp_parallel",
              "output": [
                {
                  "type": "function_call",
                  "id": "fc_b",
                  "call_id": "call_second",
                  "name": "ReadFile",
                  "arguments": "{\"path\":\"b.txt\",\"stage\":0}"
                },
                {
                  "type": "function_call",
                  "id": "fc_a",
                  "call_id": "call_first",
                  "name": "ReadFile",
                  "arguments": "{\"path\":\"a.txt\",\"stage\":0}"
                }
              ]
            }
            """) as JsonObject
            ?? throw new InvalidOperationException("Expected parallel fixture.");

        var parsed = OpenAiResponsesClient.Parse(json);
        if (parsed.IsError)
            throw new InvalidOperationException(parsed.Error);

        if (parsed.Value.ToolCalls.Count != 2)
            throw new InvalidOperationException("Expected 2 parallel tool calls.");

        if (parsed.Value.ToolCalls[0].CallId != "call_second"
            || parsed.Value.ToolCalls[1].CallId != "call_first")
        {
            throw new InvalidOperationException(
                "Tool calls must preserve response.output order (not dict reorder).");
        }
    }

    private static OpenAiCompatibleAgentProvider MakeProvider(
        string slug,
        string? managedSource,
        string? openAiApiMode = null)
    {
        var entity = new DysonModelProviderEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = managedSource is null ? "Direct OpenAI" : "Managed Codex",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = managedSource is null ? "https://api.openai.com/v1" : "http://127.0.0.1:8317/v1",
            OpenAiApiMode = openAiApiMode ?? DysonOpenAiApiModes.Responses,
            ManagedSource = managedSource,
        };
        var slugEntity = new DysonModelSlugEntity
        {
            Id = Guid.NewGuid(),
            ProviderId = entity.Id,
            Slug = slug,
            DisplayAlias = slug,
            Provider = entity,
        };
        return new OpenAiCompatibleAgentProvider(entity, slugEntity);
    }

    private sealed class DirectOpenAiSession() : OpenAiProviderSession(MakeProvider("gpt-5.6-sol", null));

    private class OpenAiProviderSession : DysonAgentSession
    {
        public OpenAiProviderSession(OpenAiCompatibleAgentProvider provider)
            : base(DysonAgentModes.Work, new DysonAgentSessionConfig(), provider)
        {
        }

        public void AddTurnForTest(DysonAgentTurn turn) => AddTurn(turn);

        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
            IReadOnlyList<string>? contextFiles = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> LoadFunctionalContextAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
