#!/usr/bin/env python3
"""
TTS PreToolUse Hook for Claude Code
=====================================
Fires before each tool call. Speaks any new assistant text that appeared since
the last hook fired, so you hear Claude mid-turn instead of only at the end.
Silent unless running inside the terminal linked in ClaudeVoice.
"""

import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from tts_utils import (DISABLED_FLAG, is_linked, read_cursor, write_cursor,
                       write_to_queue, get_assistant_text_blocks, clean_for_tts)


def main():
    if os.path.exists(DISABLED_FLAG) or not is_linked():
        sys.exit(0)

    try:
        hook_input = json.load(sys.stdin)
    except Exception:
        sys.exit(0)

    transcript_path = hook_input.get("transcript_path")
    if not transcript_path or not os.path.exists(transcript_path):
        sys.exit(0)

    time.sleep(0.3)  # let the transcript flush

    blocks = get_assistant_text_blocks(transcript_path)
    new_blocks = blocks[read_cursor():]
    if not new_blocks:
        sys.exit(0)

    cleaned = clean_for_tts("\n".join(new_blocks))
    if cleaned and len(cleaned) >= 10:
        write_to_queue(cleaned)
        write_cursor(len(blocks))
    sys.exit(0)


if __name__ == "__main__":
    main()
