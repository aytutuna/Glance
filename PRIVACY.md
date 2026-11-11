# Privacy

Glance has no accounts, no analytics and no telemetry. It talks to the AI you choose and, optionally, to GitHub to look for a new release.

## What leaves your PC

When you read the screen, Glance takes a screenshot of the screen or area you picked and sends it, together with the prompt of the current mode and any follow-up questions you type, to the AI you selected under Connect an AI. Nothing else is sent, and only to that AI:

- Claude API, OpenAI, Gemini: the provider's own API address.
- OpenAI-compatible: the base URL you entered, which may be a server on your own PC.
- Claude CLI: Glance starts the `claude` program on your PC and hands it the screenshot. The CLI talks to Anthropic itself, under its own settings and terms.

If you list more than one AI, Glance tries the next one when a key fails or a limit is reached, so the same screenshot can go to each of them in turn. Each provider's privacy terms apply to what you send it.

Once a day Glance asks `api.github.com` for the latest release of aytutuna/Glance to tell you when a newer version exists. The request carries no data from your PC beyond what any web request carries (your IP address and the name "Glance"). Turn it off with Check for updates in the tray menu or the settings window.

## What is stored on your PC

All in `%APPDATA%\Glance`:

- `settings.json`: your options. API keys are encrypted with Windows DPAPI for your Windows account and are not kept as plain text. The file also holds the date of the last update check.
- `history.dat`: the last ten answers, only while Keep answers after closing is on, encrypted the same way. Turning the option off deletes it.
- `glance.log`: errors only, with the time and the error text. It holds no screenshots and no answers, but an error message from a provider or the CLI can be in it. When it passes 1 MB it is renamed to `glance.old.log` and a new one starts.

Screenshots are kept in memory while they are sent and are not written to disk by Glance.

Uninstalling does not remove `%APPDATA%\Glance`; delete the folder to remove everything.

## Contact

Open an issue at https://github.com/aytutuna/Glance.
