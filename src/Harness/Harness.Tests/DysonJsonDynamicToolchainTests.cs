using System.Text.Json;
using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert-only JDSL schema + interpreter (flat reject, flow flags, branches, loops, refs, caps).
/// </summary>
public class DysonJsonDynamicToolchainTests
{
    [Fact]
    public void Run()
    {
        AssertRejectFlatFunctionCall();
        AssertRejectMaxInterationsTypo();
        AssertRejectEntryArgumentsArray();
        AssertSuccessBranchFlowFlags();
        AssertFailureBranchHandled();
        AssertUnhandledFailureIsProgramError();
        AssertFromArgAndFromResultRefs();
        AssertLoopIterationsAndConditionExit();
        AssertSelfCallForbidden();
        AssertUnknownToolFatal();
        AssertNestingDepthCap();
        AssertCatalogRegistration();
        AssertReturnOutputSuccessStopsAndSkipsContinueWith();
        AssertReturnOutputRejectsNonJdslNames();
        AssertReturnOutputMissingOutputIsError();
        AssertReturnOutputDoesNotCountAsNestedMcp();
        AssertReturnOutputTranscriptSlim();
    }

    private static void AssertRejectFlatFunctionCall()
    {
        var parsed = DysonJsonDynamicToolchainSchema.ParseProgram("""
            {
              "Entry": {
                "Actions": {
                  "FunctionCall": "MCP:GetDateTime"
                }
              }
            }
            """);
        if (!parsed.IsError)
            throw new InvalidOperationException("Flat FunctionCall string must fail to parse.");
    }

    private static void AssertRejectMaxInterationsTypo()
    {
        var parsed = DysonJsonDynamicToolchainSchema.ParseProgram("""
            {
              "Entry": {
                "Actions": {
                  "Loop": {
                    "Condition": { "FunctionCall": { "Function": "MCP:GetDateTime" } },
                    "Action": { "FunctionCall": { "Function": "MCP:GetDateTime" } },
                    "MaxInterations": 3
                  }
                }
              }
            }
            """);
        if (!parsed.IsError)
            throw new InvalidOperationException("MaxInterations typo must be rejected.");
    }

    private static void AssertRejectEntryArgumentsArray()
    {
        var parsed = DysonJsonDynamicToolchainSchema.ParseProgram("""
            {
              "Entry": {
                "Arguments": ["a", "b"],
                "Actions": {
                  "FunctionCall": { "Function": "MCP:GetDateTime" }
                }
              }
            }
            """);
        if (!parsed.IsError)
            throw new InvalidOperationException("Entry.Arguments array must fail to parse.");
    }

    private static void AssertSuccessBranchFlowFlags()
    {
        var result = RunToolchain("""
            {
              "program": {
                "Entry": {
                  "Actions": {
                    "FunctionCall": {
                      "Function": "MCP:GetDateTime",
                      "Arguments": { "timezone": "utc" },
                      "OnSuccess": {
                        "FunctionCall": {
                          "Function": "MCP:GetDateTime",
                          "Arguments": { "timezone": "utc" }
                        }
                      },
                      "OnFailure": {
                        "FunctionCall": {
                          "Function": "MCP:GetDateTime",
                          "Arguments": { "timezone": "utc" }
                        }
                      }
                    }
                  }
                }
              }
            }
            """);

        if (result.IsError)
            throw new InvalidOperationException("Success branch program should succeed: " + result.Content);

        using var doc = JsonDocument.Parse(result.Content);
        var flow = doc.RootElement.GetProperty("flow");
        if (flow.GetProperty("executed").GetBoolean() != true
            || flow.GetProperty("branchTaken").GetString() != "OnSuccess"
            || flow.GetProperty("onSuccess").GetProperty("executed").GetBoolean() != true
            || flow.GetProperty("onFailure").GetProperty("executed").GetBoolean() != false)
        {
            throw new InvalidOperationException("Success path must mark OnSuccess taken and OnFailure ignored.");
        }

        if (doc.RootElement.GetProperty("steps").GetArrayLength() != 2)
            throw new InvalidOperationException("Expected 2 nested steps.");
    }

