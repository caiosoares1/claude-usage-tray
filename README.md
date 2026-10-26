# Claude Usage Tray

A Windows system tray app that shows plan usage limits (5-hour session and weekly) for **several Claude accounts at once**, warns you when an account is close to its limit, and switches the account Claude Code uses with a single click.

![Panel](docs/painel.png)

> The app's interface is in Brazilian Portuguese.

## Features

- **Tray icon** with one bar per account, filled by that account's tightest limit: green (< 70%), yellow (70–90%), red (≥ 90%).
- **Panel** (left-click the icon) showing the 5-hour session, the weekly limit and any other limits the plan has, with the percentage used and when each one resets.
- **Notifications** when an account crosses 80%, hits 100%, and when it frees up again.
- **Quick account switching**: the chosen account becomes Claude Code's default, while your settings, plugins and history in `~/.claude` stay as they are.
- **Automatic suggestion**: when the active account crosses 80%, the notification offers to switch to the account with the most headroom.
- **Token refresh**: the app renews OAuth tokens as they expire (they last about 8 hours), so idle accounts keep showing up.
- **Starts with Windows** (enabled on first run, can be turned off from the menu).

## Requirements

- Windows 10 or 11
- .NET Framework 4.x, which ships with Windows (no SDK needed)
- [Claude Code](https://code.claude.com) installed (`claude` on your PATH) and Claude accounts on a Pro, Max or Team plan

## Installation

```powershell
git clone https://github.com/caiosoares1/claude-usage-tray.git
cd claude-usage-tray
.\build.ps1
.\ClaudeUsageTray.exe
```

`build.ps1` compiles with the `csc.exe` that comes with .NET Framework and writes `ClaudeUsageTray.exe` to the project folder. On first run the app registers itself to start with Windows, pointing at that `.exe`. If you move the file, run it once from its new location and the path is updated.

If the icon lands in the hidden icons area (the `^` arrow on the taskbar), drag it onto the taskbar to keep it visible.

## Adding accounts

The account logged in to Claude Code (`~/.claude`) shows up automatically. For the others:

1. Right-click the icon and choose **Adicionar conta...** (Add account).
2. Pick a short name (it becomes the folder `%USERPROFILE%\.claude-contas\<name>`).
3. A terminal opens running `claude auth login`: sign in with the account you want.

> The login uses whichever account is signed in to your browser. Before authorizing, switch accounts on claude.ai or open the link in a private window.

Each folder in `.claude-contas` is an independent `CLAUDE_CONFIG_DIR`. To use a specific account in a single terminal only, click it in the panel and choose **Copiar comando para usar no terminal** (Copy command to use in the terminal).

## Switching Claude Code's account

From the icon menu (**Trocar conta do Claude Code**), from the panel (click the account → **Usar no Claude Code**), or by clicking the suggestion notification.

When switching, the app **moves** the login between `~/.claude` and the chosen account's folder: the `.credentials.json` file and the `oauthAccount` field of `.claude.json`. Nothing else in `~/.claude.json` is touched. Moving instead of copying is deliberate: the refresh token rotates on every renewal, so two copies of the same login would end up invalidating each other.

As a result:

- The folders in `.claude-contas` act as "slots": after a switch, the folder `someone` may hold a different account. The panel always shows the right email.
- Open a new Claude Code session after switching. In sessions that were already open, check with `/status`.

## Menu (right-click)

| Item | What it does |
| --- | --- |
| Abrir painel | Opens the panel (same as left-click) |
| Trocar conta do Claude Code | Lists accounts with current usage; includes a shortcut to the account with the most headroom |
| Atualizar agora | Fetches usage for all accounts now (`F5` in the panel does the same) |
| Adicionar conta... | Creates the account folder and opens the login |
| Abrir pasta das contas | Opens `%USERPROFILE%\.claude-contas` |
| Atualizar a cada | Polling interval: 2, 5 (default), 10, 15 or 30 min |
| Notificações | Turns notifications on/off |
| Iniciar com o Windows | Turns the entry in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` on/off |

Preferences are stored in `%APPDATA%\ClaudeUsageTray\settings.json`.

## How it works

- Usage comes from `GET https://api.anthropic.com/api/oauth/usage`, the same endpoint Claude Code's `/usage` command calls, authenticated with the OAuth token from each `.credentials.json`.
- Each account's email comes from `GET /api/oauth/profile`.
- When a token is about to expire, the app renews it at `https://platform.claude.com/v1/oauth/token` and writes the result back to `.credentials.json` (atomically). Claude Code notices the file change and picks up the new token.
- Right after a limit resets, the app polls once more to refresh the numbers.

## Caveats

- **Unofficial project**, not affiliated with Anthropic.
- The `/api/oauth/usage` endpoint is **undocumented** and may change without notice.
- Tokens are stored in plain text in the `.credentials.json` files, the same way Claude Code already stores them on Windows. Anyone with access to your Windows user can read them.
- If a future Claude Code version starts storing logins in Windows Credential Manager instead of the file, the app will no longer see or switch accounts.

## Uninstalling

1. Right-click the icon, uncheck **Iniciar com o Windows** and click **Sair** (Exit).
2. Delete the project folder and `%APPDATA%\ClaudeUsageTray`.
3. If you no longer want the extra accounts, delete `%USERPROFILE%\.claude-contas` (this signs those accounts out of this machine).
