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

## Python hooks (in `~/.claude/`)

| File | Trigger | Does |
|---|---|---|
| `tts_hook.py` | Stop hook | Writes Claude response to audio_queue |
| `tts_pretool.py` | PreToolUse hook | Writes tool-use text to audio_queue |
| `write_active.py` | PostToolUse hook | Writes per-process CWD to `claudevoice_active_{pid}.txt` |
| `tts_utils.py` | Imported | Shared transcript parsing + markdown stripping |

## Runtime state files (in `~/.claude/`)

`audio_queue/`, `active_session.txt`, `claudevoice_active_{pid}.txt`, `tts_rate.txt`, `tts_last.txt`, `tts_disabled`, `claudevoice_pos.txt`

## Build

```bash
# Kill ClaudeVoice.exe first, then:
export PATH="$PATH:/c/Program Files/dotnet"
cd "D:/My Claude/TalkingPoint"
dotnet publish ClaudeVoice/ClaudeVoice.csproj --configuration Release --runtime win-x64 --self-contained false -o publish
```

Launch: `D:/My Claude/TalkingPoint/publish/ClaudeVoice.exe`

## Known issues

1. TTS not playing — hasn't been heard for several sessions, needs pipeline trace
2. Closed terminals remain in session list — process-exit monitor not cleaning up
3. Headset play button doesn't send Enter — only keyboard Enter works
4. Second terminal linking not working
