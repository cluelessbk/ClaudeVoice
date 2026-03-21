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

## Roadmap

Five phases to get ClaudeVoice fully working. Mark each `[x]` when complete.

### Phase 1: Fix TTS `[x]`
Root cause: `_activeSessionId` was always empty — TTS only plays for the active session but no session was ever set active. Audio queue files piled up unconsumed.
Fix: auto-adopt first incoming session ID in `TtsService.OnAudioFileArrived`; auto-activate first linked terminal in MainWindow. TTS now works without explicit linking for single-session use.

### Phase 2: Jabra Play → Enter `[ ]`
**Status: blocked — needs alternative approach.**
Headset: Jabra Link 380. The Jabra SDK has a mode conflict that prevents detecting the Play button while mic arm works:
- `callActive=true` → mic arm MuteState works ✅, Play button consumed by SDK with NO event and NO HID ❌
- `callActive=false` → Play button sends HID 0x0080 (consumer page 0x000C) ✅, mic arm stops firing ❌
- CallActive observable does NOT fire when Play is pressed with callActive=true
- Toggle approach (EndCall→detect HID→Teardown+recreate ECC) tested but RestoreCallMode doesn't reliably restore mic arm

**What was tested and failed:**
1. CallActive subscription with callActive=true — Play press produces no event
2. SignalIncomingCall ring loop — Play press during ring produces no event
3. Raw HID detection with callActive=true — Play button sends no HID at all
4. Toggle: EndCall→HID→RestoreCallMode — HID works but mic arm breaks after restore

**What works:**
- HID 0x0080 detection in HotkeyService (HidButtonPressed event) — fires reliably when callActive=false
- Volume buttons always send HID regardless of mode (0x00E9 up, 0x00EA down)
- Keyboard VK_MEDIA_PLAY_PAUSE hook — works for non-Jabra headsets

**Ideas not yet tried:**
- Jabra Direct software button remapping (zero code if supported)
- AutoHotkey script to intercept at OS level
- Lower-level ICallControl raw signals instead of EasyCallControl
- External listener process with direct HID device access
- Remap a different physical action (e.g. double-tap volume) to submit

### Phase 3: Flow 1 — Active session end-to-end `[ ]`
Mic down → speak → mic up (transcribes) → Enter or Play button → Claude answers → 5-sec delay → TTS reads response. Active session only, no beeps, no queue.

### Phase 4: Flow 2 — Multi-session queue `[ ]`
- Non-active session gets Claude response → queue it, don't read
- Beep notification: once every 10 sec for 1 min, then 1 min silence, repeat for 5 min total, then stop
- Play button → jump to oldest pending session (arrival order), make active, read queued message
- Session stays pending silently if user never switches

### Phase 5: Terminal linking polish `[ ]`
- "Link terminal" button → user clicks PowerShell window → app captures that window → shows in list
- Terminal auto-removes from list when its process exits
- Switch hotkey cycles all linked terminals in link order (not arrival order)
- Play button cycles only terminals with pending messages (arrival order)

## Known issues

1. ~~TTS not playing~~ — FIXED (Phase 1)
2. Headset Play button doesn't send Enter — blocked, needs alternative approach (→ Phase 2)
3. JabraService has leftover toggle code from failed Phase 2 attempts — needs cleanup
4. HotkeyService has HidButtonPressed event + 0x0080 detection — keep for future Play button solution
5. Closed terminals remain in session list (→ Phase 5)
