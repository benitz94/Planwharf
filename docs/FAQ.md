# Frequently asked questions

New to Planwharf? This page explains what it does, how AI coding orchestration works, and how to get started. For step-by-step instructions, see the [quick start](QUICKSTART.md), [first project guide](HOW-TO.md), and [complete user guide](USER-GUIDE.md).

## Getting started

### What is Planwharf?

Planwharf is a local, project-first notebook and coordinator for AI coding workers. It keeps goals, decisions, plans, execution results, and checkpoints together so you can understand what has happened in a project and what needs to happen next. It is not just a chat interface.

### Why use Planwharf instead of running AI coding CLIs separately?

You can run Codex, Grok, or other supported connections directly. Planwharf adds a shared project record around those tools: an approved assignment, bounded tasks, visible worker and model identities, recorded outcomes, and checkpoints for interrupted work. It helps you coordinate and review work without relying on a long chat transcript to reconstruct progress.

### How do I download and launch Planwharf?

Open [GitHub Releases](https://github.com/benitz94/Planwharf/releases/latest) and download **`Planwharf-vX.Y.Z.zip`** under **Assets**. Extract the complete ZIP. Install Node.js separately; no `npm install` step is required. On Windows, launch `Planwharf.exe`. On macOS or Linux, use `sh ./planwharf.sh` or `node ./planwharf.cjs` from the extracted directory. Windows is the best-verified platform; macOS and Linux have launch paths but less end-to-end validation. See the [quick start](QUICKSTART.md) for details.

### Is Planwharf cloud-hosted?

No. Planwharf runs a local Node.js backend and stores project notebook data locally. AI work through selected providers still depends on those providers' own CLI or API connectivity, authentication, and terms. Running Planwharf locally does not mean every connected model runs offline.

### Do I need API keys or paid subscriptions?

You can open and use the local project notebook without invoking AI. To perform AI work, configure and authenticate a supported coding CLI or API connection and use a model available to that account. Access and possible charges depend on the chosen provider. A chat subscription does not automatically grant CLI or API access. See [external provider setup](EXTERNAL-PROVIDERS.md).

## AI models and orchestration

### Can I coordinate Codex, Grok and Gemini in one project?

Planwharf includes native adapters for Codex, Grok and Antigravity, plus documented direct API and compatible CLI connection options. Configure the actual connection and exact model IDs that your account supports. Availability and compatibility vary by provider and environment; not every model/combination has been tested. See [external providers](EXTERNAL-PROVIDERS.md).

### What is the difference between Orchestrator, Worker and Reviewer?

The **Orchestrator** coordinates approved work; **Workers** perform implementation or supported research; **Reviewers** inspect and report on approved review assignments. These are independent permissions assigned to models, not a ranking of which model is smarter. Review-only permission does not authorize implementation.

### Does Planwharf pick a different AI model if mine is unavailable?

No. The selected provider and exact model identity remain visible. If an assignment cannot run because the model or role is unavailable, Planwharf reports the problem rather than silently substituting a different model.

### Does Planwharf automatically change my source code?

Creating a notebook project or discussing an idea does not start coding. For execution, Planwharf prepares a **MegaTask** and its **SubTasks** for review and approval. Approved workers can then edit files in the linked project folder. Completion does **not** automatically commit, push, or publish those changes.

## Progress and recovery

### Can I pause or stop a coding run and resume later?

Yes. **Pause after current tasks** lets active tasks finish without starting new ones. **Stop** terminates active workers and saves a checkpoint while preserving partial work. **Resume** uses checkpoint and repository-state checks to continue unfinished tasks; it does not automatically replay completed SubTasks. See the [user guide](USER-GUIDE.md).

### What happens if an AI worker fails?

Planwharf retains execution evidence and reconciles process status rather than treating a successful process exit as proof of successful work. The run may need review, correction, and a fresh attempt. Examine results and changed files before accepting them.

### Can Planwharf manage multiple projects?

Yes. Projects have their own plans, histories, notes, and linked source folders. The selected project is the operational focus: an instruction for one project should not dispatch work for another unless you explicitly request it.

### Can I see AI usage and remaining quota?

Planwharf records available invocation and attempt metadata, such as model identity, timing and outcomes. It displays **remaining** provider quota when a reliable reading exists. When a provider cannot expose quota or token counts, Planwharf marks them unavailable instead of inventing values. This is not a billing calculator.

## Limitations and help

### Does Planwharf securely sandbox every AI worker?

No. Depending on the configured provider and permissions, workers may have broad access to local files and processes. Role restrictions and assignment instructions are not a security sandbox. Use trusted project folders, inspect proposed work, and understand provider permissions before running workers.

### Where can I report a bug or find instructions?

Start with the [quick start](QUICKSTART.md), [first project guide](HOW-TO.md) and [user guide](USER-GUIDE.md). Check [project status](PROJECT-STATUS.md) for known limitations. For bugs, share reproducible steps and sanitized diagnostics via [GitHub Issues](https://github.com/benitz94/Planwharf/issues); never include credentials or private source code in public reports.
