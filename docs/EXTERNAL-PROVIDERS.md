# External API and CLI connections

Open **Usage and permissions → Add API / CLI connection**. A connection has a unique provider ID, display name and exact model IDs. Saving adds it to the provider selector without making inference requests. Connections persist in `provider-connections.json` beside the notebook's orchestrator state; the shipped `models.json` is unchanged. Existing connection IDs cannot be overwritten through this form.

## Claude Code subscription CLI

Claude Code is a built-in provider, separate from the Anthropic API connection and from Claude models routed through Antigravity. Install the native `claude` executable and run `claude auth login` using the account with Claude Code access. `claude auth status` checks the active login. Execution delegates authentication to the CLI; no API key is needed for a subscription login. The quota reader uses the active subscription access token in memory and never copies credentials into notebooks, model prompts or diagnostic output.

Restart Planwharf after updating its backend, open **Usage and permissions**, select **Claude Code**, and use **Refresh models**. Enable the models and Allowed roles you want. Models start disabled. Discovery checks authentication and uses the CLI initialization control response without sending a user turn. Aliases are accepted only when that response supplies their exact `resolvedModel`; the runner sends that exact ID with no fallback model. A catalog entry is not proof of inference entitlement; rejected model access is reported explicitly.

Claude Code supports coordinator, implementation worker and reviewer assignments. Always approve / YOLO uses `bypassPermissions`; turning it off uses `dontAsk`, which denies tools requiring approval instead of waiting for an interactive prompt. These modes are not an operating-system sandbox. Prompts travel over stdin, including long Unicode conversations. Streaming results must include the exact model identity and a successful final result; process exit zero alone is insufficient. Structured worker results must also match their assigned task identities. Reported token usage is recorded separately from subscription quota.

Sessions are not persisted by Claude Code. Planwharf disables hooks, automatic memory, Chrome integration, inherited MCP servers and nested Agent/Task delegation for these launches; project settings files are excluded. User settings remain available for CLI authentication configuration. Planwharf supplies its own project Skill instructions through the existing prompt delivery path. Source-protected research and the no-tool Supervisor role are unavailable for this adapter.

Subscription quota is read from Anthropic's OAuth usage service after checking the active CLI authentication method. Planwharf shows the smaller remaining percentage of the shared 5-hour and weekly windows, with their reset times. Model-specific meters and extra paid usage are not combined into the shared pool. Reads are cached in memory for at most five minutes, ending earlier at a window reset or credential expiry; a changed login invalidates the cache. Rate-limit responses honor numeric Retry-After up to one hour. Failed reads show **REMAINING UNREAD** with a reason rather than authorizing work using expired readings.

Quota authentication supports the CLI's `.credentials.json` on Windows/Linux and an explicitly configured `CLAUDE_CODE_OAUTH_TOKEN`. `CLAUDE_CONFIG_DIR` is respected. Keychain-only macOS credentials are not read by this adapter. API/gateway/cloud-provider authentication is not attributed to a stored subscription. The reader does not refresh or rewrite CLI credentials: open Claude Code to renew an expired login. The OAuth usage endpoint is an observed first-party service, not a guaranteed public API contract; format or access changes fail explicitly. `/usage` in Claude Code remains an independent check.

Automatic account classification and per-account role separation are not implemented for this adapter; stop work before switching CLI accounts, refresh models and review roles afterwards. Native executable discovery checks `PATH` and `~/.local/bin`; `PLANWHARF_CLAUDE` can select an explicit executable.

Offline checks: `node --test engine/claude.test.cjs engine/provider-transport.test.cjs`. An explicitly requested live check can run `node validation/check-claude-cli.cjs --live <exact-model-ID>`; it uses the current CLI account for a coordinator read and a worker write in a temporary project, preserves attempt evidence, and never opens a personal notebook. Windows validation with Claude Code 2.1.309 and `claude-sonnet-5-5` completed both operations. Other catalog models and platforms require separate live checks.

