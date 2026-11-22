# Job 35: worker isolation and sandbox specification

**Goal.** Specify how each Planwharf worker runs isolated on Linux, macOS, and Windows,
restricting what it can read and write, controlling its network access, and gating risky commands
behind the owner's explicit approval. This document is the specification sheet delivered by Job 35;
the implementation is a separate future job following review. Follow [the pipeline design](../../../docs/DESIGN-PIPELINE.md)
and [the porting rules](../README.md).

## 1. Current boundary and the need for OS isolation

Planwharf today enforces workspace containment primarily by starting worker processes inside the
project folder or assigned Git worktree (`engine/project-workspace.cjs`, `engine/worktrees.test.cjs`,
`engine/worker-containment.test.cjs`). While the engine checks starting directories and validates file
claims after execution, this is an application-level convention rather than an operating-system security
barrier:
- A rogue, compromised, or misaligned model can invoke commands that inspect or modify paths outside the
  project folder (e.g. `~/.ssh`, `~/.aws`, `~/.gitconfig`, environment variables, or other project directories).
- Untrusted dependencies or scripts executed during build or test phases can read secrets, access host network
  resources, or install persistent artifacts in user-level system paths.
- Different AI CLI tools provide disparate built-in sandboxing (Codex sandbox modes, Claude Code permissions,
  Grok and Antigravity runners); Planwharf requires a uniform, verifiable minimum level of containment
  across all supported providers and operating systems.

The C# engine (in Phase 4 process execution) will execute all worker commands within an OS-enforced
sandbox. This sheet specifies the exact capabilities, platform implementations, approval boundaries,
and verification tests for that isolation layer.

---

## 2. Worker capabilities and access policies

Every worker process runs under the principle of least privilege, bound to a specific job and worktree.

### 2.1 Filesystem access

| Target path | Access mode | Purpose / Justification |
|---|---|---|
| Assigned worktree (`.planwharf/worktrees/<id>`) | **Read-Write** | Primary working directory for source changes, build outputs, and local artifacts. |
| Project-level shared caches (`.cache`, compiler artifacts) | **Read-Only** (or isolated overlay) | Avoid downloading standard runtime packages from scratch; prevent poisoning cache for other workers or write races. |
| System toolchain and runtimes (`/usr`, `/opt`, `Program Files`, `.dotnet`) | **Read-Only / Execute** | Running compilers (`dotnet`, `node`, `git`, `python`, standard system utilities). |
| OS temporary directories (`/tmp`, Windows `%TEMP%`) | **Isolated private per-worker temp directory** | Scratch space cleaned up immediately upon process or attempt completion. |
| User Home directory (`~`, `%USERPROFILE%`) | **Deny all** | Prevent access to personal files, desktop, downloads, browser data, or shell configuration. |
| Credential and secret stores (`~/.ssh`, `~/.aws`, `~/.config/gcloud`, macOS Keychain, Windows Credential Manager) | **Deny all / Blocked** | Workers must never read host keys, cloud tokens, or API secrets. |
| Planwharf engine state and attempts store (`.planwharf/attempts/`, notebooks) | **Deny / Engine-brokered only** | Workers interact with Planwharf exclusively through the approved MCP protocol or result JSON, not direct file access. |
| Other project directories and sibling worktrees | **Deny all** | Complete isolation between concurrent jobs and unrelated projects. |

Two concurrent workers never share a worktree or an AppContainer profile. Each concurrent worker is assigned a dedicated worktree (`.planwharf/worktrees/<id>` or `worktrees/<P>/jobs/<J>`) and a unique private temp directory (`/tmp/planwharf-worker-<attemptId>` on Linux/macOS, `%TEMP%\planwharf-worker-<attemptId>` on Windows). Sibling worktree isolation ensures a worker in one worktree cannot inspect, read, or modify the worktree of a concurrent worker. Shared runtime caches are mounted read-only (or with isolated per-worker copy-on-write overlays) to prevent write races or cache poisoning between concurrent workers. Git repository internal protection ensures that while workers run local git commands on their branch, the repository's `.git/config` and `.git/hooks` are protected against modification, and git push is blocked inside the sandbox (only the engine outside the sandbox may push). Symlink and hardlink breakouts are strictly prevented by rejecting path traversal and links resolving outside the worktree.

