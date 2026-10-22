# ThatPet for Windows

A desktop pet that reacts to your AI coding agents: Claude Code, Codex, Cursor, OpenCode, Hermes Agent and
Antigravity.
One small native executable (`thatpet.exe`, about 1.9 MB) is the pet app and its own hook
for every agent. It implements the shared contract in `docs/BEHAVIOR.md` (v2.1, §1–16) and
reads `thatpet-pack/1` character packs (`docs/PACK-FORMAT.md`).

> **Status:** this build has been compiled and tested on macOS only. It has **not been run
> on Windows yet**. See [Verification](#verification) for what was and was not checked.

## Install

1. Unzip `ThatPet-<version>-windows-x64.zip` (or `-arm64`) to a permanent folder, for
   example `%LOCALAPPDATA%\Programs\ThatPet\`. Keep `packs\` and `WebView2Loader.dll` next
   to `thatpet.exe`.
2. Run `thatpet.exe`. The exe is not code-signed yet, so SmartScreen may warn the first time
   (More info, then Run anyway).
3. The welcome tour opens: meet ThatPet, choose your pet, connect your agents, follow along,
   done. Restart agent sessions that were already open so they pick up the hook.

From then on each connected agent starts the pet when a session starts, and the pet fades
out when the last session ends. After **Quit ThatPet** in her menu, agents leave her closed
(`run\quit-by-user`) until you open ThatPet again yourself, or it opens at login. Requirements: Windows 10 1803 or later, or Windows 11 (x64 or
ARM64). The welcome and license windows use the Microsoft Edge WebView2 Runtime, which ships
with Windows 11 and with current Windows 10. Without it the pet still works; only those two
windows are unavailable (a message says so).

To uninstall: menu **Agents** → click each connected agent to disconnect, untick **Settings →
Launch at Login**, quit, delete the folder, and optionally delete `%LOCALAPPDATA%\ThatPet\`.

## Agents

```text
thatpet.exe agents                                   status of every agent
thatpet.exe connect    <claude|codex|cursor|opencode|hermes|antigravity|all>
thatpet.exe disconnect <claude|codex|cursor|opencode|hermes|antigravity|all>
thatpet.exe hook --provider <agent>                  the hook itself (event JSON on stdin)
```

The menu's **Agents** submenu and the welcome tour do the same. Paths are under
`%USERPROFILE%`:

| Agent | File ThatPet edits | Shape |
| --- | --- | --- |
| Claude Code | `.claude\settings.json` | `{"hooks": {Event: [{"matcher", "hooks": [handler]}]}}`, Notification matcher `permission_prompt` |
| Codex | `.codex\hooks.json` + trust records in `.codex\config.toml` | grouped; handler has `"statusMessage": "ThatPet"` |
| Cursor | `.cursor\hooks.json` | flat, `"version": 1`, handlers without `type` |
| OpenCode | `.config\opencode\plugin\thatpet.js` (or `%XDG_CONFIG_HOME%\opencode`) | a plugin file ThatPet owns |
| Hermes Agent | `%LOCALAPPDATA%\hermes\plugins\thatpet\` (or `%HERMES_HOME%`) | a plugin folder ThatPet owns, switched on with `hermes plugins enable thatpet` (no console window) |
| Antigravity | `.gemini\config\hooks.json` (App, CLI and IDE) | ThatPet's own named hook `"thatpet"`; each handler runs `hook --provider antigravity --event <Event>` (5 s timeout) and the hook prints the answer Antigravity reads (`{"decision":"allow"}` for PreToolUse, `{}` otherwise) |

- The handler command is `C:/…/thatpet.exe hook --provider <agent>` with a 2 s timeout.
  Forward slashes work in cmd, PowerShell and Git Bash. The path is quoted only when it has
  to be (spaces), because a quoted path followed by arguments is a parse error in
  PowerShell.
- Every write keeps unknown keys, other people's hooks and the original key order. An
  existing file is copied to `<file>.thatpet-backup-<yyyyMMdd-HHmmss>` (UTC) first. Invalid
  JSON (or a non-object `hooks`) is refused and never touched. Connecting twice changes
  nothing.
- **Codex trust:** one `[hooks.state."<hooks.json path>:<event>:<group>:<handler>"]`
  `trusted_hash = "sha256:…"` record per ThatPet handler, with the hash format verified on
  macOS (`docs/CREDITS.md`). The path is the resolved absolute path with Windows
  separators. A key with backslashes is written as a TOML literal string
  (`[hooks.state.'C:\Users\…\hooks.json:stop:0:0']`), the form Rust's `toml_edit` uses;
  both quoting styles are read back. Disconnecting removes only ThatPet's records.
- **Status:** not installed / not connected / connected / needs update (an older
  `hook <state>` install or a moved exe) / hooks turned off (Codex `[features] hooks = false`).
- The older `thatpet.exe install-hooks` / `uninstall-hooks [--config-dir DIR]...` still work
  (Claude Code only, `hook <state>` form); **Connect** upgrades such an install.

### The hook

`thatpet.exe hook --provider <agent>` reads `hook_event_name` (or Cursor's `hookEventName`)
from stdin and maps it (Cursor's own names first):

| Event | State |
| --- | --- |
| SessionStart | idle |
| UserPromptSubmit, Progress (Cursor afterAgentThought/afterAgentResponse) | thinking |
| PreToolUse, PostToolUse, SubagentStop | working |
| PostToolUseFailure | toolfail (`oops`, then working) |
| PermissionRequest, Notification | attention |
| SubagentStart | helper (`helper`, then working) |
| Stop (Cursor `stop` unless status is `error`) | celebrating |
| StopFailure (Cursor `stop` with status `error`) | error |
| PreCompact | compacting |
| SessionEnd | stop |

Unknown events write nothing. The payload is never parsed as a whole: the event name,
session id (`session_id`, `sessionId`, `conversation_id` or `conversationId`), `status` and
`tool_name` are read straight from the bytes, so multi-megabyte tool payloads cost nothing.
Session ids keep only `[A-Za-z0-9_-]` (never dots: they are file names).

It writes `"<state> <unix_ms> agent=claude app=WindowsTerminal.exe apppid=1234 tool=Bash"`
atomically to `run\state`, records the session in `run\sessions\`, sends the same line to the
running pet over the fast path, and starts the pet (`thatpet.exe --from-hook`) if `run\pet.pid`
is missing or dead and `run\quit-by-user` does not exist. A pet started that way exits at once
while the marker exists; any other launch removes it. It prints nothing and always exits 0. `app` is the terminal or editor found by walking the
hook's parent processes (one Toolhelp snapshot): the nearest known GUI host
(`WindowsTerminal.exe`, `Code.exe`, `Cursor.exe`, `wezterm-gui.exe`, `Windsurf.exe`,
`alacritty.exe`, …), else the nearest shell (`powershell.exe`, `pwsh.exe`, `cmd.exe`), else
`TERM_PROGRAM`. `apppid` is that process, so the right window is found among several.

The older `thatpet.exe hook <state>` form keeps working.

**Fast path.** The pet binds a UDP socket on `127.0.0.1` (ephemeral port, loopback only)
and writes the port to `run\pet.port`; each hook sends its line as one datagram. Datagrams
from non-loopback senders and unknown states are dropped. The `run\` folder watcher
(`ReadDirectoryChangesW`, plus a 500 ms poll) stays as the fallback, and lines are
de-duplicated by (ms, state); a line older than the newest applied one is ignored, so the
file and the datagrams can arrive in any order.

`/clear` sends SessionEnd and SessionStart back to back, and two cases keep the pet alive:
a new state arriving during the 300 ms stop fade cancels the fade, and a state that arrives
while the process is already exiting starts a fresh pet (it waits for the old one's
single-instance mutex). Session files untouched for 24 h count as dead.

## Using it

- **Drag** the pet with the left mouse button: she is lifted by the collar and swings with
  your mouse speed. Let go to throw her.
- **Click** her (press and release within 4 px and 350 ms) to bring the agent's window to
  the front: the window of the `app=`/`apppid=` the last hook reported (a classic console
  window through its console host). With no agent seen yet she says *"Hi! Start your agent
  and I'll follow along."* Turn it off in Settings → Click Pet to Open Agent.
- **Speech bubble** (on by default): while the agent works, a small rounded label above her
  head (still while she types or hops: it sits over the highest frame of the animation and
  moves only when she does) says what it is doing for about 3 s ("Running a command…", "Editing code…",
  "Reading…", "Searching…", "Browsing the web…", "Asking a helper…", "Planning…",
  "Using github…", "Using <tool>…"). It fades in over 160 ms and out over 280 ms, and hides on
  done, error, idle, stop and approval.
- **Approval:** `attention` waves; after 30 s without an answer she waves harder
  (`attention_urgent`) and the reminder sound plays. A waiting approval lasts 10 minutes
  (other states 60 s).
- **Sounds** (off by default): a soft two-note chime when a turn finishes, a soft ping when
  approval is needed and on the 30 s reminder; at most one per 0.8 s. They are synthesized
  WAVs played with `PlaySound`, not the system's alert sounds.
- Clicks pass through everything except her visible pixels (alpha > 10). Particles and the
  bubble never catch clicks. The pet never takes focus.
- **Right-click** the pet, or click the tray icon, for the menu:

```text
Today: 12 turns · 1 h 20 min working        (when there was activity today)
Characters ▸   Opus 5.5 ✓ / Unlock Sonnet 5.5… 🔒 / … / Party Mode (up to 4 pets)
               Enter License Key… / Get More Characters…
Agents ▸       Claude Code (connected) ✓ / Connect Codex /
               OpenCode (not found) (click to connect or disconnect)
Size ▸         Tiny, Small, Normal, Large, Huge
Settings ▸     Wander Around, Sounds, Speech Bubble, Click Pet to Open Agent,
               Rare Surprise Chance ▸, Animations ▸ (When Thinking/Working/It Needs
               Approval/Done/Something Fails ▸ Default or another animation),
               Custom Event Images ▸, Display ▸ (several monitors), Launch at Login,
               Show in System Tray, Share Anonymous Usage
Play ▸         Surprise, Celebrate, Error, Attention (demo)
Welcome Tour…
Visit thatpet.app
Quit ThatPet
```

Agents show their own mark, locked characters a padlock. In party mode a character choice
changes the pet you clicked. Changing characters never moves the pets that stay: a pet whose
character is still chosen keeps running as it is, a new character takes over the place of
the pet it replaces, and only extra pets start somewhere new.

**Show in System Tray** (on by default): unticked, the tray icon goes away and her
right-click menu stays. Opening ThatPet again while it runs (Start menu, Explorer, a second
`thatpet.exe`) brings the icon back. Agent hooks never do.

## Characters and licence

Opus 5.5 is free; Sonnet 5.5, Haiku 5.5, Astra 6, DeepSeek V4.1 and Sol 6.1 need a key (`PET-XXXXX-XXXXX-XXXXX-XXXXX`)
from pet.thatlev.com. **Characters → Enter License Key…** (or a locked character, or "I
have a license key" in the tour) opens the License window: key field and Unlock, one row
per character (Free / Unlocked, or Buy · $4.99), "Get all eight for $29.99", and the keys on
this PC with "Remove from this PC".

- `<data>\installation-id`: 32 random bytes, base64url without padding, created once.
- `POST https://pet.thatlev.com/api/pet/license` `{installation_id, key?, app_version,
  os_version, mac_model}` (Windows sends the machine model there) →
  `{ok, token, signature}`; `token` is base64 of the JSON payload and `signature` is Ed25519
  over those bytes, checked against the built-in public key (`ed25519-dalek`). The payload
  must be `v: 1`, product `com.thatlev.thatpet` and installation
  `sha256_hex("thatpet-installation-v1|" + id)`. Only a verified answer is saved, to
  `<data>\license.json`; it is trusted offline afterwards and cannot be copied to another PC.
- Remove: `POST /api/pet/license/remove {installation_id, key}`. Buy:
  `https://thatpet.app/unlock/<pack|bundle>` in the browser; the site records the click and
  shows that character's card, where she is bought.
