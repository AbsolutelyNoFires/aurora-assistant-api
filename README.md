# Aurora Assistant API

An [AuroraPatch](https://github.com/Aurora-Modders/AuroraPatch) patch for [Aurora 4X](http://aurora2.pentarch.org/)
(C# edition) that lets other programs **read and operate the running game over HTTP**: every open window as text,
a live stream of what the player does, and player-like actions (click, type, select, answer message boxes).

It was built for LLM assistants (see [aurora-assistant-bridge](http://forgejo/yecenia/aurora-assistant-bridge)), but
it has no AI in it and any tool can use it.

**Version 0.1.0** — tested with Aurora 2.7.1 on Windows-compatible .NET Framework 4.8 (run under Proton on Linux).
Other Aurora versions are untested.

## Install

1. Install [AuroraPatch](https://github.com/Aurora-Modders/AuroraPatch) and its **Lib** patch, and start Aurora
   with `AuroraPatch.exe` as usual.
2. Unzip `aurora-assistant-api-<version>.zip` into your Aurora folder, so you get
   `Aurora\Patches\AuroraAssistantApi\AuroraAssistantApi.dll`.
3. Start `AuroraPatch.exe`. **AuroraAssistantApi** appears in the patch list; press **Start Aurora**.
4. Check it is running: open <http://127.0.0.1:47100/health> in a browser.

## Settings

Select **AuroraAssistantApi** in the AuroraPatch launcher and press **Change settings**:

- **Accept API connections from:** *this computer only* (default, `127.0.0.1`) or *LAN and Tailscale* (all network
  interfaces). There is no authentication: anyone who can reach the port can read and operate your game.
- **Port:** default `47100`.
- **Command to run when Aurora starts** (optional), run through `cmd.exe` — e.g. `aurora-assistant` to start the
  bridge with the game on Windows. Under Proton the game runs inside the Steam Runtime container and cannot start
  Linux programs (`start /unix` does not work there), so start the bridge from the script that launches Aurora.

Settings are saved in `Patches\AuroraAssistantApi\settings.json` and apply the next time Aurora starts.

## API

All responses are JSON unless noted. Windows are named by their title without the date/wealth suffix some Aurora
windows append (`Economics`, `Class Design`); the main map is `Tactical Map`.

| Method | Path | |
|---|---|---|
| GET | `/health` | `api`, `version`, Aurora checksum, map title (race, game date, wealth) |
| GET | `/forms` | Open windows: `id`, `name`, `title`, `active`, … |
| GET | `/forms/{name or id}` | The window as indented text (see below); `?format=json` for the tree, `hidden=1` to include hidden controls, `items=1` for all drop-down options, `alltree=1` to expand collapsed tree nodes, `max=N` rows per list (default 200) |
| POST | `/forms/{window}/controls/{control}/{action}` | `click`, `set` (`value`: text, number, `true`/`false` for check/radio boxes), `select` (`value` or `index`; tree paths as `Parent > Child`; tab strips by tab text), `doubleclick` (optional `value` selects the item first). Arguments as query parameters or a JSON body; `wait` = ms to wait (default 3000) |
| GET | `/events?since=N&wait=ms&limit=N` | Long-poll the UI event stream |
| GET | `/dialogs` | Open message boxes: `id`, `message`, `buttons` |
| POST | `/dialogs/{id}/click?button=OK` | Press a message box button |

Actions return `{"status": "done"}`, `{"status": "running"}` when the action opened a modal window or message box
and is waiting on it (answer it, then the action completes), or `{"status": "error", "error": …}`.

### Window text

```
Form ClassDesign "Class Design"
  TabControl tabDesign tab="Class Design" of [Class Design, Ships in Class, Components, …]
    ComboBox cboHullDescription = "Freighter  FT" (306 options)
    TextBox txtArmourRequired "Armour Rating": = "1"
    Label txtBuildTime "Build Time (yrs)": "0.32"
    TextBox txtSummary
      │ Freighter (8) class Freighter      166,506 tons       359 Crew …
    TreeView tvClassList
      - Freighter
        > Freighter (8)
    Button cmdNew "New Ship Class"
```

`Kind name "caption": "text" = "value"`, where the caption is the label next to or above the control. Lists show
their rows (`|` separated, `>` marks the selection), trees their nodes (`[+N]` = collapsed children), multi-line
text as a `│` block. Control names (`cmdNew`, `tvClassList`) are the stable handles for actions.

### Events

Each event: `seq`, `time`, `gameTime`, `source`, `type`, `form`, `formTitle`, `control`, `kind`, `label`, `value`, `from`.

- `source`: `user` (real player input on that control), `api` (the control an API action targeted), `game`
  (window and message box lifecycle). Changes Aurora makes to its own controls in reaction are not recorded.
- `type`: `form_open`, `form_close`, `form_focus`, `click`, `doubleclick`, `check`, `select`, `text`, `tab`,
  `dialog_open`, `dialog_close`.
- Clicks are recorded before Aurora handles them, so windows and message boxes they open come after them.
- Typing is coalesced into one `text` event (`from` → `value`).

## Building

Requires the .NET SDK (8+; builds `net48` using reference assemblies, so it works on Linux too). References come
from an Aurora install with AuroraPatch and Lib: set `AuroraDir` to that folder, as an environment variable or with
`-p:AuroraDir=...`.

```sh
export AuroraDir=/path/to/Aurora                            # Windows: set AuroraDir=C:\Games\Aurora
dotnet build src/AuroraAssistantApi -c Release              # build
dotnet build src/AuroraAssistantApi -c Release -t:Deploy    # build + copy into $AuroraDir/Patches/AuroraAssistantApi
scripts/package.sh                                          # release zip in dist/
```

## Notes

- The HTTP server is a small socket server rather than `HttpListener`, which needs admin rights on Windows (URL
  reservations) and is unreliable under Wine.
- The launcher window is minimized once the game starts, since it otherwise covers the map's toolbar.
