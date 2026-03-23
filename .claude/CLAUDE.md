# ClaudeVoice

A native Windows WPF app that gives Claude Code a voice interface: TTS (edge-tts) + STT (Whisper) + multi-session terminal management.

All source code lives in this directory. Do not create files outside of it (except Python hooks in `~/.claude/` which are part of the runtime, not the build).

## Architecture

Two parts that communicate via files in `~/.claude/`:
1. **Python hooks** — fire inside Claude Code on Stop/PreToolUse/PostToolUse, write text to `~/.claude/audio_queue/`
2. **ClaudeVoice.exe** — watches `audio_queue/`, speaks text via edge-tts, records mic via NAudio, transcribes via Whisper.net, types into the Claude terminal

## File map

| Feature | File |
|---|---|
| TTS playback + queues | `Services/TtsService.cs` |
| File watching (audio_queue, flags) | `Services/FileWatcherService.cs` |
| Mic recording + Whisper STT | `Services/SttService.cs` |
| Terminal linking + session mgmt | `Services/TerminalService.cs` |
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

## Roadmap

Phases 1–4 complete. Phase 5 in progress.

### Phase 5: Terminal linking polish `[~]`
- Foreground tracking: OS-focused terminal auto-becomes active session
- Click session row → brings terminal window to foreground
- Unlinked sessions' audio silently discarded (no phantom beeps)

## Pending tests

- [ ] Display name — second terminal shows wrong name (shared PID). CWD-first with dedup added but untested
- [ ] Foreground tracking — auto-switch when focusing a linked terminal
- [ ] Click session row → focus terminal window
- [ ] Unlinked session audio silently discarded
- [ ] Multi-session end-to-end: two terminals, correct names, voice/bell routing
- [ ] Beep schedule — 10s beep / 50s silent per minute, 5 min total
- [ ] Beep cleanup — stops when pending session is closed or switched to
- [ ] Hang-up cycling — submit transcription > cycle to pending > send Enter
- [ ] Ctrl+Alt+Arrow — switch between linked terminals

## Known issues

1. HotkeyService HidButtonPressed + 0x0080 detection — unused, could remove or keep for non-Jabra headsets
2. Stale `claudevoice_active_{pid}.txt` files accumulate in `~/.claude/` — never cleaned up

## Design decisions

- Jabra Play button unsolvable (SDK consumes it with callActive=true). Hang-up button used instead.
- SendKeys("{ENTER}") via PowerShell — SendInput with VK_RETURN doesn't reach Windows Terminal.
- HWND-based dedup for terminal linking — Windows Terminal shares a PID across windows.
- Session IDs: MD5 of transcript path normalized to backslashes before hashing.
- Audio from unlinked sessions discarded in OnAudioFileArrived.
