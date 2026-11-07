# Planwharf

**Your team of AI models, in one chat.**

Planwharf is a free app for your computer. You tell a lead model what you want built, in plain words. A crew of AI models builds it, a second model checks the work and fixes what fails, and you follow everything in the chat. You can stop it at any time.

- **You are the captain.** You set the goal and choose the team.
- **One model builds, another checks.** The checker can come from another company, so mistakes are less likely to slip through.
- **Everything stays on your computer.** Planwharf uses the AI tools you already have (Claude Code, Codex, Grok CLI, Antigravity) or an API key. It never asks for your passwords.
- **Nothing is published without you.** The work stays in your project folder: Planwharf does not commit, push or publish it.

## Download

**Before you start**, you need:

1. [Node.js](https://nodejs.org/) 18 or newer;
2. at least one AI coding tool, installed and signed in: [Claude Code](https://github.com/anthropics/claude-code), [Codex](https://github.com/openai/codex), Grok CLI or Antigravity. A chat subscription alone does not include the command-line tool.

Then pick your system:

### Windows

1. Open the [latest release](https://github.com/benitz94/Planwharf/releases/latest) and download **`Planwharf-vX.Y.Z.zip`** (under **Assets**).
2. Extract the whole folder.
3. Double-click **`Planwharf.exe`**.

### macOS

1. Open the [latest release](https://github.com/benitz94/Planwharf/releases/latest) and download **`Planwharf-vX.Y.Z.zip`**.
2. Extract it, open Terminal and run:

   ```sh
   cd ~/Downloads/Planwharf
   sh planwharf.sh
   ```

### Linux

Run this one command. It installs Planwharf for your user, without `sudo`:

```sh
curl -fsSL https://raw.githubusercontent.com/benitz94/Planwharf/main/install.sh | sh
```

Then start it with `planwharf`. To read the script before running it:

```sh
curl -fsSL https://raw.githubusercontent.com/benitz94/Planwharf/main/install.sh -o install.sh
less install.sh
sh install.sh
```

Windows is the most tested system. macOS and Linux work but have been tested less: please [tell us](https://github.com/benitz94/Planwharf/issues) if something goes wrong.

## First steps

1. **Team:** choose the lead, give one model **Build** and another **Verify**.
2. **+ New project:** give it a name and a folder.
3. In the chat, tell the lead what you want built, then press **Use as the goal**.
4. Press **Let the crew do it** and follow the work in the chat.

Updates arrive inside the app: **Settings > Updates**. Your projects and settings are kept.

## Learn more

- [Guida in italiano](docs/GUIDA.md)
- [User guide](docs/USER-GUIDE.md) · [The chat screen](docs/CHAT-VIEW.md) · [FAQ](docs/FAQ.md)
- [Benchmark: Planwharf and four AI tools on the same task](docs/benchmarks/2026-11-logbook/README.md)
- [Technical reference](docs/REFERENCE.md), for advanced users and contributors

Planwharf is under active development and is provided **as is**, without warranty. Ideas and bug reports are welcome in [Issues](https://github.com/benitz94/Planwharf/issues).

## License

Free software under the **GNU General Public License version 3 only** ([LICENSE](LICENSE), [COPYRIGHT](COPYRIGHT)). Contributions need a [CLA](CLA.md); see [CONTRIBUTING.md](CONTRIBUTING.md).