    private static void AssertFailureBranchHandled()
    {
        var result = RunToolchain("""
            {
              "program": {
                "Entry": {
                  "Actions": {
                    "FunctionCall": {
                      "Function": "MCP:WaitForSeconds",
                      "Arguments": { "seconds": 0 },
                      "OnSuccess": {
                        "FunctionCall": { "Function": "MCP:GetDateTime" }
                      },
                      "OnFailure": {
                        "FunctionCall": {
                          "Function": "MCP:GetDateTime",
                          "Arguments": { "timezone": "utc" }
                        }
                      }
                    }
                  }
                }
              }
            }
            """);

        if (result.IsError)
            throw new InvalidOperationException("Handled failure should keep outer IsError false: " + result.Content);

        using var doc = JsonDocument.Parse(result.Content);
        if (doc.RootElement.GetProperty("status").GetString() != "ok")
            throw new InvalidOperationException("Handled failure status should be ok.");

        var flow = doc.RootElement.GetProperty("flow");
        if (flow.GetProperty("isError").GetBoolean() != true
            || flow.GetProperty("branchTaken").GetString() != "OnFailure"
            || flow.GetProperty("onFailure").GetProperty("executed").GetBoolean() != true
            || flow.GetProperty("onSuccess").GetProperty("executed").GetBoolean() != false)
        {
            throw new InvalidOperationException("Failure path flow flags mismatch.");
        }
    }

    private static void AssertUnhandledFailureIsProgramError()
    {
        var result = RunToolchain("""
            {
              "program": {
                "Entry": {
                  "Actions": {
                    "FunctionCall": {
                      "Function": "MCP:WaitForSeconds",
                      "Arguments": { "seconds": 0 }
                    }
                  }
                }
              }
            }
            """);

        if (!result.IsError)
            throw new InvalidOperationException("Unhandled leaf error must set outer IsError.");

        using var doc = JsonDocument.Parse(result.Content);
        if (doc.RootElement.GetProperty("status").GetString() != "error")
            throw new InvalidOperationException("Unhandled failure status should be error.");
    }

    private static void AssertFromArgAndFromResultRefs()
    {
        var result = RunToolchain("""
            {
              "program": {
                "Entry": {
                  "Arguments": { "tz": "utc" },
                  "Actions": {
                    "FunctionCall": {
                      "Function": "GetDateTime",
                      "Arguments": { "timezone": "fromArg:tz" },
                      "OnSuccess": {
                        "FunctionCall": {
                          "Function": "MCP:CompleteTask",
                          "Arguments": { "summary": "fromResult:$0" }
                        }
                      }
                    }
                  }
                }
              }
            }
            """);

        if (result.IsError)
            throw new InvalidOperationException("Ref program should succeed: " + result.Content);

        using var doc = JsonDocument.Parse(result.Content);
        var steps = doc.RootElement.GetProperty("steps");
        if (steps.GetArrayLength() != 2
            || steps[0].GetProperty("tool").GetString() != "GetDateTime"
            || steps[1].GetProperty("tool").GetString() != "CompleteTask"
            || steps[1].GetProperty("isError").GetBoolean())
        {
            throw new InvalidOperationException("fromArg/fromResult chain steps mismatch.");
        }
    }

