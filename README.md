# Glance

Glance is a small tray app for Windows and macOS. Press a hotkey and it reads your screen, sends the screenshot to an AI you choose and shows the answer in a popup in the corner of the screen. You can sign in with a ChatGPT, Claude or Google account through the official command-line tools, or use an API key for the Claude API, OpenAI, Gemini or any OpenAI-compatible server. The answer can also go to the clipboard instead of the popup.

Project page: https://aytutuna.github.io/Glance/

## Download

Get the latest build from the Releases page: a portable exe (x64 or arm64) or an installer. The portable exe needs no install and can be run from anywhere.

On a Mac, download the Mac zip from Releases, unzip it and move Glance to Applications. It needs macOS 14 or later. The app is not notarized, so macOS blocks it the first time you open it. Open System Settings > Privacy & Security and click Open Anyway (macOS 15 and later no longer offer this from the right-click menu), or run `xattr -dr com.apple.quarantine /Applications/Glance.app`. The first time it reads the screen, macOS asks for Screen & System Audio Recording access, and it asks again after each update because the app has no stable signing identity. The command-line tools below are found if they are installed.

## First run

Glance sits in the tray, or in the menu bar on a Mac. Open its menu and choose Connect an AI... to pick a provider and paste a key.

To use an account instead of a key, install the tool and sign in from the same dialog: the Claude CLI (`npm install -g @anthropic-ai/claude-code`, then `claude`), the Codex CLI for a ChatGPT account (`npm install -g @openai/codex`, then `codex login`) or the Gemini CLI for a Google account (`npm install -g @google/gemini-cli`, then `gemini`). No key is needed, and the usage limits of the account's plan apply.

Each provider's reasoning effort can be set in Connect an AI, in the tray menu or in the settings window, where the provider supports it. The Gemini CLI uses the model choice instead.

Answer goes to, in the menu and the settings window, can also copy the answer to the clipboard, or only copy it without showing anything, so you can paste it where you need it.

Default hotkeys, changeable in the settings window:

| Keys | Action |
| --- | --- |
| Ctrl+Shift+Q | read the screen |
| Ctrl+Shift+D | read an area you drag out |
| Ctrl+Shift+X | close the answer |
| Ctrl+Shift+B | hide the answer text and keep the screen black |

## Options

The tray menu and the settings window cover the same ground:

- Mode: Answer, Explain, Translate or Summarize, plus your own modes. The built-in prompts can be edited.
- Read from: the screen with the mouse, a specific screen, all screens, or the active window.
- Show answer on: which screen the popup appears on, and whether it sits in the bottom right or the center. The answer can also go to an area you set.
- Plain black answer: a pure black popup with white text instead of the dark gray one, with an adjustable text opacity.
- Keep answers after closing: saves the last ten answers, encrypted, so they are there next time.
- Connect an AI and Change order: save several keys and providers; Glance moves to the next one when a key fails or a limit runs out.
- Check for updates, Start with Windows (Open at login on a Mac).

The interface is in English and Turkish.

## Building

You need the .NET 10 SDK.

    dotnet build Glance.csproj -c Release
    dotnet test tests/Glance.Tests

For the Mac app, run `xcodegen generate --spec mac/project.yml`, then build the Glance scheme in Xcode.

`scripts/publish.ps1` builds the self-contained x64 and arm64 executables into `publish/` and, if Inno Setup 6 is installed, the installer.

## Privacy

See [PRIVACY.md](PRIVACY.md).

## License

MIT, see [LICENSE](LICENSE).
