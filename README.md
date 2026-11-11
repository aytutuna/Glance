# Glance

Glance is a small Windows tray app. Press a hotkey and it reads your screen, sends the screenshot to an AI you choose and shows the answer in a popup in the corner of the screen. It works with the Claude CLI, the Claude API, OpenAI, Gemini and any OpenAI-compatible server.

## Download

Get the latest build from the Releases page: a portable exe (x64 or arm64) or an installer. The portable exe needs no install and can be run from anywhere.

## First run

Glance sits in the tray. Open its menu and choose Connect an AI... to pick a provider and paste a key. The Claude CLI needs no key if `claude` is installed and signed in.

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
- Read from: the screen with the mouse, a specific screen, or all screens.
- Show answer on: which screen the popup appears on, and whether it sits in the bottom right or the center. The answer can also go to an area you set.
- Plain black answer: a pure black popup with white text instead of the dark gray one, with an adjustable text opacity.
- Keep answers after closing: saves the last ten answers, encrypted, so they are there next time.
- Connect an AI and Change order: save several keys and providers; Glance moves to the next one when a key fails or a limit runs out.
- Check for updates, Start with Windows.

The interface is in English and Turkish.

## Building

You need the .NET 10 SDK.

    dotnet build Glance.csproj -c Release
    dotnet test tests/Glance.Tests

`scripts/publish.ps1` builds the self-contained x64 and arm64 executables into `publish/` and, if Inno Setup 6 is installed, the installer.

## Privacy

See [PRIVACY.md](PRIVACY.md).

## License

MIT, see [LICENSE](LICENSE).