### 2.2 Network access

- **Default policy**: **Denied** (isolated network namespace, loopback disabled or strictly bound).
- **Conditional allowance**: Permitted only when the work manifest explicitly declares that dependencies or
  external assets must be retrieved, and restricted where possible to known package registries (e.g.,
  `api.nuget.org`, `registry.npmjs.org`).
- **Localhost and LAN restriction**: Direct access to local loopback ports (preventing probing of local web
  servers, database instances, or internal microservices) is blocked unless specifically authorized for integration testing. Localhost network isolation via private network namespaces or loopback filtering prevents concurrent workers from probing or colliding on each other's test ports.
- **Credential isolation**: Git operations requiring remote authentication are performed exclusively by the
  engine outside the sandbox; workers never receive Git push credentials.

---

## 3. Platform isolation implementations and AI tool integration

Planwharf targets Windows, macOS, and Linux across x64 and ARM64 architectures. The sandbox adapts
native OS isolation primitives while presenting a single configuration interface to the engine.

### 3.1 Linux implementation: Bubblewrap and namespaces

- **Mechanism**: Unprivileged user namespaces using `bwrap` (Bubblewrap), standard on modern Linux desktops and CI runners.
- **Filesystem mapping**:
  - `--ro-bind / /`: Root filesystem mounted read-only.
  - `--dev /dev`, `--proc /proc`: Minimal virtual file systems.
  - `--tmpfs /tmp`: Ephemeral private scratch space.
  - `--bind <worktree> <worktree>`: Read-write access granted strictly to the assigned worktree.
  - `--ro-bind <toolchain-path> <toolchain-path>`: Read-only access to necessary runtimes (.NET, Node.js).
  - Explicit hiding (`--tmpfs <path>` masking) over `~/.ssh`, `~/.gnupg`, `~/.config`, `/root`.
- **Network containment**:
  - `--unshare-net`: Drops network interfaces entirely when network access is disabled.
  - When network is enabled, runs in an isolated network namespace or filters outbound connections.
- **Process containment**:
  - `--unshare-pid`: Worker processes cannot see, signal, or trace host processes.
  - `--die-with-parent`: Eliminates orphaned background processes on worker cancellation or timeout.

### 3.2 macOS implementation: Sandbox-exec and seatbelt profiles

- **Mechanism**: Apple Seatbelt sandbox profiles compiled dynamically and enforced via `sandbox-exec` (or POSIX sandbox APIs).
- **Profile rules (`.sb`)**:
  - `(version 1)`
  - `(deny default)`: Default-deny all operations.
  - `(allow process-exec (subpath "/bin") (subpath "/usr") (subpath "/Library/Developer") ...)`: System tools.
  - `(allow file-read* (subpath "/"))`: Read-only system and dependency inspection.
  - `(deny file-read* (subpath (param "USER_HOME_CREDENTIALS")))`: Explicit deny on Keychain, SSH, AWS, and Cloud tokens.
  - `(allow file-read* file-write* (subpath (param "WORKTREE_PATH")))`: RW restricted to the designated worktree.
  - `(allow file-read* file-write* (subpath (param "PRIVATE_TEMP_PATH")))`: RW restricted to isolated temp.
  - `(deny network*)`: Default deny network socket creation; selectively relaxed for DNS and HTTPS outbound when network enabled.

### 3.3 Windows implementation: AppContainer, restricted tokens, and Job Objects

