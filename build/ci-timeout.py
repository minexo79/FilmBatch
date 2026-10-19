#!/usr/bin/env python3
"""Mac CI command-budget enforcer using only the Python3 standard library.

Usage: python3 build/ci-timeout.py <remaining-seconds> <command> [args...]

Replaces GNU timeout on macOS runners so CI needs no Homebrew install.
Runs <command> in a fresh session/process group with inherited stdio,
environment and exact argv. If the command finishes in time, its exit
status is preserved. On timeout the whole process group gets SIGTERM;
survivors get SIGKILL after 30s (mirroring timeout --kill-after=30s),
the direct child is reaped, and the exit status is 124. A non-positive
budget exits 124 immediately without launching the command.
"""
import os
import signal
import subprocess
import sys
import time

TIMEOUT_EXIT = 124
MISUSE_EXIT = 125
NOT_RUN_EXIT = 127
KILL_AFTER_SECONDS = 30
POLL_INTERVAL_SECONDS = 0.05


def group_empty(pgid):
    try:
        os.killpg(pgid, 0)
    except ProcessLookupError:
        return True
    except PermissionError:
        return False
    return False


def kill_group(pgid, sig):
    try:
        os.killpg(pgid, sig)
    except (ProcessLookupError, PermissionError):
        pass


def wait_group_empty(child, pgid, deadline):
    while time.monotonic() < deadline:
        child.poll()  # Reap an exited leader; its zombie otherwise keeps the group alive.
        if group_empty(pgid):
            return True
        time.sleep(POLL_INTERVAL_SECONDS)
    child.poll()
    return group_empty(pgid)


def main(argv):
    if len(argv) < 3:
        print('usage: ci-timeout.py <remaining-seconds> <command> [args...]', file=sys.stderr)
        return MISUSE_EXIT
    try:
        remaining = float(argv[1])
    except ValueError:
        print('ci-timeout.py: invalid remaining seconds: %r' % argv[1], file=sys.stderr)
        return MISUSE_EXIT
    if not remaining > 0:
        print('CI command budget exhausted.', file=sys.stderr)
        return TIMEOUT_EXIT
    try:
        child = subprocess.Popen(argv[2:], start_new_session=True)
    except OSError as exc:
        print('ci-timeout.py: cannot run %r: %s' % (argv[2], exc), file=sys.stderr)
        return NOT_RUN_EXIT
    try:
        status = child.wait(timeout=remaining)
    except subprocess.TimeoutExpired:
        pass
    else:
        # Mirror shell/GNU timeout convention for signal deaths (128 + signum).
        return status if status >= 0 else 128 - status
    # Timed out: TERM the whole group. The leader may already be gone while
    # descendants survive; killpg still reaches them via the surviving pgid.
    pgid = child.pid
    kill_group(pgid, signal.SIGTERM)
    if not wait_group_empty(child, pgid, time.monotonic() + KILL_AFTER_SECONDS):
        kill_group(pgid, signal.SIGKILL)
    child.wait()
    return TIMEOUT_EXIT


if __name__ == '__main__':
    sys.exit(main(sys.argv))
