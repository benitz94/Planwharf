// Ports the checks of engine/activity-evidence.test.cjs that do not use activity-sessions.cjs
// (source-whitelisted technical excerpts, provenance, redaction, ANSI/OSC stripping,
// size limits and truncation markers), plus edge cases checked against JavaScript.
//
// Deferred: the session paging tests (lines 53-83 of activity-evidence.test.cjs) depend on
// engine/activity-sessions.cjs, which is not ported yet.
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ActivityEvidenceTests
{
    [Fact]
    public void Codex_command_execution_extracts_whitelisted_fields_only()
    {
        var codex = new JsonObject { ["adapter"] = "codex" };
        var row = new JsonObject
        {
            ["type"] = "item.completed",
            ["timestamp"] = "2026-10-16T10:00:00Z",
            ["item"] = new JsonObject
            {
                ["type"] = "command_execution",
                ["command"] = "node --test\n# original command",
                ["aggregated_output"] = "TAP version 13\nok 1 - fixture\n# pass 1\n",
                ["exit_code"] = 0,
                ["CodeContent"] = "NEVER-COPY-CODE"
            }
        };

        var technical = ActivityEvidence.TechnicalEvidence(row.ToJsonString(), codex);
        var entries = technical["entries"]!.AsArray();
        Assert.Single(entries);
        var first = entries[0]!.AsObject();

        Assert.Equal("node --test\n# original command", first["command"]!.GetValue<string>());
        Assert.Equal("TAP version 13\nok 1 - fixture\n# pass 1\n", first["output"]!.GetValue<string>());
        Assert.Equal(0, first["exitCode"]!.GetValue<int>());
        Assert.Equal("2026-10-16T10:00:00Z", first["sourceTimestamp"]!.GetValue<string>());
        Assert.Equal("observed", first["provenance"]!.GetValue<string>());
        Assert.False(first.ContainsKey("validated"));

        var json = technical.ToJsonString();
        Assert.DoesNotContain("NEVER-COPY-CODE", json);
        Assert.DoesNotContain("Running tests", json);

        Assert.Empty(ActivityEvidence.TechnicalEvidence(row.ToJsonString(), new JsonObject { ["adapter"] = "antigravity" })["entries"]!.AsArray());
        Assert.Empty(ActivityEvidence.TechnicalEvidence(row.ToJsonString(), new JsonObject { ["adapter"] = "grok" })["entries"]!.AsArray());
        Assert.Empty(ActivityEvidence.TechnicalEvidence(row.ToJsonString(), new JsonObject { ["adapter"] = "claude" })["entries"]!.AsArray());
    }

    [Fact]
    public void Claude_and_antigravity_structured_events()
    {
        var claudeTool = JsonNode.Parse("""
        {
            "type": "assistant",
            "message": {
                "content": [{
                    "type": "tool_use",
                    "name": "Bash",
                    "input": { "command": "node --test --token SECRET-FLAG", "content": "PRIVATE-SOURCE" }
                }]
            }
        }
        """)!.AsObject();

        var claudeEvidence = ActivityEvidence.TechnicalEvidence(claudeTool.ToJsonString(), new JsonObject { ["adapter"] = "claude" });
        var claudeEntries = claudeEvidence["entries"]!.AsArray();
        Assert.Single(claudeEntries);
        Assert.Equal("Bash", claudeEntries[0]!["tool"]!.GetValue<string>());
        Assert.Contains("node --test", claudeEntries[0]!["command"]!.GetValue<string>());
        Assert.Equal("observed", claudeEntries[0]!["provenance"]!.GetValue<string>());

        var claudeJson = claudeEvidence.ToJsonString();
        Assert.DoesNotContain("SECRET-FLAG", claudeJson);
        Assert.DoesNotContain("PRIVATE-SOURCE", claudeJson);
        Assert.DoesNotContain("Adapter identity is unknown", claudeJson);

        var agy = JsonNode.Parse("""
        {
            "event": "step_update",
            "step_update": {
                "step_type": "tool",
                "state": "DONE",
                "tool_name": "run_command",
                "tool_info": {
                    "parameters": { "CommandLine": "npm test --token SECRET-FLAG", "CodeContent": "PRIVATE-SOURCE" },
                    "result": { "output": "UNVERIFIED-RESULT" }
                }
            }
        }
        """)!.AsObject();

        var agyEvidence = ActivityEvidence.TechnicalEvidence(agy.ToJsonString(), new JsonObject { ["adapter"] = "antigravity" });
        var agyEntries = agyEvidence["entries"]!.AsArray();
        Assert.Single(agyEntries);
        Assert.Equal("DONE", agyEntries[0]!["state"]!.GetValue<string>());
        Assert.Contains("npm test", agyEntries[0]!["command"]!.GetValue<string>());

        var agyJson = agyEvidence.ToJsonString();
        Assert.DoesNotContain("SECRET-FLAG", agyJson);
        Assert.DoesNotContain("PRIVATE-SOURCE", agyJson);
        Assert.DoesNotContain("UNVERIFIED-RESULT", agyJson);
    }

    [Fact]
    public void Redaction_removes_secrets_private_keys_and_ansi_escapes()
    {
        var sensitive = "api_key=VALUE-1\npassword=\"VALUE 2\"\nAuthorization: Bearer VALUE-3\nhttps://user:VALUE-4@example.invalid?token=VALUE-5\n-----BEGIN PRIVATE KEY-----\nVALUE-6\n-----END PRIVATE KEY-----\n<script>unsafe</script>\x1b[31m\x1b]0;secret title\x07";
        var sanitized = ActivityEvidence.Redact(sensitive);

        Assert.DoesNotContain("VALUE", sanitized);
        Assert.DoesNotContain("<script>", sanitized);
        Assert.DoesNotContain("\x1b", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret title", sanitized);
        Assert.Contains("\n", sanitized);
    }

    [Fact]
    public void Plain_text_and_size_limits()
    {
        var codex = new JsonObject { ["adapter"] = "codex" };
        var plain = ActivityEvidence.TechnicalEvidence("compiler diagnostic line 1\nline 2\n5 passed\n", codex);
        var entries = plain["entries"]!.AsArray();
        Assert.Single(entries);
        Assert.Equal("compiler diagnostic line 1\nline 2\n5 passed\n", entries[0]!["output"]!.GetValue<string>());
        Assert.Equal("reported", entries[0]!["provenance"]!.GetValue<string>());
        Assert.Equal("plain", entries[0]!["protocol"]!.GetValue<string>());

        var huge = ActivityEvidence.TechnicalEvidence(new JsonObject
        {
            ["type"] = "item.completed",
            ["item"] = new JsonObject
            {
                ["type"] = "command_execution",
                ["aggregated_output"] = string.Concat(Enumerable.Repeat("diagnostic\n", 800))
            }
        }.ToJsonString(), codex);

        Assert.True(huge["truncated"]!.GetValue<bool>());
        Assert.Contains("excerpt truncated", huge["entries"]!.AsArray()[0]!["output"]!.GetValue<string>());
    }

    [Fact]
    public void Structured_lines_that_are_not_objects_are_omitted_and_secrets_stay_redacted()
    {
        var codex = new JsonObject { ["adapter"] = "codex" };
        var mixed = ActivityEvidence.TechnicalEvidence("401\nplain text\n[1,2]\nnull\n{\"item\":\nhello", codex);
        Assert.Equal(["plain text", "hello"], mixed["entries"]!.AsArray().Select(node => node!["output"]!.GetValue<string>()));
        var warnings = mixed["warnings"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        Assert.Contains("Unknown structured evidence was omitted.", warnings);
        Assert.Contains("Malformed structured evidence was omitted.", warnings);
        Assert.DoesNotContain("401", mixed.ToJsonString());

        var write = ActivityEvidence.TechnicalEvidence("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write","input":{"command":"node --test","content":"PRIVATE-SOURCE"}}]}}""", new JsonObject { ["adapter"] = "claude" });
        Assert.Equal("Write", write["entries"]![0]!["tool"]!.GetValue<string>());
        Assert.False(write["entries"]![0]!.AsObject().ContainsKey("command"));
        Assert.DoesNotContain("PRIVATE-SOURCE", write.ToJsonString());

        var fileWrite = ActivityEvidence.TechnicalEvidence("""{"step_update":{"step_type":"tool","state":"DONE","tool_name":"write_to_file","tool_info":{"parameters":{"CommandLine":"DO-NOT","CodeContent":"SOURCE"}}}}""", new JsonObject { ["adapter"] = "antigravity" });
        Assert.False(fileWrite["entries"]![0]!.AsObject().ContainsKey("command"));
        Assert.DoesNotContain("DO-NOT", fileWrite.ToJsonString());
        Assert.DoesNotContain("SOURCE", fileWrite.ToJsonString());

        Assert.Equal("unknown", ActivityEvidence.TechnicalEvidence("""{"type":"item.completed","item":{"type":"command_execution","command":"echo"}}""", new JsonObject())["entries"]![0]!["provenance"]!.GetValue<string>());
        var omitted = ActivityEvidence.TechnicalEvidence("hello");
        Assert.Contains("Adapter identity is unknown", omitted["warnings"]!.ToJsonString());
        Assert.Equal("hello", omitted["entries"]![0]!["output"]!.GetValue<string>());

        Assert.Equal("""{"nested":"{\"api_key\":[REDACTED],\"password\":[REDACTED]}"}""", ActivityEvidence.Redact("""{"nested":"{\"api_key\":\"NESTED-SECRET\",\"password\":\"NESTED-PASSWORD\"}"}"""));
        Assert.Equal("[REDACTED PRIVATE KEY]\n", ActivityEvidence.TechnicalEvidence("-----BEGIN OPENSSH PRIVATE KEY-----\nRAW-KEY-LINE\n-----END OPENSSH PRIVATE KEY-----\n", codex)["entries"]![0]!["output"]!.GetValue<string>());
        Assert.Equal("passwor\u212A=SECRET-VALUE", ActivityEvidence.Redact("passwor\u212A=SECRET-VALUE"));
        Assert.Equal("Bearer\u00a0[REDACTED]", ActivityEvidence.Redact("Bearer\u00a0SECRET-VALUE"));
        Assert.Equal("[REDACTED]", ActivityEvidence.Redact("sk-" + new string('a', 8)));
        Assert.DoesNotContain("VALUE", ActivityEvidence.Redact("token: VALUE-9"));

        var fractional = ActivityEvidence.TechnicalEvidence("""{"type":"item.completed","item":{"type":"command_execution","command":"x","exit_code":1.5}}""", codex);
        Assert.False(fractional["entries"]![0]!.AsObject().ContainsKey("exitCode"));
    }

    [Fact]
    public void Tails_excerpts_and_parallel_redaction_stay_inside_the_limits()
    {
        var codex = new JsonObject { ["adapter"] = "codex" };
        var dropped = ActivityEvidence.TechnicalEvidence("HEAD-LINE-SHOULD-DROP\n" + new string('x', ActivityEvidence.LIMITS.TailBytes), codex);
        Assert.True(dropped["truncated"]!.GetValue<bool>());
        Assert.Empty(dropped["entries"]!.AsArray());
        Assert.Contains("bounded truncated excerpt", dropped["warnings"]!.ToJsonString());

        var rows = string.Join("\n", Enumerable.Range(0, 100).Select(i =>
            new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "command_execution", ["command"] = "echo " + i } }.ToJsonString()));
        var many = ActivityEvidence.TechnicalEvidence(rows, codex);
        var commands = many["entries"]!.AsArray().Select(node => node!["command"]!.GetValue<string>()).ToArray();
        Assert.Equal(20, commands.Length);
        Assert.Equal("echo 80", commands[0]);
        Assert.Equal("echo 99", commands[^1]);
        Assert.True(many["truncated"]!.GetValue<bool>());

        var euros = ActivityEvidence.TechnicalEvidence(new string('\u20ac', 3000), codex)["entries"]![0]!["output"]!.GetValue<string>();
        Assert.EndsWith("\n[excerpt truncated]", euros);
        Assert.DoesNotContain("\uFFFD", euros);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(euros) <= ActivityEvidence.LIMITS.FieldBytes);
        Assert.All(euros[..^"\n[excerpt truncated]".Length], character => Assert.Equal('\u20ac', character));

        var gate = new System.Threading.Barrier(8);
        var results = new string[8];
        System.Threading.Tasks.Parallel.For(0, 8, i =>
        {
            gate.SignalAndWait();
            results[i] = ActivityEvidence.Redact("password=\"VALUE " + i + "\"\nBearer TOKEN" + i);
        });
        for (var i = 0; i < results.Length; i++)
        {
            Assert.DoesNotContain("VALUE", results[i]);
            Assert.DoesNotContain("TOKEN", results[i]);
            Assert.Contains("password=[REDACTED]", results[i]);
            Assert.Contains("Bearer [REDACTED]", results[i]);
        }
    }
}