References: [Claude Code CLI](https://code.claude.com/docs/en/cli-reference), [programmatic execution](https://code.claude.com/docs/en/headless), [credential storage and authentication precedence](https://code.claude.com/docs/en/authentication), [subscription rate-limit windows](https://code.claude.com/docs/en/statusline#rate-limit-usage), [Agent SDK Python source](https://github.com/anthropics/claude-agent-sdk-python).

## Direct APIs

Select **OpenAI-compatible API** or **Anthropic Messages API**, enter the base URL and the **name** of the environment variable containing your key. Set that variable in the environment that starts Planwharf, then restart Planwharf. Never enter the key itself in the form or notebook. Leave the variable name empty only for an unauthenticated endpoint, such as a local server. Remote endpoints require HTTPS; HTTP is accepted only on loopback.

Examples of endpoint configuration from provider documentation:

| Provider | Protocol | Base URL | Suggested environment variable |
| --- | --- | --- | --- |
| DeepSeek | OpenAI-compatible | `https://api.deepseek.com` | `DEEPSEEK_API_KEY` |
| Kimi / Moonshot | OpenAI-compatible | `https://api.moonshot.ai/v1` | `MOONSHOT_API_KEY` |
| Gemini | OpenAI-compatible | `https://generativelanguage.googleapis.com/v1beta/openai` | `GEMINI_API_KEY` |
| Claude | Anthropic Messages | `https://api.anthropic.com/v1` | `ANTHROPIC_API_KEY` |
| OpenAI API | OpenAI-compatible | `https://api.openai.com/v1` | `OPENAI_API_KEY` |
| Local compatible server | OpenAI-compatible | Your loopback server's base URL | Optional |

Choose exact model IDs from your account's catalog. The form intentionally does not promise access to fixed model names. Check **These models support tools** only for models that implement the selected protocol's function/tool calls; this enables lead and worker roles. Merely configuring a route does not verify access. Catalog discovery can list models, but does not prove they can execute tasks. Discovered models receive no roles automatically.

The API agent handles tool rounds, workspace file listing/reading/writing, and structured worker results. Leads receive read/list tools for coordination. Worker shell execution requires YOLO. YOLO starts **off** for new connections; off still permits workspace file edits and is not an operating-system sandbox. Neither external adapter supports the source-protected research workflow.

Responses must follow the selected protocol and identify the requested exact model. A different returned model, unsupported response contract, truncated output, HTTP error, or exhausted round limit fails explicitly. Provider aliases that return a different canonical model ID must be configured with that canonical ID. No silent provider substitution or automatic inference retry occurs.

API services generally meter token consumption and charge according to the account's pricing, credits or prepaid balance. Chat subscriptions do not imply API credits. Planwharf records input/output tokens when reported by the API; missing usage remains unknown. It does not infer prices, remaining money, or a provider quota percentage from those counts. Configuring a remaining quota stop threshold while quota is unread blocks new calls. Maximum output tokens and maximum rounds bound each agent call; they are **not a hard currency budget** and do not cover simultaneous calls or other applications. Use the provider's own spending controls when available.

## Configured CLIs

Select **Configured CLI** and declare:

- A native executable path or executable name on `PATH`.
- An explicit JSON argument array containing `{model}`. `{cwd}` is also supported. The prompt is passed on stdin.
- Separate explicit argument arrays for YOLO and restricted mode. Use the CLI's documented flags. An empty array adds no restrictions; Planwharf cannot establish that arbitrary flags implement a sandbox.
- A supported structured event protocol: `codex-jsonl`, `messages-jsonl`, or `antigravity-jsonl`.

Planwharf uses the CLI's existing authentication. Installing a CLI alone does not prove compatibility: it must accept these transports and emit the selected event contract. Shell command strings and automatic guessing of permission flags are not supported. A CLI with another protocol needs a dedicated adapter. Command launch, exact model selection, Unicode stdin and separate permission arguments are covered by local process fixtures.

## Compatibility and validation

OpenAI-compatible does not mean every vendor extension is supported. Model-specific request requirements or other protocols may require another adapter. Tests cover both API protocols with local HTTP fixtures, real lead/worker child processes, tool rounds, structured results, reported tokens, failures and permission boundaries. They do not establish access to any live provider account or verify every model. Keys are read in the child process and are not placed in process arguments, connection files or generated issue drafts. Provider response bodies from failed HTTP requests are excluded from error logs.

Protocol references: [DeepSeek tool calls](https://api-docs.deepseek.com/guides/tool_calls/), [Kimi chat API](https://platform.kimi.ai/docs/api/chat), [Gemini OpenAI compatibility](https://ai.google.dev/gemini-api/docs/openai), [Anthropic Messages](https://platform.claude.com/docs/en/api/messages/create), [OpenAI function calling](https://developers.openai.com/api/docs/guides/function-calling).