- **Mechanism**:
  - **Job Objects**: Every worker process is assigned to a Windows Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`,
    active process count bounds, and memory limits, ensuring reliable process tree termination (preventing lingering node/dotnet background processes).
  - **Restricted Security Tokens**: Worker processes run with stripped privileges, removing administrative and debugging rights
    (`SeDebugPrivilege`, `SeAssignPrimaryTokenPrivilege`), and explicit deny SIDs on sensitive directories.
  - **AppContainer / Low-Integrity Isolation**: For maximum isolation, workers run under an AppContainer profile with unique AppContainer profile names and SIDs per worker attempt (`Planwharf.Worker.<AttemptId>`), with explicit file capabilities granted only to the worktree and private temp folder, with internet client capability toggled per job policy.
  - **Credential Isolation**: Access to DPAPI, Windows Credential Manager (`CredRead`), and `%USERPROFILE%\.ssh` / `%USERPROFILE%\.aws`
    is explicitly blocked or masked.

### 3.4 Integration with existing AI tool sandboxing

Different AI tools currently offer disparate sandboxing controls:
- **Codex CLI**: Supports internal bubblewrap / Docker container flags. When Codex is configured, Planwharf ensures its internal
  flags align with Planwharf's worktree boundary, but treats the outer OS sandbox as the authoritative boundary.
- **Claude Code**: Employs prompt-level permission checks and bash execution hooks. Planwharf wraps Claude in the OS sandbox
  so that even if Claude runs in automated non-interactive mode, commands cannot exceed the sandbox envelope.
- **Grok & Antigravity CLI**: Lack autonomous native OS sandboxing on arbitrary script invocation; Planwharf's outer sandbox
  envelops these workers uniformly.
- **Sanitized Environment**: For all tools, Planwharf strips extraneous host environment variables before launch:
  `AWS_*`, `GITHUB_TOKEN`, `AZURE_*`, `SSH_AUTH_SOCK`, `GCLOUD_*`, `GIT_ASKPASS`, and all provider API keys (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `GEMINI_API_KEY`, `GOOGLE_API_KEY`, `GROQ_API_KEY`, `MISTRAL_API_KEY`). Neutralizing HOME and USERPROFILE to an isolated dummy scratch directory prevents tools from discovering or reading user profile dotfiles.

---

## 4. Risky commands, escalation, and owner approval

Any operation that attempts to breach the least-privilege boundary cannot execute autonomously. It is intercepted
and submitted to the owner for authorization.

### 4.1 Operations requiring explicit owner approval

The sandbox interceptor and supervisor flag the following actions as privileged:
1. **External filesystem modification**: Writing, deleting, or renaming paths outside the assigned worktree (e.g. attempting to alter global configs or system files).
2. **System package installation**: Invocations of system package managers (`apt`, `dnf`, `pacman`, `brew`, `choco`, `winget`, `npm install -g`, `dotnet tool install -g`).
3. **Unapproved network destinations**: Outbound socket requests to unknown IP addresses or unlisted domains.
4. **Elevation attempts**: Any attempt to use `sudo`, `doas`, `runas`, or request elevated UAC tokens.
5. **System service or hardware control**: Adding background daemons, systemd units, Windows services, or interacting with device hardware.

### 4.2 Escalation channels and notification flow

When a risky command is detected:
1. **Immediate pause**: The worker process execution is suspended at the interception point.
2. **Planwharf desktop chat**: A highlighted, prominent card is rendered in the project chat:
   - Command line and binary path.
   - Identified risk category and affected resource.
   - One-click actions: **Allow once**, **Allow for this work**, or **Deny**.
3. **Phone integration**: An immediate alert is dispatched via configured phone channels (Job 12):
   - **ntfy**: High-priority push notification with command summary.
   - **Telegram bot**: Interactive notification containing the request details and inline buttons (`/approve`, `/deny`).
4. **Fail-closed timeout**:
   - If the owner does not respond within the configured timeout (default: 5 minutes), the operation is **strictly denied**.
   - The worker is returned a standard `EACCES` / permission denied error, allowing the model to adapt its strategy or report a blocker. Under no circumstances does a timeout default to approval.

### 4.3 Error paths and exception handling

- **Sandbox initialization failure**: If the OS sandbox primitive fails to initialize (e.g. `bwrap` missing, unprivileged namespaces disabled, or AppContainer creation failure), the engine strictly fails closed immediately with diagnostic code `sandbox-init-failed` and does not run uncontained without explicit owner override.
- **Access violation handling**: Attempted boundary breaches return `EACCES` / access denied, do not crash the engine, and log an append-only audit event in `sandbox.log`.
- **Escalation channel failure**: If desktop chat or phone channels (ntfy/Telegram) are offline or unreachable, escalation fails closed immediately (never default to approval).
- **Explicit denial**: When the owner selects Deny or `/deny`, worker immediately receives `EACCES` cleanly and adapts.
- **Resource exhaustion**: Exceeding memory ceilings or process count limits triggers Job Object or cgroups termination with reason `worker-resource-exhausted`, halting runaway fork-bombs without crashing the engine.

---

## 5. Development workflow compatibility, overhead, and configuration

The sandbox is designed to run transparently during normal development workflows without breaking legitimate builds or tests.

### 5.1 Permitted standard development operations

The following actions proceed without interruption:
- Compiling code and generating artifacts inside the worktree (`dotnet build`, `cargo build`, `npm run build`, `tsc`).
- Running automated test suites (`dotnet test`, `node --test`, `pytest`, `cargo test`).
- Local Git operations in the worktree (`git status`, `git diff`, `git add`, `git commit` on the designated task branch; pushing to remotes remains an engine-exclusive responsibility).
- Reading installed SDKs, compilers, standard library headers, and local project dependencies.

### 5.2 Performance overhead and resource bounds

- **Startup latency**: Process invocation overhead must remain under 50 ms on Linux (`bwrap`), 80 ms on macOS (`sandbox-exec`), and 100 ms on Windows (`Job Object` + restricted token).
- **Resource capping**: Each sandbox instance enforces CPU limits (e.g. maximum CPU cores) and memory ceilings (configured per project or attempt) via Linux cgroups or Windows Job Object limits to prevent runaway fork-bombs or memory exhaustion.

### 5.3 Project configuration and emergency override

- **Settings controls**:
  - `sandbox.enabled`: Enabled by default. Can be toggled off per project if a specialized legacy toolchain is fundamentally incompatible with OS sandboxing.
  - `sandbox.network`: Options: `none` (default), `package-registries-only`, or `full`.
  - `sandbox.escalationTimeoutSeconds`: Defaults to 300 seconds (5 minutes).
- **Audit trail**: Every sandbox initialization, policy violation, and escalation decision is logged append-only to `.planwharf/attempts/<id>/sandbox.log` for debugging and security auditing.

### 5.4 Recovery paths and lifecycle cleanup

- **Process tree cleanup on crash, timeout, or cancellation**: Windows Job Object (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`) and Linux `--die-with-parent` / PID namespace reaper guarantee all descendant processes are terminated when a worker finishes, hangs, or is cancelled.
- **Ephemeral temp cleanup**: Private worker temp directory is deleted upon worker termination or crash, and cleanup errors do not leave orphaned mounts.
- **Engine restart recovery**: Startup sweeps close leftover Job Objects / containers and delete stale sandbox temp directories from previous sessions.

