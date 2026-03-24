# Changelog

## 0.8.0 — 2026-03-24

### Removed
- **Pause/Resume** — button, Ctrl+Shift+P hotkey, and all underlying logic (TtsService pause state, mid-generation wait loop, requeue-on-pause). Simplifies TTS pipeline. Action buttons now just Replay + Stop.

## 0.7.0 — 2026-03-24

### Changed
- **Badge-based session routing**: replaced transcript-path MD5 session IDs with stable badge numbers (1, 2, 3…) assigned at link time. Badges survive new conversations, Claude Code restarts, and transcript path changes. TerminalService writes `claudevoice_badge_{pid}.txt` for all descendant PIDs every 3s so Python hooks can self-identify.
- Per-badge cursor files (`tts_cursor_b{N}.txt`) — eliminates cross-session cursor contamination that caused hooks to skip audio
- Queue directory polling every ~2s as fallback for FileSystemWatcher missed events
- Terminal row UI shows badge number before display name

### Removed
- `ComputeSessionId()` (MD5 of transcript path) from both TtsService and TerminalService
- `SessionIdIsPlaceholder` and placeholder re-adopt logic
- `SessionIdResolved` event
- `active_session.txt` dependency for session initialization

## 0.6.1 — 2026-03-23

### Verified
- Foreground tracking: auto-switch when focusing a linked terminal
- Click session row: brings terminal window to foreground
- Ctrl+Alt+Arrow: switch between linked terminals
- Display name: shows tab title (CWD-based fallback exists but tab title is acceptable)

### Discovered
- Inactive session audio silently discarded: TTS only tracks the active session ID, so a second linked-but-never-activated terminal's audio gets deleted instead of queued. Needs `RegisterSession()` in TtsService.

## 0.6.0 — 2026-03-23

### Added
- Transcription-complete ding: rising two-tone sound (1200→1500 Hz) plays after text is fully typed into terminal, signaling ready to submit

### Fixed
- Pause button: now a true pause — audio freezes in place and resumes from exact position (was restarting from beginning)
- Stop button: stops playback and clears active session queue only, preserves last queued item for Replay (was auto-replaying and keeping the interrupted item instead of the last)
- Replay button: plays only the last queued part with nothing after it (was continuing with remaining queue)
- Transcription ding timing: waits for PowerShell SendKeys to finish typing before playing (was firing immediately)

## 0.5.0 — 2026-03-23

### Added
- Foreground tracking: active session auto-switches when user focuses a linked terminal in the OS
- Click-to-focus: clicking a session row in ClaudeVoice brings its terminal window to foreground
- Placeholder session ID re-adopt: when FindTranscriptPath fails at link time, the real session ID is resolved on first audio arrival
- Orphan cleanup: pending notifications and audio queues are cleaned up when a terminal is removed

### Fixed
- Session ID mismatch: normalized transcript paths to backslashes before MD5 hashing (Python hooks use backslashes, C# Path.Combine produced mixed slashes)
- Closed terminals now detected via IsWindow(HWND) check — catches tab-closed-but-process-alive (Windows Terminal shared PID)
- Audio from unlinked sessions silently discarded — no more phantom beeps from Claude sessions not linked in ClaudeVoice
- Display name: CWD-based with dedup check, falls back to window title when name is already claimed

## 0.4.0 — 2026-03-22

### Added
- Multi-session queue (Phase 4): beep notification schedule for pending sessions (10 sec beep, 50 sec silence per minute, 5 min total)
- Hang-up button smart routing: submit transcription > cycle to pending session > send Enter (fallback)
- HWND-based window targeting: FocusAndType and SendEnter can target exact windows

### Fixed
- Second terminal can now be linked (was blocked by PID-based duplicate check — Windows Terminal shares one PID across windows)
- Audio queue cleanup on startup/shutdown prevents stale files from old sessions being replayed

## 0.3.0 — 2026-03-21

### Added
- Jabra hang-up button sends Enter to Claude terminal (Phase 2 complete)
- JabraService detects hang-up via CallActive observable, restores call mode with StartCall(), fires HangUpPressed event
- TerminalTypist.SendEnter uses PowerShell SendKeys (SendInput doesn't reach Windows Terminal)

## 0.2.0 — 2026-03-21

### Fixed
- TTS now plays automatically — no longer requires terminal linking for single-session use
- Auto-adopts first incoming session ID when no active session is set
- First linked terminal auto-activates (no manual click needed)

### Investigated (not yet resolved)
- Jabra Play button → Enter: extensive testing revealed SDK mode conflict (callActive=true blocks Play button HID, callActive=false breaks mic arm). Documented findings and approaches for next session.

### Added
- HID 0x0080 detection in HotkeyService for Jabra Play button (ready for future solution)
- Roadmap with 5 phases added to project CLAUDE.md

## 0.1.0 — 2026-03-20

- Initial commit: ClaudeVoice project with TTS, STT, terminal management, Jabra integration