    private static void AssertLoopIterationsAndConditionExit()
    {
        var loopOk = RunToolchain("""
            {
              "program": {
                "Entry": {
                  "Actions": {
                    "Loop": {
                      "Condition": {
                        "FunctionCall": {
                          "Function": "MCP:GetDateTime",
                          "Arguments": { "timezone": "utc" }
                        }
                      },
                      "Action": {
                        "FunctionCall": {
                          "Function": "MCP:GetDateTime",
                          "Arguments": { "timezone": "utc" }
                        }
                      },
                      "MaxIterations": 2
                    }
                  }
                }
              }
            }
            """);

        if (loopOk.IsError)
            throw new InvalidOperationException("Loop program should succeed: " + loopOk.Content);

        using (var doc = JsonDocument.Parse(loopOk.Content))
        {
            var flow = doc.RootElement.GetProperty("flow");
            if (flow.GetProperty("kind").GetString() != "Loop"
                || flow.GetProperty("iterations").GetInt32() != 2
                || flow.GetProperty("condition").GetProperty("executed").GetBoolean() != true
                || flow.GetProperty("action").GetProperty("executed").GetBoolean() != true)
            {
                throw new InvalidOperationException("Loop iterations/flow flags mismatch.");
            }

            // Condition + Action per iteration ⇒ 4 steps
            if (doc.RootElement.GetProperty("steps").GetArrayLength() != 4)
                throw new InvalidOperationException("Expected 4 steps for 2 loop iterations.");
        }

        var loopExit = RunToolchain("""
            {
              "program": {
                "Entry": {
                  "Actions": {
                    "Loop": {
                      "Condition": {
                        "FunctionCall": {
                          "Function": "MCP:WaitForSeconds",
                          "Arguments": { "seconds": 0 }
                        }
                      },
                      "Action": {
                        "FunctionCall": { "Function": "MCP:GetDateTime" }
                      },
                      "MaxIterations": 5
                    }
                  }
                }
              }
            }
            """);

        if (loopExit.IsError)
            throw new InvalidOperationException("Condition IsError should exit loop normally: " + loopExit.Content);

        using (var doc = JsonDocument.Parse(loopExit.Content))
        {
            var flow = doc.RootElement.GetProperty("flow");
            if (flow.GetProperty("iterations").GetInt32() != 0
                || flow.GetProperty("action").GetProperty("executed").GetBoolean() != false
                || doc.RootElement.GetProperty("status").GetString() != "ok")
            {
                throw new InvalidOperationException("Condition-error loop exit flags mismatch.");
            }
        }
    }

