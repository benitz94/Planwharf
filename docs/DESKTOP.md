# Desktop app and Linux install

Planwharf comes in two forms. Both run the same app on your computer.

| | Portable (ZIP) | Desktop app |
| --- | --- | --- |
| What it is | The app folder; it opens in your browser's app window | An installer with its own window (Electron) |
| Needs Node.js | Yes, installed separately | No, it is included |
| Updates | From **Settings > Updates** | Download the new installer |
| Your data | Next to the app (`plan.json`, `.attempts-plan`) | In your user folder (see below) |

The AI tools you use (Claude Code, Codex and the others) are installed and signed in separately in both cases.

## Desktop app

- **Windows:** run `Planwharf-Setup-<version>.exe`. The installer is **not code-signed yet**, so Windows SmartScreen may say it "protected your PC": choose **More info > Run anyway** only if you downloaded it from the official GitHub Releases page.
- **Linux:** use the `.AppImage` (make it executable, then open it) or install the `.deb` with `sudo apt install ./planwharf-desktop_<version>_amd64.deb`.

Your projects and settings are stored in:

- Windows: `%APPDATA%\Planwharf\data`
- Linux: `~/.config/Planwharf/data`

On its first start, the desktop app asks whether you already use the ZIP version. Choose that folder (the one with `Planwharf.exe` or `planwharf.sh`) and it copies your projects, team and history. The ZIP version keeps its own copy, so use one version at a time. It asks only once; later you can still move single projects with **Settings > Move a project**.

Closing the window saves unfinished work and stops Planwharf, as in the portable version. A log of each start is in `desktop.log` in the data folder.

## Linux: install from the command line

One command installs the portable version for your user, without `sudo`:

```sh
curl -fsSL https://raw.githubusercontent.com/benitz94/Planwharf/main/install.sh | sh
```

It is good practice to read a script before you run it:

```sh
curl -fsSL https://raw.githubusercontent.com/benitz94/Planwharf/main/install.sh -o install.sh
less install.sh
sh install.sh
```

The script:

1. checks for Node.js 18 or newer;
2. downloads the latest stable release from GitHub and checks its SHA-256 against the value GitHub publishes;
3. copies the app into `~/.local/share/planwharf`, keeping your projects when you run it again;
4. adds the `planwharf` command in `~/.local/bin` and a menu entry.

It also works on macOS, without the menu entry.

## Building the desktop app

```sh
cd electron
npm install
npm run dist:win     # Windows installer, in electron/dist
npm run dist:linux   # AppImage and .deb (on Linux)
```

The **Build desktop app** workflow builds both on GitHub. It runs only when started by hand or for a `desktop-v*` tag, and puts the files in a draft release.
