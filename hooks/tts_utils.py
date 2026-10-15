#!/usr/bin/env python3
"""
TTS Utilities — shared by tts_hook.py and tts_pretool.py
=========================================================
Master copy lives in the ClaudeVoice repo (hooks/). Deployed to ~/.claude/.

Linking: ClaudeVoice writes the linked terminal's process ID to
claudevoice_link.txt. A hook speaks only if that process is one of its
ancestors, i.e. the hook is running inside the linked terminal.
"""

import ctypes
import ctypes.wintypes
import json
import os
import re
import time

CLAUDE_DIR    = os.path.dirname(os.path.abspath(__file__))
DISABLED_FLAG = os.path.join(CLAUDE_DIR, "tts_disabled")
QUEUE_DIR     = os.path.join(CLAUDE_DIR, "audio_queue")
LINK_FILE     = os.path.join(CLAUDE_DIR, "claudevoice_link.txt")
CURSOR_FILE   = os.path.join(CLAUDE_DIR, "tts_cursor.txt")


# ── Link check ────────────────────────────────────────────────────────────────

class _PROCESSENTRY32(ctypes.Structure):
    _fields_ = [
        ("dwSize", ctypes.wintypes.DWORD),
        ("cntUsage", ctypes.wintypes.DWORD),
        ("th32ProcessID", ctypes.wintypes.DWORD),
        ("th32DefaultHeapID", ctypes.c_void_p),
        ("th32ModuleID", ctypes.wintypes.DWORD),
        ("cntThreads", ctypes.wintypes.DWORD),
        ("th32ParentProcessID", ctypes.wintypes.DWORD),
        ("pcPriClassBase", ctypes.c_long),
        ("dwFlags", ctypes.wintypes.DWORD),
        ("szExeFile", ctypes.c_char * 260),
    ]


def _ancestor_pids():
    """PIDs of every process above this one (parent, grandparent, ...)."""
    kernel32 = ctypes.windll.kernel32
    kernel32.CreateToolhelp32Snapshot.restype = ctypes.wintypes.HANDLE
    snap = kernel32.CreateToolhelp32Snapshot(0x00000002, 0)  # TH32CS_SNAPPROCESS
    if not snap or snap == ctypes.wintypes.HANDLE(-1).value:
        return set()

    parent_of = {}
    pe = _PROCESSENTRY32()
    pe.dwSize = ctypes.sizeof(_PROCESSENTRY32)
    try:
        ok = kernel32.Process32First(snap, ctypes.byref(pe))
        while ok:
            parent_of[pe.th32ProcessID] = pe.th32ParentProcessID
            ok = kernel32.Process32Next(snap, ctypes.byref(pe))
    finally:
        kernel32.CloseHandle(snap)

    ancestors = set()
    current = os.getpid()
    for _ in range(30):  # safety limit
        current = parent_of.get(current)
        if not current or current in ancestors:
            break
        ancestors.add(current)
    return ancestors


def is_linked():
    """True if this hook runs inside the terminal linked in ClaudeVoice."""
    try:
        with open(LINK_FILE, "r") as f:
            linked_pid = int(f.read().strip())
    except Exception:
        return False
    try:
        return linked_pid in _ancestor_pids()
    except Exception:
        return False


# ── Cursor (how many text blocks of this turn were already spoken) ────────────

def read_cursor():
    try:
        with open(CURSOR_FILE, "r") as f:
            return int(f.read().strip())
    except Exception:
        return 0


def write_cursor(n):
    try:
        with open(CURSOR_FILE, "w") as f:
            f.write(str(n))
    except Exception:
        pass


def reset_cursor():
    try:
        os.unlink(CURSOR_FILE)
    except Exception:
        pass


# ── Queue ─────────────────────────────────────────────────────────────────────

def write_to_queue(text):
    """Write text to audio_queue/ for ClaudeVoice.exe to pick up."""
    os.makedirs(QUEUE_DIR, exist_ok=True)
    path = os.path.join(QUEUE_DIR, f"{int(time.time() * 1000)}.txt")
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)


# ── Transcript ────────────────────────────────────────────────────────────────

def get_assistant_text_blocks(transcript_path):
    """
    Returns a list of assistant text block strings from the current turn.
    A turn = everything after the last real user message.
    Each entry in the list = one text block (natural speech unit).
    """
    entries = []
    with open(transcript_path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                entries.append(json.loads(line))
            except json.JSONDecodeError:
                continue

    def is_real_user_message(entry):
        if entry.get("type") != "user":
            return False
        content = entry.get("message", {}).get("content", "")
        if isinstance(content, str):
            return True
        if isinstance(content, list):
            return not any(
                isinstance(b, dict) and b.get("type") == "tool_result"
                for b in content
            )
        return False

    last_user_idx = -1
    for i, entry in enumerate(entries):
        if is_real_user_message(entry):
            last_user_idx = i

    blocks = []
    for entry in entries[last_user_idx + 1:]:
        if entry.get("type") != "assistant":
            continue
        message = entry.get("message", {})
        if message.get("role") != "assistant":
            continue
        content = message.get("content", [])
        if isinstance(content, list):
            for block in content:
                if isinstance(block, dict) and block.get("type") == "text":
                    text = block.get("text", "").strip()
                    if text:
                        blocks.append(text)
        elif isinstance(content, str) and content.strip():
            blocks.append(content.strip())

    return blocks


def clean_for_tts(text):
    """Strip markdown formatting so the spoken output sounds natural."""
    text = re.sub(r"```[\s\S]*?```", "", text)
    text = re.sub(r"`([^`\n]+)`", r"\1", text)
    text = re.sub(r"^#{1,6}\s+", "", text, flags=re.MULTILINE)
    text = re.sub(r"\*{1,3}([^*\n]+)\*{1,3}", r"\1", text)
    text = re.sub(r"_{1,3}([^_\n]+)_{1,3}", r"\1", text)
    text = re.sub(r"^[-*_]{3,}\s*$", "", text, flags=re.MULTILINE)
    text = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", text)
    text = re.sub(r"^\s*[-*+]\s+", "", text, flags=re.MULTILINE)
    text = re.sub(r"^\s*\d+\.\s+", "", text, flags=re.MULTILINE)
    text = re.sub(r"\|[^\n]+\|", "", text)
    text = re.sub(r"^[-|:\s]+$", "", text, flags=re.MULTILINE)
    text = re.sub(r"\n{3,}", "\n\n", text)
    return text.strip()