    private static void AssertSelfCallForbidden()
    {
        var result = RunToolchain("""
            {
              "program": {
                "Entry": {
                  "Actions": {
                    "FunctionCall": {
                      "Function": "MCP:JsonDynamicStructuredLanguageToolchain",
                      "Arguments": {
                        "program": {
                          "Entry": {
                            "Actions": {
                              "FunctionCall": { "Function": "MCP:GetDateTime" }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """);

        if (!result.IsError)
            throw new InvalidOperationException("Self-call must fail.");

        using var doc = JsonDocument.Parse(result.Content);
        var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        if (err is null || !err.Contains("Self-call", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Self-call error message missing: " + result.Content);
    }

    private static void AssertUnknownToolFatal()
    {
        var result = RunToolchain("""
            {
              "program": {
                "Entry": {
                  "Actions": {
                    "FunctionCall": { "Function": "MCP:NotARealTool" }
                  }
                }
              }
            }
            """);

        if (!result.IsError)
            throw new InvalidOperationException("Unknown tool must fail.");
    }

    private static void AssertNestingDepthCap()
    {
        // Build OnSuccess chain deeper than MaxActionDepth (8).
        var node = """{ "FunctionCall": { "Function": "MCP:GetDateTime", "Arguments": { "timezone": "utc" } } }""";
        for (var i = 0; i < 10; i++)
        {
            node = $$"""
                {
                  "FunctionCall": {
                    "Function": "MCP:GetDateTime",
                    "Arguments": { "timezone": "utc" },
                    "OnSuccess": {{node}}
                  }
                }
                """;
        }

        var result = RunToolchain($$"""
            { "program": { "Entry": { "Actions": {{node}} } } }
            """);

        if (!result.IsError)
            throw new InvalidOperationException("Depth cap must fail the program.");

        using var doc = JsonDocument.Parse(result.Content);
        var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        if (err is null || !err.Contains("depth", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Depth cap error missing: " + result.Content);
    }

    private static void AssertCatalogRegistration()
    {
        var session = new StubSession();
        session.ConfigureRootForTest();
        if (!session.McpPipeline.Tools.ContainsKey(DysonJsonDynamicToolchainSchema.ToolName))
            throw new InvalidOperationException("Toolchain must be in default catalog.");
    }

    private static void AssertReturnOutputSuccessStopsAndSkipsContinueWith()
    {
        var result = RunToolchain("""
            {
              "program": {
                "Entry": {
                  "Actions": {
                    "FunctionCall": {
                      "Function": "JDSL:ReturnOutput",
                      "Arguments": { "output": "hello-return" },
                      "OnSuccess": {
                        "FunctionCall": {
                          "Function": "MCP:GetDateTime",
                          "Arguments": { "timezone": "utc" }
                        }
                      },
                      "ContinueWith": {
                        "FunctionCall": {
                          "Function": "MCP:GetDateTime",
                          "Arguments": { "timezone": "utc" }
                        }
                      }
                    }
                  }
                }
              }
            }
            """);

        if (result.IsError)
            throw new InvalidOperationException("ReturnOutput should succeed: " + result.Content);

        using var doc = JsonDocument.Parse(result.Content);
        var root = doc.RootElement;
        if (root.GetProperty("status").GetString() != "ok"
            || root.GetProperty("returned").GetBoolean() != true
            || root.GetProperty("finalContent").GetString() != "hello-return")
        {
            throw new InvalidOperationException("ReturnOutput envelope fields mismatch: " + result.Content);
        }

        var flow = root.GetProperty("flow");
        if (flow.GetProperty("executed").GetBoolean() != true
            || flow.GetProperty("function").GetString() != DysonJsonDynamicToolchainSchema.ReturnOutputFunction
            || flow.GetProperty("onSuccess").GetProperty("executed").GetBoolean() != false
            || flow.GetProperty("continueWith").GetProperty("executed").GetBoolean() != false)
        {
            throw new InvalidOperationException("ReturnOutput must skip OnSuccess/ContinueWith: " + result.Content);
        }

        if (root.GetProperty("steps").GetArrayLength() != 1)
            throw new InvalidOperationException("ReturnOutput alone should log one step.");
    }

    private static void AssertReturnOutputRejectsNonJdslNames()
    {
        foreach (var name in new[] { "MCP:ReturnOutput", "ReturnOutput" })
        {
            var result = RunToolchain($$"""
                {
                  "program": {
                    "Entry": {
                      "Actions": {
                        "FunctionCall": {
                          "Function": "{{name}}",
                          "Arguments": { "output": "x" }
                        }
                      }
                    }
                  }
                }
                """);

            if (!result.IsError)
                throw new InvalidOperationException(name + " must fail as unknown tool.");

            using var doc = JsonDocument.Parse(result.Content);
            var err = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            if (err is null || !err.Contains("not in the session catalog", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(name + " should be catalog-miss: " + result.Content);

            if (doc.RootElement.TryGetProperty("returned", out var returned) && returned.GetBoolean())
                throw new InvalidOperationException(name + " must not set returned.");
        }
    }

    private static void AssertReturnOutputMissingOutputIsError()
    {
        var result = RunToolchain("""
            {
              "program": {
                "Entry": {
                  "Actions": {
                    "FunctionCall": {
                      "Function": "JDSL:ReturnOutput",
                      "Arguments": { }
                    }
                  }
                }
              }
            }
            """);

        if (!result.IsError)
            throw new InvalidOperationException("Missing output must fail the program.");

        using var doc = JsonDocument.Parse(result.Content);
        if (doc.RootElement.GetProperty("status").GetString() != "error")
            throw new InvalidOperationException("Missing output status should be error.");

        if (doc.RootElement.TryGetProperty("returned", out var returned) && returned.GetBoolean())
            throw new InvalidOperationException("Failed ReturnOutput must not set returned.");

        var steps = doc.RootElement.GetProperty("steps");
        if (steps.GetArrayLength() != 1
            || steps[0].GetProperty("tool").GetString() != DysonJsonDynamicToolchainSchema.ReturnOutputFunction
            || steps[0].GetProperty("isError").GetBoolean() != true)
        {
            throw new InvalidOperationException("Missing output should log an error step.");
        }
    }

    private static void AssertReturnOutputDoesNotCountAsNestedMcp()
    {
        var nestedCalls = 0;
        var session = new StubSession();
        session.ConfigureRootForTest();
        var parent = new DysonToolCall
        {
            CallId = "jdsl-return-cap",
            ToolName = DysonJsonDynamicToolchainSchema.ToolName,
            Stage = 0,
            ArgumentsJson = "{}",
        };

        var parsed = DysonJsonDynamicToolchainSchema.ParseProgram("""
            {
              "Entry": {
                "Actions": {
                  "FunctionCall": {
                    "Function": "JDSL:ReturnOutput",
                    "Arguments": { "output": 42 }
                  }
                }
              }
            }
            """);
        if (parsed.IsError)
            throw new InvalidOperationException("Parse failed: " + parsed.Error);

        var outcome = DysonJsonDynamicToolchainInterpreter.RunAsync(
                parsed.Value,
                parent,
                session.McpPipeline.Tools,
                (_, _) =>
                {
                    nestedCalls++;
                    return Task.FromResult(new DysonToolCallResult
                    {
                        CallId = "n",
                        ToolName = "x",
                        Stage = 0,
                        Content = "should-not-run",
                    });
                })
            .GetAwaiter()
            .GetResult();

        if (nestedCalls != 0)
            throw new InvalidOperationException("ReturnOutput must not dispatch nested MCP.");

        if (outcome.IsError
            || !outcome.Result.Returned
            || outcome.Result.FinalContent != "42"
            || outcome.Result.Status != "ok")
        {
            throw new InvalidOperationException(
                "ReturnOutput numeric output / flags mismatch: "
                + DysonJsonDynamicToolchainSchema.SerializeResult(outcome.Result));
        }
    }

    private static void AssertReturnOutputTranscriptSlim()
    {
        var result = RunToolchain("""
            {
              "program": {
                "Entry": {
                  "Actions": {
                    "FunctionCall": {
                      "Function": "JDSL:ReturnOutput",
                      "Arguments": { "output": "slim-me" }
                    }
                  }
                }
              }
            }
            """);

        if (result.IsError)
            throw new InvalidOperationException("ReturnOutput for transcript slim should succeed.");

        // Persist/UI Content stays the full envelope.
        using (var doc = JsonDocument.Parse(result.Content))
        {
            if (doc.RootElement.GetProperty("returned").GetBoolean() != true
                || doc.RootElement.GetProperty("finalContent").GetString() != "slim-me"
                || !doc.RootElement.TryGetProperty("flow", out _))
            {
                throw new InvalidOperationException("Envelope must remain full for UI: " + result.Content);
            }
        }

        var modelFacing = DysonJsonDynamicToolchainSchema.TryFormatReturnedToolResultForModel(
            result.ToolName,
            result.Content,
            result.IsError);
        if (modelFacing != "slim-me")
            throw new InvalidOperationException("Transcript slim should equal finalContent, got: " + modelFacing);

        // Non-returned JDSL envelopes must not slim.
        var ordinary = RunToolchain("""
            {
              "program": {
                "Entry": {
                  "Actions": {
                    "FunctionCall": {
                      "Function": "MCP:GetDateTime",
                      "Arguments": { "timezone": "utc" }
                    }
                  }
                }
              }
            }
            """);
        if (ordinary.IsError)
            throw new InvalidOperationException("Ordinary GetDateTime should succeed.");

        if (DysonJsonDynamicToolchainSchema.TryFormatReturnedToolResultForModel(
                ordinary.ToolName,
                ordinary.Content,
                ordinary.IsError) is not null)
        {
            throw new InvalidOperationException("Non-returned JDSL Content must not slim.");
        }
    }

    private static DysonToolCallResult RunToolchain(string argumentsJson)
    {
        var session = new StubSession();
        session.ConfigureRootForTest();
        using var http = new HttpClient();
        var executor = DysonWorkspaceTestFs.CreateExecutor(session, Path.GetTempPath(), http);
        return executor.ExecuteAsync(new DysonToolCall
        {
            CallId = "jdsl1",
            ToolName = DysonJsonDynamicToolchainSchema.ToolName,
            Stage = 0,
            ArgumentsJson = argumentsJson,
        }).GetAwaiter().GetResult();
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void ConfigureRootForTest() => ConfigureRootInterAgentTools();

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
