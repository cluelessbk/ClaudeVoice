# ClaudeVoice

A native Windows WPF app that gives Claude Code a voice interface: TTS (edge-tts) + STT (Whisper) + multi-session terminal management.

All source code lives in this directory. Do not create files outside of it (except Python hooks in `~/.claude/` which are part of the runtime, not the build).

## Architecture

Two parts that communicate via badge-numbered files in `~/.claude/`:
1. **Python hooks** — fire inside Claude Code on Stop/PreToolUse/PostToolUse, read their badge from `claudevoice_badge_{pid}.txt`, write text to `~/.claude/audio_queue/b{N}_{timestamp}.txt`
2. **ClaudeVoice.exe** — watches `audio_queue/`, routes audio by badge number, speaks via edge-tts, records mic via NAudio, transcribes via Whisper.net, types into the Claude terminal

### Badge-based session routing

Each linked terminal gets a stable badge number (1, 2, 3…) at link time. TerminalService writes `claudevoice_badge_{pid}.txt` for all descendant PIDs every 3 seconds. Python hooks read the badge and include it in audio filenames. This replaces the old transcript-path-based session ID which broke every time a new conversation started.

## File map

| Feature | File |
|---|---|
| TTS playback + queues | `Services/TtsService.cs` |
| File watching (audio_queue, flags) | `Services/FileWatcherService.cs` |
| Mic recording + Whisper STT | `Services/SttService.cs` |
| Terminal linking + badge mgmt | `Services/TerminalService.cs` |
| Focus terminal + type text + Enter | `Services/TerminalTypist.cs` |
| Keyboard hotkeys + media keys | `Services/HotkeyService.cs` |
| Jabra headset (mic arm + play btn) | `Services/JabraService.cs` |
| Session data model | `Models/TerminalSession.cs` |
| UI layout | `MainWindow.xaml` + `MainWindow.xaml.cs` |
| Styles | `App.xaml` |

## Build

```bash
# Kill ClaudeVoice.exe first, then:
export PATH="$PATH:/c/Program Files/dotnet"
cd "D:/My Claude/TalkingPoint"
dotnet publish ClaudeVoice/ClaudeVoice.csproj --configuration Release --runtime win-x64 --self-contained false -o publish
```

Launch: `D:/My Claude/TalkingPoint/publish/ClaudeVoice.exe`

## Pending tests

- [ ] Badge routing: link terminal, start new conversation, verify audio still plays
- [ ] Multi-session: two terminals with badges 1 and 2, correct audio routing
- [ ] Beep schedule — 10s beep / 50s silent per minute, 5 min total
- [ ] Hang-up cycling — submit transcription > cycle to pending > send Enter

## Known issues

1. Display name uses window tab title, not project folder name — CWD-based naming exists but often falls back to tab title. Works well enough.
2. HotkeyService HidButtonPressed + 0x0080 detection — unused, could remove or keep for non-Jabra headsets
3. Stale `claudevoice_active_{pid}.txt` files accumulate in `~/.claude/` — never cleaned up

## Design decisions

- Badge-based routing replaces transcript-path MD5 session IDs. Badges are stable across conversations.
- Jabra Play button unsolvable (SDK consumes it with callActive=true). Hang-up button used instead.
- SendKeys("{ENTER}") via PowerShell — SendInput with VK_RETURN doesn't reach Windows Terminal.
- HWND-based dedup for terminal linking — Windows Terminal shares a PID across windows.
- Per-badge cursor files (`tts_cursor_b{N}.txt`) prevent cross-session cursor contamination.
- Queue directory polling every ~2s as fallback for FileSystemWatcher missed events.