- The licence is refreshed silently at launch when `license.json` exists.
  `THATPET_UNLOCK_ALL=1` unlocks everything (development).
- HTTPS is WinHTTP on a worker thread: the system's TLS, certificate store and proxy
  settings (automatic proxy/PAC), and no TLS library in the exe.
- Besides these licence calls, the only other network traffic is the anonymous usage
  reports below (and the pages you open in the browser).

## Anonymous usage

Installs and activation are counted as in StillOn: a few anonymous reports from a closed
vocabulary, with no analytics SDK. **Settings → Share Anonymous Usage** (on by default)
turns them off. Turning it off sends one last `sharing_disabled` report and drops any report
still waiting for a retry, then nothing more. Turning it back on sends nothing extra; the
next heartbeat or launch resumes.

- `POST https://pet.thatlev.com/api/pet/telemetry` (`Content-Type: application/json`,
  `User-Agent: ThatPet/<version> (windows)`), over WinHTTP with 5 s timeouts on a background
  thread, one report at a time, so nothing ever waits for it. A report that fails waits in
  memory (at most 20, the oldest dropped) and is retried on the hourly tick; the queue is
  gone when the pet quits. The answer is ignored; only a 2xx counts as delivered.
- When: `launch` at every start (it also restarts the heartbeat clock); `heartbeat` from an
  hourly check, once 6 h have passed since the last heartbeat or launch;
  `onboarding_completed` when the welcome tour is finished with **Start ThatPet** for the
  first time; `agent_connected` when Connect/Update succeeds in the tour or the Agents menu;
  `first_reaction` for the first agent state the pet ever receives from a hook (fast path or
  `run\state`), saved as sent only after a 2xx (a failed one is not queued: the next agent
  state tries again); `licence_activated` when a key unlocks at least one more character;
  `sharing_disabled` as above. In-app use, each time it happens (the server keeps daily
  counters and never alerts on them): `unlock_clicked` (`placement` `welcome` for a locked
  card or its Unlock, `licence` for Buy or "Get all eight", `menu` for a locked character;
  `sku`), `licence_opened` (`welcome` for "I have a license key", `menu`), `site_opened`
  (`menu`), `menu_opened` (`menu_bar` from the tray icon, `pet` from a right-click),
  `agent_disconnected` (`provider`), `size_changed` (`size`: `tiny` to `huge`),
  `character_changed`, `menu_bar_toggled` (Show in System Tray: `placement` `menu` or
  `relaunch`, `enabled`) and `party_mode` (`enabled`). `launch` and `heartbeat` also carry
  `work_minutes` and `turns`: the agent work (busy minutes and finished turns, as in the Today
  counter) recorded since the last report that carried it, kept in `telemetry.json` until then.
