# Session log

## 2026-11-24 — Windows PC — Claude desktop app support (v0.11.0)
- Found that voice hooks don't fire in Claude desktop-app sessions; built a hook-free route instead: follow the session's transcript for speech, type into the app's Prompt box and press Send for dictation.
- Decision: terminal linking stays exactly as before; "Link terminal" detects which kind of window was clicked.
- State: published and tested live — speech and dictation both work in the app. Hooks untouched.
- Next: use it for a while; watch for breakage after Claude app updates (transcript format / UI names).
