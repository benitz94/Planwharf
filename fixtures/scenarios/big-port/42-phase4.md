# Sheet 42: phase 4, processes and providers

Port each module below to C#, with its tests and its comparison cases.

- `runner`: `src/runner.cs` and `tests/runner.tests.cs`
- `provider-models`: `src/provider-models.cs` and `tests/provider-models.tests.cs`
- `external-adapters`: `src/external-adapters.cs` and `tests/external-adapters.tests.cs`
- `api-agent`: `src/api-agent.cs` and `tests/api-agent.tests.cs`
- `provider-connections`: `src/provider-connections.cs` and `tests/provider-connections.tests.cs`
- `antigravity`: `src/antigravity.cs` and `tests/antigravity.tests.cs`
- `plan-usage`: `src/plan-usage.cs` and `tests/plan-usage.tests.cs`
- `plan-file`: `src/plan-file.cs` and `tests/plan-file.tests.cs`
- `phone`: `src/phone.cs` and `tests/phone.tests.cs`
- `phone-ollama`: `src/phone-ollama.cs` and `tests/phone-ollama.tests.cs`
- `phone-handover`: `src/phone-handover.cs` and `tests/phone-handover.tests.cs`
- `phone-handover-live`: `src/phone-handover-live.cs` and `tests/phone-handover-live.tests.cs`
- `phone-activation-check`: `src/phone-activation-check.cs` and `tests/phone-activation-check.tests.cs`
- `pr-check`: `src/pr-check.cs` and `tests/pr-check.tests.cs`
- `worktrees`: `src/worktrees.cs` and `tests/worktrees.tests.cs`
- `provider-adapters`: `src/provider-adapters.cs` and `tests/provider-adapters.tests.cs`

Then wire them together: the comparison list `parity/required.json` names every module, and every test file passes.