- What: exactly these keys, each from a closed set. `event`; `installation_id` (the
  licence's random `installation-id`, created if missing); `platform` `windows`;
  `app_version`; `build_version` `0`; `os_version` (`10.0.26100`: major.minor.build);
  `architecture` (`x86_64` or `arm64`, the exe's); `install_channel` (the first word of
  `install-channel` if it is `script`, `dmg`, `zip`, `package` or `source`, else `unknown`);
  `character` (the current one: `opus55`, `sonnet55`, `haiku55`, `astra6`, else `other`);
  `paid_packs` (0–3, the paid characters a verified licence unlocks); the extra keys of the
  event above and no others; `test: true` only when `THATPET_TELEMETRY_TEST=1`. Each body is
  checked against this list before it leaves.
- Never sent: prompts, tool names, paths, session ids, user, host or machine names, emails
  or licence keys.

## Welcome tour and License window

First launch (`onboarded` false) opens an 860×560 window with five pages, as on macOS:
**Meet ThatPet** (app icon) → **Choose your pet** (six cards; locked ones show a padlock
and "Unlock · $4.99", and clicking anywhere on a locked card opens her page on thatpet.app
like its button; "I have a license key" opens the key sheet) → **Connect your agents**
(one row per agent: its mark, name, a one-line status ("Found on this PC" when it is installed), and Connect, a spinner while
connecting, or a green check) → **Follow along** (GitHub and X rows; each Follow opens the
link and turns into a check; Continue unlocks when both are done) → **You're all set** (the
chosen character). Footer: page dots, Back, the primary button; Enter is the primary
button, Escape closes the key sheet. Closing the window counts as skipping the tour; **Welcome
Tour…** in the menu opens it again. Moving between pages never moves the pet: only a changed
choice on Choose your pet swaps the character, where she stands.

