import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import unittest


class SessionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        shutil.copy2(Path(__file__).parents[1] / "bridge.py", self.root / "bridge.py")
        # This fixture never opens Wayland or the user's clipboard.
        native = self.root / "lanview-clipboard-wayland"
        native.write_text("#!/bin/sh\nprintf 'READY\\n'\nwhile IFS= read -r line; do :; done\n")
        native.chmod(0o700)
        environment = os.environ.copy()
        environment["XDG_CACHE_HOME"] = str(self.root / "cache")
        self.process = subprocess.Popen([sys.executable, str(self.root / "bridge.py")], env=environment,
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        hello = json.loads(self.process.stdout.readline())
        self.assertEqual(hello, {"type": "hello", "version": 1})

    def tearDown(self):
        if self.process.poll() is None:
            self.process.terminate()
            self.process.wait(timeout=5)
        self.process.stdin.close()
        self.process.stdout.close()
        self.process.stderr.close()
        self.temporary.cleanup()

    def test_eof_closes_native_without_buffered_reader_crash(self):
        self.process.stdin.close()
        self.assertEqual(self.process.wait(timeout=5), 0)
        self.assertEqual(self.process.stderr.read(), b"")

    def test_watchdog_exits_with_stdin_still_open(self):
        started = time.monotonic()
        self.assertEqual(self.process.wait(timeout=42), 0)
        self.assertGreaterEqual(time.monotonic() - started, 33)
        self.assertFalse(self.process.stdin.closed)
        self.assertEqual(self.process.stderr.read(), b"")


if __name__ == "__main__":
    unittest.main()