---

## 6. Acceptance checks and verification test suite for the build job

The future implementation job will be evaluated against a comprehensive test suite across all three supported operating systems.

### 6.1 Adversarial escape and boundary test cases

The test suite must include automated adversarial scenarios attempting to bypass isolation:

1. **Path traversal**: Attempting to read or write files using `../../` sequences pointing outside the assigned worktree. Verified: blocked with access violation / permission denied.
2. **Symlink breakout**: Creating a symlink inside the worktree targeting a file in the user's home directory or system configuration, and attempting to read/write via the symlink. Verified: path resolution strictly denied by sandbox policy.
3. **Credential store protection**: Probing `~/.ssh`, `~/.aws`, `~/.config`, Windows Credential Manager, and macOS Keychain. Verified: paths are either completely unmounted/invisible or return immediate access denial.
4. **Host process isolation**: Attempting to enumerate or send signals (`kill`) to processes outside the worker's private process namespace or Job Object. Verified: host processes invisible and unaffected.
5. **Network egress containment**: Attempting raw socket connections or HTTP requests to external IPs when `sandbox.network` is `none`. Verified: network unreachable error returned immediately.
6. **Localhost port probe containment**: Attempting to connect to active local development ports (e.g. `127.0.0.1:8080`). Verified: connection refused or dropped by sandbox network filter.
7. **Process tree cleanup**: Launching background processes or child threads that intentionally detach. When the worker attempt terminates or times out, all descendants must be forcibly reaped by the Job Object or PID namespace monitor.
8. **Escalation lifecycle**: Triggering a privileged command:
   - Verifying the command pauses and prompts the owner in chat and phone channels.
   - Verifying that explicit denial returns `EACCES` cleanly to the worker.
   - Verifying that expiration of the timeout period results in strict fail-closed rejection.
   - Verifying that approved execution resumes and completes without un-sandboxing unrelated tasks.
