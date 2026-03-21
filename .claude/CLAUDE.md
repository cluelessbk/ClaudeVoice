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

### Phase 2: Jabra Hang-Up → Enter `[x]`
Play button was unsolvable (SDK consumes it with callActive=true, no event or HID). Used the **hang-up button** instead:
- With `callActive=true`, pressing hang-up fires `CallActive→False` on the `ISingleCallControl` observable
- On `CallActive→False`: call `StartCall()` to restore call mode (mic arm keeps working), wait 300ms for SDK to settle, then fire `HangUpPressed` event
- MainWindow wires `HangUpPressed` → `TerminalTypist.SendEnter()` which focuses the terminal and sends Enter via PowerShell `SendKeys("{ENTER}")` (SendInput with VK_RETURN doesn't reach Windows Terminal)

**What failed (Play button — kept for reference):**
1. CallActive subscription — Play press produces no event with callActive=true
2. Raw HID detection — Play button sends no HID with callActive=true
3. Toggle EndCall→HID→RestoreCallMode — mic arm breaks after restore

### Phase 3: Flow 1 — Active session end-to-end `[ ]`
Mic down → speak → mic up (transcribes) → Enter or Play button → Claude answers → 5-sec delay → TTS reads response. Active session only, no beeps, no queue.

### Phase 4: Flow 2 — Multi-session queue `[x]`
- Non-active session gets Claude response → queued silently, not read
- Beep notification: beeps in first 10 sec of each minute, silent 50 sec, repeats for 5 min, then stops
- Hang-up button → priority: submit transcription > cycle to oldest pending session > send Enter (fallback)
- Session stays pending silently if user never switches
- Bug fix: second terminal can now be linked (HWND-based dedup instead of PID — Windows Terminal shares a PID)
- Audio queue cleanup on app startup/shutdown prevents stale files from old sessions

### Phase 5: Terminal linking polish `[ ]`
- "Link terminal" button → user clicks PowerShell window → app captures that window → shows in list
- Terminal auto-removes from list when its process exits
- Switch hotkey cycles all linked terminals in link order (not arrival order)
- Hang-up button cycles only terminals with pending messages (arrival order)

## Known issues

1. ~~TTS not playing~~ — FIXED (Phase 1)
2. ~~Headset button doesn't send Enter~~ — FIXED (Phase 2, using hang-up button)
3. HotkeyService has HidButtonPressed event + 0x0080 detection — unused now, could remove or keep for non-Jabra headsets
4. Closed terminals remain in session list (→ Phase 5)
5. Phase 4 pending testing — beep schedule, hang-up cycling, and multi-terminal linking need end-to-end verification
