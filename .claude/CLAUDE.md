# ClaudeVoice

A native Windows WPF app that gives Claude Code a voice interface: TTS (edge-tts) + STT (Whisper), linked to a single terminal.

All source code lives in this directory. Do not create files outside of it (except deploying the Python hooks to `~/.claude/`, see Build).

## Architecture

Two parts that communicate via files in `~/.claude/`:
1. **Python hooks** (`hooks/` in this repo, deployed to `~/.claude/`) — fire inside Claude Code on Stop/PreToolUse. If running inside the linked terminal, write new assistant text to `~/.claude/audio_queue/{timestamp}.txt`
2. **ClaudeVoice.exe** — watches `audio_queue/`, speaks each file via edge-tts, records mic via NAudio, transcribes via Whisper.net, types into the linked terminal

### Single-terminal linking (v0.10.0)

Clicking "Link terminal" then a terminal window writes one process ID to `~/.claude/claudevoice_link.txt` (deleted on Unlink, app close and app start). A hook speaks only if that PID is one of its ancestors (`tts_utils.is_linked()`, Toolhelp32 snapshot, ~8 ms). One cursor file (`tts_cursor.txt`) tracks how many text blocks of the current turn were already spoken.

**Windows Terminal:** all WT windows and tabs run in ONE process, so linking its PID would make every Claude session speak (the v0.10.0-dev bug). Instead `TerminalLink` identifies the active tab: each tab's shell (a direct child of WindowsTerminal.exe) has its own console whose title Claude Code sets, and the window title always equals the active tab's console title (ignoring the animated status glyph ✳ ◑ …). The app reads each shell's console title via `AttachConsole` + `GetConsoleTitle` and links the matching shell's PID. Survives Claude restarts in that tab; unlinks when the tab/window closes.

Typing and headset-submit go to whichever tab is showing in the linked window, so they are refused (dictation copied to clipboard) when the linked tab isn't the active one.

### Claude desktop app (v0.11.0)

Hooks from `~/.claude/settings.json` do NOT fire in desktop-app Code sessions (tested 2026-11-24; docs say they should, cause unknown, folder is trusted). So linking a Claude app window (`DesktopApp.IsClaudeWindow`: process `claude`, class `Chrome_WidgetWin_1`) works without hooks and writes no link file:
- **Which session:** UI Automation document named `<session title> - Claude Code` = the session on screen. `TranscriptReader.FindTranscript` picks the newest `.jsonl` under `~/.claude/projects/` whose latest `custom-title` entry equals that title.
- **Speech:** `TranscriptReader` tails that file from its end (500 ms poll), each `type:"assistant"` non-sidechain line with text blocks → cleaned (C# port of `clean_for_tts`) → `audio_queue/`. Temp file is written outside `audio_queue/` then moved in (a rename inside the folder doesn't fire the watcher's Created event).
- **Typing:** `ValuePattern.SetValue` on the Edit named "Prompt" (empty box reports `"\n"`); submit = `InvokePattern` on the Button named "Send" (queues while Claude is busy). Refused when the linked session isn't on screen.
- Transcript format is internal to Claude Code and may change between versions.

## File map

| Feature | File |
|---|---|
| TTS queue + edge-tts + playback | `Services/TtsService.cs` |
| File watching (audio_queue, flags) | `Services/FileWatcherService.cs` |
| Mic recording + Whisper STT | `Services/SttService.cs` |
| The one linked terminal + link file | `Services/TerminalLink.cs` |
| Claude app window (session title, Prompt box, Send) | `Services/DesktopApp.cs` |
| Follow desktop session transcript → audio_queue | `Services/TranscriptReader.cs` |
| Focus terminal + type text + Enter | `Services/TerminalTypist.cs` |
| Keyboard hotkeys + media keys | `Services/HotkeyService.cs` |
| Jabra headset (mic arm + hang-up btn) | `Services/JabraService.cs` |
| UI layout | `MainWindow.xaml` + `MainWindow.xaml.cs` |
| Styles | `App.xaml` |
| Hooks (master copies) | `hooks/tts_hook.py`, `hooks/tts_pretool.py`, `hooks/tts_utils.py` |

## Build

```bash
# Kill ClaudeVoice.exe first, then:
export PATH="$PATH:/c/Program Files/dotnet"
cd "D:/My Claude/TalkingPoint"
dotnet publish ClaudeVoice/ClaudeVoice.csproj --configuration Release --runtime win-x64 --self-contained false -o publish
# If hooks/ changed, deploy them:
cp hooks/*.py ~/.claude/
```

Launch: `D:/My Claude/TalkingPoint/publish/ClaudeVoice.exe`

## Pending tests

- [ ] Jabra hang-up: after a transcription → submits (Enter); otherwise → stops the voice
- [ ] Dictation containing + ( ) [ ] types correctly (SendKeys escaping fix)

## Known issues

1. **Silence + "Voice service refused (403)"** = Microsoft rotated edge-tts's access token. Fix: `pip install -U edge-tts` (happened Mar 2026 and Oct 2026). No restart needed.
2. Linking fails with a message if two WT tabs have identical titles (e.g. two fresh sessions) — send a message in one first.
3. Terminal only: `_submitPending` stays set if the user presses Enter manually, so the next hang-up press sends an extra Enter instead of stopping audio. (Claude app: fixed — empty box → stop voice.)
4. Claude app: a brand-new session with no title can't be linked until it has one (send it a message first). Renaming a linked session breaks the "session on screen" check — re-link.

## Design decisions

- Single terminal only (v0.10.0). Multi-terminal badge routing (v0.7–0.9) never worked reliably and was removed at the user's request.
- Link = one file with one PID (WT: the tab's shell), checked by the hooks; replaces rewriting ~30 badge files every 3 s.
- Jabra Play button unsolvable (SDK consumes it with callActive=true). Hang-up button used instead: submit if a transcription is waiting, else stop audio.
- SendKeys("{ENTER}") via PowerShell — SendInput with VK_RETURN doesn't reach Windows Terminal.
- edge-tts runs with stdin/stdout/stderr all redirected: as a GUI app ClaudeVoice has no console, and edge-tts ≥7.3 crashes on `sys.stdin.isatty()` when stdin is None. Test TTS with the app launched from Explorer (`explorer.exe publish\ClaudeVoice.exe`), not from a shell — a shell launch hides this class of bug.
- Text goes to edge-tts via `--file`, not `--text` (Windows command line caps at ~32k chars).
- edge-tts failure is detected by exit code + non-empty mp3 (it leaves an empty file behind on failure).
- Queue directory polling every ~2s as fallback for FileSystemWatcher missed events.
- Window height fits content (hotkey list collapsed by default); aspect ratio is recomputed when it changes.