9. **Sibling worktree containment**: Attempting to read or write a concurrent worker's worktree (`../sibling-worktree`). Verified: blocked with access violation.
10. **Sandbox init failure containment**: Simulating missing sandbox prerequisites or corrupted profiles. Verified: engine halts attempt with `sandbox-init-failed`, refusing unconfined fallback.

### 6.2 Acceptance check for the implementation job

1. Worker processes for Claude Code, Codex, Grok, and Antigravity launch under the OS sandbox with verified least-privilege policies on Linux, macOS, and Windows.
2. Build commands, test executions, and local Git operations run cleanly without performance degradation or false positive permission errors.
3. Every adversarial escape test passes with zero privilege leaks across all target platforms.
4. Run `dotnet build native/Planwharf.Engine`, `dotnet test native/Planwharf.Engine.Tests`, and `node scripts/engineering/ci.cjs` with zero failures and zero warnings.

## 7. Files allowed for the implementation job

The C# engine process execution and containment layer (`native/Planwharf.Engine/Runner/`, `Sandbox/`), its automated test suite (`native/Planwharf.Engine.Tests/`), and minimal native platform adapters for Linux (`bwrap`), macOS (`sandbox-exec`), and Windows (`JobObject`/`AppContainer`). The existing JavaScript runner modules remain unaffected until phase 4.

## 8. Open design decisions

The following decisions are documented for the owner's review prior to starting the build job:

1. **Linux fallback mechanism when `bwrap` is missing**: Whether the engine should strictly require `bwrap` as a system prerequisite, or support a degraded fallback using direct unprivileged `unshare` namespaces or Docker/Podman when available.
2. **Windows AppContainer versus restricted token with low integrity level**: AppContainer offers the strongest isolation, but certain legacy toolchains expect standard Windows user object access; evaluating whether Job Objects + low-integrity restricted tokens should serve as the default baseline with AppContainer as an opt-in mode.
3. **Outbound network filtering mechanism**: Whether package registry network access should be enforced via an engine-brokered local forwarding proxy with domain allowlists, or OS-native packet filtering (e.g. `nftables`/`pf`/Windows Filtering Platform).

## 9. Hand over

Review this sheet before implementation. The current job commits this sheet, marks Job 35 as "Done, waiting for review" in `docs/NEXT-WORK.md`, and adds Job 35 to `docs/BUILT-WITH-PLANWHARF.md` with pull request `#?`.
