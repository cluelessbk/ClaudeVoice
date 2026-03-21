# Changelog

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
