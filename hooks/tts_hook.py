#!/usr/bin/env python3
"""
TTS Stop Hook for Claude Code
==============================
Registered as a Stop hook in settings.json. Fires after every Claude response.

Speaks any assistant text not already spoken by the PreToolUse hook, then
resets the cursor so the next turn starts fresh. Silent unless running inside
the terminal linked in ClaudeVoice.
"""

import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tts_utils import (DISABLED_FLAG, is_linked, read_cursor, reset_cursor,
                       write_to_queue, get_assistant_text_blocks, clean_for_tts)


def main():
    time.sleep(0.5)  # let the transcript flush

    if not is_linked():
        sys.exit(0)

    # Always reset the cursor at end of turn, whatever happens below
    cursor = read_cursor()
    reset_cursor()

    if os.path.exists(DISABLED_FLAG):
        sys.exit(0)

    try:
        hook_input = json.load(sys.stdin)
    except Exception:
        sys.exit(0)

    transcript_path = hook_input.get("transcript_path")
    if not transcript_path or not os.path.exists(transcript_path):
        sys.exit(0)

    new_blocks = get_assistant_text_blocks(transcript_path)[cursor:]
    if not new_blocks:
        sys.exit(0)

    cleaned = clean_for_tts("\n".join(new_blocks))
    if cleaned and len(cleaned) >= 10:
        write_to_queue(cleaned)
    sys.exit(0)


if __name__ == "__main__":
    main()
