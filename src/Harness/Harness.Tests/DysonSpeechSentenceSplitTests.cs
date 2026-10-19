using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DysonHarness;

namespace Harness.Tests;

/// <summary>Runs the stdlib <c>DysonSpeechSentences.py</c> with a fake generate. No weights, no torch, no bridge import.</summary>
public class DysonSpeechSentenceSplitTests
{
    [Fact]
    public void Split_sentences_keeps_abbreviations_and_sends_in_order()
    {
        string[] texts =
        [
            "", "   \n", "Hello", "Hello.", "Dr. Smith left. He waved!", "One. Two? Three!", "Wait... Go.",
            "Pi is 3.14 today.", "I went to the U.S. Then I left.", "Hello.\nWorld.", "Hello\nWorld",
        ];
        string[][] expected =
        [
            [], [], ["Hello"], ["Hello."], ["Dr. Smith left.", "He waved!"], ["One.", "Two?", "Three!"], ["Wait...", "Go."],
            ["Pi is 3.14 today."], ["I went to the U.S. Then I left."], ["Hello.", "World."], ["Hello\nWorld"],
        ];
        var output = Run(new JsonObject
        {
            ["split"] = new JsonArray(texts.Select(t => (JsonNode)t).ToArray()),
            ["speak"] = new JsonArray(
                new JsonObject { ["text"] = "One. Two.", ["lowLatency"] = true },
                new JsonObject { ["text"] = "One. Two. Three.", ["lowLatency"] = true, ["failOn"] = "Two." }),
        });

        var split = output["split"]!.AsArray();
        Assert.Equal(expected.Length, split.Count);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], Strings(split[i]));

        AssertSpeak(output["speak"]![0]!, ["One.", "Two."], ["One.", "Two."], ok: true);
        AssertSpeak(output["speak"]![1]!, ["One.", "Two."], ["One."], ok: false);
    }

    [Fact]
    public void Reliable_response_does_not_split_and_missing_setting_is_reliable()
    {
        foreach (var value in new[] { null, "", " ", "reliable", "low latency" })
            Assert.False(DysonSpeechBridge.IsLowLatency(value), value);
        Assert.True(DysonSpeechBridge.IsLowLatency("low-latency"));
        Assert.True(DysonSpeechBridge.IsLowLatency("Low-Latency"));

        var output = Run(new JsonObject
        {
            ["speak"] = new JsonArray(
                new JsonObject { ["text"] = "One. Two.", ["lowLatency"] = false },
                new JsonObject { ["text"] = "One. Two." }),
        });
        AssertSpeak(output["speak"]![0]!, ["One. Two."], ["One. Two."], ok: true);
        AssertSpeak(output["speak"]![1]!, ["One. Two."], ["One. Two."], ok: true);
    }

    private static void AssertSpeak(JsonNode result, string[] calls, string[] sent, bool ok)
    {
        Assert.Equal(calls, Strings(result["calls"]));
        Assert.Equal(sent, Strings(result["sent"]));
        Assert.Equal(ok, result["ok"]!.GetValue<bool>());
    }

    private static string[] Strings(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    private static JsonNode Run(JsonObject request)
    {
        var script = FindScript();
        var failures = new List<string>();
        foreach (var (file, prefix) in new[] { ("python3", ""), ("python", ""), ("py", "-3 ") })
        {
            var psi = new ProcessStartInfo(file, $"{prefix}\"{script}\"")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            try
            {
                using var process = Process.Start(psi)!;
                process.StandardInput.Write(request.ToJsonString());
                process.StandardInput.Close();
                var stderr = process.StandardError.ReadToEndAsync();
                var stdout = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode == 0 && stdout.TrimStart().StartsWith('{'))
                    return JsonNode.Parse(stdout)!;
                failures.Add($"{file}: exit {process.ExitCode} {stderr.Result}");
            }
            catch (Win32Exception ex)
            {
                failures.Add($"{file}: {ex.Message}");
            }
        }

        Assert.Fail("No Python ran DysonSpeechSentences.py:\n" + string.Join("\n", failures));
        return null!;
    }

    private static string FindScript()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Harness", "Harness.Engine", "Speech", "DysonSpeechSentences.py");
            if (File.Exists(candidate))
                return candidate;
        }

        throw new InvalidOperationException("Could not locate DysonSpeechSentences.py from the test output directory.");
    }
}