Look: follows the Windows app theme (`AppsUseLightTheme`), live when it changes; flat
background (#F3F3F3 / #202020) with the caption bar in the same colour
(`DWMWA_CAPTION_COLOR`, dark caption via `DWMWA_USE_IMMERSIVE_DARK_MODE`) and rounded window
corners (`DWMWA_WINDOW_CORNER_PREFERENCE`, Windows 11); rows and cards are translucent fills
(4.5 % / 7.5 % when highlighted) with a 1 px 8 % stroke, radius 12 for rows and 16 for cards
and large rows, capsule buttons; primary buttons are monochrome (near-black in light,
near-white in dark); violet (#7354DB / #A88FFF) only for the selection outline, focus ring
and links; success green #22994D / #66D98C; Segoe UI Variable (Segoe UI on Windows 10);
titles 22–34 px bold. Screenshots: `test-artifacts/ui/`.

### Why WebView2 for these two windows

The pet itself stays native: per-pixel-alpha layered windows fed by the Rust compositor.
For the two dialog windows I compared three options:

- **Direct2D/DirectWrite by hand:** native and light, but every control (text field with
  caret, selection, IME and clipboard; focus and keyboard navigation; spinner; screen-reader
  support) is written from scratch, and none of it can be rendered or looked at on the Mac
  this is built on.
- **Immediate-mode UI over the compositor:** same problems, plus a font rasterizer and no
  native text input.
- **WebView2 with a local page (chosen):** the system Edge runtime draws Segoe UI Variable
  with ClearType, real text input, focus rings, keyboard navigation and UI Automation
  (Narrator) for free, and the exact same page (`core/ui/thatpet.html`) renders in any
  browser, so every page was screenshotted and checked against the macOS reference here.

Constraints that keep it safe and small:

- The page is one self-contained HTML file compiled into the exe. Images arrive as `data:`
  URIs, and a Content-Security-Policy forbids any other source. Every navigation after the
  first is cancelled; the only links that open (in the default browser) are GitHub, X,
  thatpet.app and its `/unlock/<sku>` pages, checked by the host (`ui::allowed_external`).
- The page never chooses URLs, paths or agents: it sends small commands
  (`{"cmd":"connect","agent":"codex"}`) that the host parses strictly (`ui::parse_command`,
  unit-tested) and acts on.
- `WebView2Loader.dll` is loaded with `LoadLibrary` only when a window opens; the exe does not
  import it, so the pet and the hooks start without it. Its data goes to
  `<data>\WebView2\`. Dev tools, context menus, zoom, autofill and browser accelerator keys
  are off. The browser processes (~100 MB) exist only while one of the two windows is open.
- Creation is asynchronous, so the pet keeps animating while the window loads.

## Settings

`%LOCALAPPDATA%\ThatPet\settings.json` is written by the menu and the tour. You can also edit
it while the pet is not running. Unknown keys are preserved.

```json
{
  "characters": ["opus55"],
  "size": 1.0,
  "wander": true,
  "rare_chance": 0.03,
  "custom_events": {"approval": true, "done": true, "error": true, "idle": true, "thinking": true},
  "display": null,
  "launch_at_login": false,
  "sounds": false,
  "bubble": true,
  "focus_on_click": true,
  "event_animations": {"attention": "attention_urgent"},
  "onboarded": true,
  "share_usage": true,
  "show_in_tray": true
}
```

- `characters`: pack ids. More than one means party mode (at most 4). A locked character
  stays in the list and appears once its licence arrives.
- `display`: a monitor device name such as `\\.\DISPLAY2`, or `null` for the main one.
- `launch_at_login`: mirrors the `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
  value `ThatPet`. It is off by default and needs no admin rights.
- `event_animations`: state (`thinking`, `working`, `attention`, `celebrating`, `error`) →
  the animation to play instead. Unknown names fall back to the default.
- `share_usage`: the anonymous usage reports (Settings → Share Anonymous Usage, on by
  default). See [Anonymous usage](#anonymous-usage).
- `show_in_tray`: the tray icon (Settings → Show in System Tray, on by default).

## Data directory

`%LOCALAPPDATA%\ThatPet\` (override with the `THATPET_DATA_DIR` environment variable):

```text
settings.json
license.json            the server's signed answer (when a key was entered)
installation-id         this PC's random id (licence and anonymous usage reports)
telemetry.json          {"last_heartbeat": <unix s>, "first_reaction_sent": <bool>}
install-channel         how ThatPet was installed: "script" from the one-line installer
                        (UTF-8, or UTF-16 with a BOM as PowerShell's Out-File writes it)
stats.json              today's turns and busy time (the menu's first line)
run\state               "<state> <unix_ms> key=value…", written by the hooks
run\sessions\<id>       one empty file per live agent session
run\pet.pid             pid of the running pet
run\pet.port            the pet's fast-path UDP port on 127.0.0.1
events\<event>\         custom images (approval, done, error, thinking, idle)
packs\<id>\             user packs (override built-in packs with the same id)
WebView2\               the welcome/license windows' browser data
log.txt                 diagnostics, capped at 256 KB
```

## Custom event images

Put `.png`, `.gif` (animated) or `.apng` files in `events\<event>\`. **Settings → Custom Event
Images → Open Custom Images Folder** creates the folders for you. When the event fires and
its toggle is on, one file is picked at random. It replaces the sprite, scaled to the pet's
height on the same floor line, with a 150 ms crossfade. `approval` shows until the agent
state changes. The other events show for 4 s. An empty or missing folder means the built-in
animation plays.

| event | fires on |
| --- | --- |
| `approval` | `attention` (permission request) |
| `done` | `celebrating` (turn finished; a rare surprise roll wins first) |
| `error` | `error` |
| `thinking` | `thinking` (then the built-in thinking animation continues) |
| `idle` | `idle` (session start) |

## Packs

Built-in packs ship in `packs\` next to the exe. User packs go in `%LOCALAPPDATA%\ThatPet\packs\`
and override built-in packs with the same id. Broken packs are skipped, and the reason is
written to `log.txt`; run `thatpet.exe list-packs` to see what is found. Every pose is
decoded once, cropped to its visible area and pre-scaled for the current size and DPI. PNGs
are never decoded per frame. v2 needs `oops`, `helper` and `attention_urgent`; packs that
lack them get the defaults from `tools/animations.json`.

## Command line

```text
thatpet.exe                                  run the pet
thatpet.exe hook --provider <agent>          agent hook (stdin: hook JSON)
thatpet.exe hook <state>                     older Claude Code hook form
thatpet.exe agents | connect <agent|all> | disconnect <agent|all>
thatpet.exe install-hooks / uninstall-hooks [--config-dir DIR]...
thatpet.exe render --pack ID --anim NAME --out DIR [--fps 60] [--size 1] [--scale 1]
                   [--seed 1] [--duration S] [--packs-dir DIR]...
thatpet.exe list-packs [--packs-dir DIR]...
thatpet.exe ui-preview --out DIR [--packs-dir DIR]...
thatpet.exe --version | --help
```

Environment variables: `THATPET_DATA_DIR` (data folder), `THATPET_PACKS_DIR` (extra pack
folder, highest priority), `THATPET_NO_SPAWN` (hook mode never starts the pet; used by
tests), `THATPET_UNLOCK_ALL=1`, `THATPET_ONBOARDING_PAGE=0…4` (open the tour at a page),
`THATPET_SHOW_LICENSE=1` (open the License window at start) and `THATPET_TELEMETRY_TEST=1`
(anonymous usage reports are marked `test`) for QA.

`render` writes `frame_00000.png` and the following frames, all transparent RGBA, plus
`meta.json`. Fx and particles are included, and the output is deterministic for a given
`--seed`. `ui-preview` writes every page of the tour and the License window, light and dark,
as standalone HTML (the page WebView2 shows, with sample data), for screenshots and review in
any browser. CLI output goes to the console the command was started from. Interactive `cmd`
and PowerShell do not wait for GUI programs, so for scripts use
`start /wait thatpet.exe connect all` in `cmd`, or `thatpet.exe agents | Out-String` in
PowerShell.

## How it works

- **`core/`** (`thatpet-core`) is platform-independent and holds all the behaviour. It is
  fully unit-tested on any OS.
  - `pack`, `anim`, `fx`, `pet`, `motion`, `particles`, `blit`, `compositor`, `custom`,
    `render`: the pet (unchanged architecture from v1).
  - `brain`: the agent state machine, including toolfail/helper, the 30 s approval
    escalation and reminder, the 10-minute approval timeout and `event_animations`.
  - `agents`: event map, bubble texts, payload field extraction, sanitizing, host picking.
  - `hook`: both hook forms and the state line; `fastpath`: the UDP listener, sender and
    de-duplication.
  - `connector`: the six connectors, Codex trust records, the OpenCode plugin, the Hermes Agent plugin folder and
    Antigravity's named hook.
  - `license`: installation id, Ed25519 verification, the server protocol over a
    `Transport` trait (WinHTTP on Windows, fakes in tests).
  - `telemetry`: the anonymous usage reports: the body and its validation, the facts
    (OS version, architecture, install channel, character, paid packs), when to report
    (`telemetry.json`), the retry queue and the sender thread, over a `Poster` trait
    (WinHTTP on Windows, fakes in tests).
  - `settings`, `stats` (today counter), `sound` (synthesized chimes), `bubble` (label
    drawing and timing), `ui` (the tour/license page, its model and its messages).
- **`app/src/win/`** is the thin Win32 layer, built on the `windows` crate:
  - Each pet has two per-pixel-alpha layered windows (`WS_EX_LAYERED | WS_EX_TOOLWINDOW |
    WS_EX_TOPMOST | WS_EX_NOACTIVATE`): the hit-testable sprite (pixels with alpha ≤ 10 are
    cleared, so they click through) and the always click-through particle overlay. A third,
    click-through window shows the speech bubble; its text is rasterized with GDI
    (grayscale antialiasing) and the label drawn by `core::bubble`, faded with
    `SourceConstantAlpha`.
  - `web.rs` hosts WebView2 for the tour and License windows; `focus.rs` brings the agent's
    window forward (`EnumWindows`, console host lookup, `SetForegroundWindow` with
    thread-input and synthesized-key fallbacks); `procs.rs` walks the process tree; `net.rs` is the
    WinHTTP client (licence and usage reports); `tray.rs` shows the app icon; `sys.rs` has
    monitors, registry, theme, sound and menu-bitmap helpers.
  - Slow work (connecting an agent, licence calls) runs on worker threads that post a wake-up
    message; window procedures only queue events, and the loop owns all state, so modal
    loops (menus, message boxes) cannot re-enter it. Usage reports go one at a time through
    their own sender thread; only `first_reaction`'s outcome comes back to the loop.
- **Frame pacing, DPI, plumbing:** as in v1: a high-resolution waitable timer at the
  monitor's refresh rate while anything moves (now also while the bubble fades), 30 Hz when
  she only breathes; per-monitor DPI aware (v2); single instance per data directory;
  `#![windows_subsystem = "windows"]`, so no console ever flashes.
- **Icons:** `app/res/thatpet.ico` (16–256 px, from `design/icon/ThatPet-icon-source.png`)
  is the exe, window and tray icon; `app/res/marks/*.png` are the agent marks for the menu,
  rasterized from `apps/macos/Resources/ProviderIcon-*.svg`. `tools/rasterize-assets.sh`
  regenerates both; the welcome page inlines the SVG marks so they follow the text colour.

## Interpretations of the contract

These are places where `docs/BEHAVIOR.md` left room. Each is decided once here.

- `hold: true` starts on a random frame; crossfades are additive (no alpha dip);
  `land_squash` uses scaleX = scaleY^−0.74; pendulum angles use y-down screen coordinates;
  particles are anchored to the base position (all as in v1).
- If an agent state arrives while the pet climbs or hangs from the ceiling, she drops,
  lands, then plays it. If she is dragged during a looping state, it resumes after landing
  (`attention_urgent` if the approval had already escalated).
- `toolfail`/`helper` play `oops`/`helper` even when they arrive while she wanders (the
  macOS app ignores them until the next `working`); a pack without those animations plays
  `working` and an already playing `working` is not restarted.
- The approval reminder sound fires after 30 s even when the user picked another
  animation for `attention` (the animation only escalates from the default `attention`).
- A stale `attention` line (pet started up to 10 minutes after the request) is still shown.
- "Attention (demo)" lasts 6 s and never escalates.
- Closing the welcome window counts as skipping it (`onboarded` becomes true), like "Start
  ThatPet". If WebView2 cannot start, the tour is offered again at the next launch. The tour
  is always available from the menu.
- Backups are named in UTC (as `install-hooks` always did); macOS uses local time.
- The bubble and the "Hi!" line show on the first pet (the clicked one for "Hi!"), as on
  macOS.
- The tray icon is the app icon (v1 used the character's face).
- Walk poses are never mirrored. Only poses with `"mirror": true` are mirrored, and only
  `climb` on the right wall does that.
- Not done: hiding the pet while a fullscreen app or game is in front. The pet stays
  topmost.
- Usage reports (§16): `onboarding_completed` needs **Start ThatPet** on the last page;
  closing the tour still sets `onboarded` (a skip, see above) but sends nothing.
  `licence_activated` needs at least one character that was locked before the key, and
  `paid_packs` counts only a verified licence (`THATPET_UNLOCK_ALL` does not). `character`
  is the first pet's in party mode. `architecture` is the exe's, so the x64 build on an
  ARM64 PC reports `x86_64`. A state already in `run\state` when the pet starts counts for
  `first_reaction` too. `last_heartbeat` is saved when the heartbeat is handed to the sender
  (a failed one is retried from the queue, never sent twice). `sharing_disabled` is tried once and never
  queued. An hourly retry stops at the first network error, so the rest do not each wait
  for a timeout; they wait for the next hour. The Windows version comes from the registry
  (`CurrentMajorVersionNumber`, `CurrentMinorVersionNumber`, `CurrentBuild`), as the licence
  calls already read it, and drops the build number if it is missing.

## Verification

Done on macOS 27 (Apple Silicon). There is no Windows machine.

- `cargo test`: **124 tests pass** (78 core unit, 14 agents, 5 hooks, 4 packs, 15 pet,
  2 render integration, 6 that run the built binary), plus 1 ignored CPU-estimate test. New
  in v2.1 (0.3.0), the anonymous usage reports, all without a network (a fake `Poster`):
  - The body has exactly the contract's keys in its order, `provider` only on
    `agent_connected`, `test` only when set; out-of-set characters read as `other`,
    `paid_packs` is capped at 3, odd versions read as `0`, and no path ever gets in.
  - Validation refuses unknown and missing keys and every out-of-set value (event,
    installation id, platform, versions, architecture, channel, character, paid packs,
    provider case, `test: false`), so an invalid body is never built.
  - `install-channel` parsing (first word, case, UTF-8 with and without a BOM, UTF-16 LE/BE,
    junk), the Windows version, the architecture and paid-pack mapping, the test flag, the
    User-Agent, and the context read from a temp data folder (the shared installation id).
  - Timing: a launch saves the heartbeat clock, the hourly checks stay quiet for 6 h and then
    send and save one heartbeat; sharing off sends and saves nothing; a far-future
    `last_heartbeat` does not block heartbeats; odd `telemetry.json` files read as defaults.
  - `first_reaction` goes out one at a time, is retried on the next agent state after a
    failure and saved (and never sent again) only after a 2xx.
  - The queue keeps at most 20 failed reports (the oldest dropped) and never
    `first_reaction`; the hourly retry goes oldest first, keeps what fails and stops at a
    network error; the sender thread reports outcomes, and turning sharing off drops the
    queue and sends `sharing_disabled` once.
  - `share_usage` defaults to on (also for a settings file from before) and round-trips.

  New in v2:
  - The event map for Claude/Codex and Cursor (including `stop` with `status: error`), tool
    extraction and sanitizing, bubble texts, session ids that never keep dots, payload field
    extraction, host picking from a process chain.
  - Connectors on temp homes (never the real one): Claude keeps user hooks and key order,
    is idempotent with exactly one backup, disconnect restores the original file; an old
    `hook <state>` install reads as needs-update and Connect upgrades it; Codex writes hooks
    with `statusMessage` and one trust record per event (count and hash checked), keeps
    other people's records, reports and refuses `hooks = false`; Cursor is flat with
    `version: 1`; OpenCode's plugin is owned, needs-update when the exe moves, and someone
    else's file is never replaced or removed; invalid JSON is never touched (no backup
    either); Windows paths in TOML keys round-trip.
  - The provider hook end to end (state line with agent/app/apppid/tool, unknown events,
    the last SessionEnd), plus the binary's `hook --provider`, `agents`, `connect`,
    `disconnect` and `ui-preview` on temp folders.
  - The fast path: a hook line reaches a live listener over loopback UDP, junk is dropped,
    the port file goes away with the pet, de-duplication and out-of-order lines.
  - Licence: a payload signed with a test key verifies; tampered token or signature,
    another installation, another key, wrong product or version all fail; only verified
    answers are stored; server errors, network failures and junk answers surface the macOS
    messages; the request body and URLs.
  - Brain and pet: toolfail plays `oops` then `working` and a following `working` does not
    cut it short; helper; approval escalates to `attention_urgent` at 30 s with one reminder
    and still waits after 60 s; approving goes to `grateful`; demo attention; user-chosen
    animations and the unknown-name fallback.
  - Settings round trip of the new keys, today's stats (turns, busy time, day reset, the
    4 h cap), the synthesized WAVs, the bubble drawing, placement and fade timing, the page
    commands and safe state injection.
- `cargo clippy --all-targets -D warnings` is clean for the host, `x86_64-pc-windows-gnu`
  and `aarch64-pc-windows-gnullvm`.
- Cross-compiles: `x86_64-pc-windows-msvc` gives `PE32+ executable (GUI) x86-64` (2.4 MB) and
  `aarch64-pc-windows-msvc` gives `PE32+ executable (GUI) Aarch64` (2.1 MB). Both embed the
  icon (32-bit bitmaps, so the resource section isn't compressed data), manifest and version info
  (with LegalCopyright), carry a valid PE checksum, and import only system DLLs (no
  `WebView2Loader.dll`, no Visual C++ runtime).
- `render` still works, including the new `oops`, `helper` and `attention_urgent`.
- The tour and License pages were rendered offscreen with `thatpet ui-preview` and headless
  Chrome at 200 %, light and dark, and compared with `design/reference-macos/`:
  `test-artifacts/ui/` (every page, the key sheet, the half-done follow page, the License
  window with and without a key, and `bubble-200pct.png`, the speech bubble drawn by the real
  `core::bubble` code around a stand-in text mask). On the Mac the font falls back to the
  system font; Windows uses Segoe UI Variable. Compared with the refreshed macOS references
  (flat, monochrome primary buttons, violet only for selection and links, connected rows
  outlined in violet) and the Linux port's screenshots.
- An independent read-only review of the Win32/WebView2 layer against Win32 semantics. Its
  findings are fixed: a missing `GdiFlush` before reading the bubble's text bits; a web window
  closed before WebView2 finished starting no longer leaves an orphaned browser; a per-window
  background brush instead of a shared class brush; window sizing at the target monitor's DPI;
  `WebView2Loader.dll` loaded only from the exe's folder; a browser-process crash closes the
  window with a message; focus is no longer handed back to the terminal after opening a URL or
  folder; the tray icon is retried until the shell accepts it; quitting works with no pet on
  screen; `WM_ENDSESSION` cleans up synchronously; the fast-path listener cannot hang shutdown;
  click-to-focus uses only non-blocking calls and an unassigned key (F24) instead of an Alt tap.

**Not verified, because nothing has run on Windows.** Every Win32 and WebView2 call
compiles against `windows` 0.62 and `webview2-com` 0.39 but has never executed. Check these
first on a real machine:

1. The pet appears: layered windows, alpha, click-through on transparent and faint pixels,
   no focus stealing; the right-click menu (bitmaps for marks and padlocks, submenus,
   checks) and focus returning to the terminal afterwards; the tray icon including after an
   Explorer restart.
2. The welcome tour and License window: WebView2 starts from the bundled loader, the page
   loads, light/dark follows the app theme (and switches live), caption colour and rounded
   corners on Windows 11, keyboard (Tab, Enter, Escape), Connect with spinner and check,
   Follow and Buy open the browser, the License window sizes itself to its content.
3. A real session of each agent: Claude Code (hooks run under Git Bash), Codex (does it
   accept the trust records with Windows paths as written?), Cursor, OpenCode (the plugin
   spawning the exe). The pet starts, reacts, and stops; Claude Code does not wait for the
   detached pet.
4. `app=` detection in Windows Terminal, VS Code, Cursor and a classic console; click-to-focus
   bringing each to the front (foreground-lock rules).
5. The fast path (no firewall prompt for a loopback-only socket), the speech bubble (font,
   placement at every edge, fades), sounds (volume, throttle).
6. Licence calls and the anonymous usage reports (2xx status read with
   `WinHttpQueryHeaders`) over WinHTTP against pet.thatlev.com, including behind a proxy;
   Settings → Share Anonymous Usage and its final `sharing_disabled`.
7. Mixed-DPI multi-monitor setups, the taskbar on any edge, auto-hide; CPU in Task Manager
   (< 1 % idle, < 3 % walking), memory, cold start; launch at login, sleep and resume.

## Risks

- **Codex trust on Windows:** the key path and quoting follow the contract and TOML, but
  Codex's own normalization of the hooks.json path on Windows (case, `\\?\` prefix, quoting)
  is unverified. If Codex keys it differently, it asks the user to trust the hooks once;
  nothing breaks.
- **Agent shells:** the hook command is unquoted unless the path has spaces. If an agent runs
  hooks through PowerShell and the install path has spaces, the quoted form fails there; the
  default `%LOCALAPPDATA%\Programs\ThatPet` usually has none. Check Codex and Cursor on Windows.
- **Hook start-up:** the exe now also imports `winhttp.dll`, `dwmapi.dll`, `winmm.dll` and
  COM DLLs (the GNU toolchain has no delay-loading). These are system DLLs, but every hook
  process maps them; measure the hook against the 50 ms budget on real hardware. The
  remedy, if needed, is a separate tiny `thatpet-hook.exe`.
- **Busy cursor:** every hook launches a GUI-subsystem process. Hook mode makes itself
  input-idle at once to end the "app starting" cursor, and the pet is spawned with
  `STARTF_FORCEOFFFEEDBACK`.
- **Foreground rules:** a pet started by a hook may not be allowed to bring the welcome
  window to the front (it flashes in the taskbar instead), and click-to-focus relies on the
  click giving the pet foreground rights; the thread-input and synthesized-key fallbacks
  cover the usual cases (not an elevated foreground window, by design of UIPI).
- **WebView2:** missing runtime (old Windows 10 images, some LTSC) disables only the tour and
  License window. Page sizes are ~600 KB (NavigateToString allows 2 MB; larger pages load
  from a temp file).
- **Hook spawn, topmost, timer:** as in v1 (breakaway from job objects, topmost re-asserted
  every 5 s, high-resolution timers need Windows 10 1803+).
- **Download size:** the zip is 14.5 MB with today's four packs (budget 15 MB), so there is little headroom;
  `build.sh` warns when it grows past the budget.
- **Unsigned exe:** SmartScreen and antivirus heuristics may flag a new unsigned binary that
  spawns detached processes and opens a localhost socket. Code signing is needed before wide
  distribution.

## Credits

- Agent marks (`ProviderIcon-*.svg`, rasterized to `app/res/marks/`): CodexBar, MIT (see
  `docs/CREDITS.md`).
- The X glyph on the Follow page: Simple Icons, CC0.
- `WebView2Loader.dll`: Microsoft's redistributable WebView2 loader (shipped next to the exe,
  taken from the `webview2-com-sys` crate).

## Building

- On macOS: `./build.sh` runs the tests and clippy, cross-builds x64 and arm64 for the MSVC targets
  (`cargo xwin`: clang-cl, llvm-rc and lld-link with Microsoft's CRT and SDK; static CRT, `/RELEASE` for a
  valid PE checksum, no build path in the exe), validates and recompresses the packs, adds
  `WebView2Loader.dll`, and writes `dist/*.zip`. The release used MinGW (`x86_64-pc-windows-gnu`) until
  October 2026; that exe was flagged by four machine-learning antivirus engines on VirusTotal (PROJECT.md
  "Windows antivirus").
- On Windows, see [BUILD-ON-WINDOWS.md](BUILD-ON-WINDOWS.md).
- `tools/rasterize-assets.sh` regenerates the icon and the menu marks.

`target/` and `dist/` are git-ignored.
