import base64
import importlib.util
import os
from pathlib import Path
import tempfile
import threading
import unittest

spec = importlib.util.spec_from_file_location("bridge", Path(__file__).parents[1] / "bridge.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


class TransferTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)

    def tearDown(self):
        self.directory.cleanup()

    def test_path_rejects_cross_platform_ambiguities(self):
        for name in ["../x", "/x", "a\\b", "a:b", "x/..", "CON.txt", "COM0", "LPT¹.log", "CONIN$", "x.", "x ", "a//b", "x\x00", "x\x7f"]:
            with self.subTest(name=name), self.assertRaises(bridge.Invalid):
                bridge.valid_path(name)

    def test_client_hello_and_ping_do_not_require_identifiers(self):
        output = []
        app = bridge.Bridge(lambda _: None, self.root, output.append, threading.Event())
        app.command({"type": "hello", "version": 1})
        app.command({"type": "ping"})
        self.assertEqual(output, [])
        app.native_event("READY\n")
        self.assertEqual(output, [{"type": "hello", "version": 1}])

    def test_manifest_requires_parents_and_unique_case(self):
        for entries in [
            [{"path": "a/b", "size": 0, "directory": False}],
            [{"path": "a", "size": 0, "directory": False}, {"path": "A", "size": 0, "directory": False}],
            [{"path": "a", "size": True, "directory": False}],
            [{"path": "a", "size": bridge.MAX_BYTES + 1, "directory": False}],
        ]:
            with self.assertRaises(bridge.Invalid):
                bridge.entries_checked(entries)

    def test_upload_requires_complete_ordered_chunks(self):
        upload = bridge.Upload(self.root, "test", [{"path": "folder", "size": 0, "directory": True},
                                                    {"path": "folder/file.txt", "size": 3, "directory": False}])
        with self.assertRaises(bridge.Invalid):
            upload.commit()
        with self.assertRaises(bridge.Invalid):
            upload.chunk("folder/file.txt", 1, "YQ==")
        with self.assertRaises(bridge.Invalid):
            upload.chunk("folder/file.txt", 0, "!!!!")
        upload.chunk("folder/file.txt", 0, "YWJj")
        self.assertEqual((upload.commit()[0] / "file.txt").read_bytes(), b"abc")

    def test_partial_cleanup_stays_in_owned_stage(self):
        keep = self.root / "keep"
        keep.write_text("keep")
        upload = bridge.Upload(self.root, "test", [{"path": "file", "size": 1, "directory": False}])
        stage = upload.root
        upload.discard()
        self.assertFalse(stage.exists())
        self.assertEqual(keep.read_text(), "keep")

    def test_symlinks_and_special_files_rejected(self):
        target = self.root / "target"
        target.write_text("data")
        link = self.root / "link"
        link.symlink_to(target)
        with self.assertRaises(bridge.Invalid):
            bridge.uri_paths(link.as_uri())
        folder = self.root / "folder"
        folder.mkdir()
        (folder / "nested").symlink_to(target)
        with self.assertRaises(bridge.Invalid):
            bridge.snapshot([folder])

    def test_open_does_not_follow_changed_parent(self):
        folder = self.root / "folder"
        folder.mkdir()
        (folder / "file").write_text("data")
        selected = folder / "file"
        folder.rename(self.root / "original")
        folder.symlink_to(self.root / "original", target_is_directory=True)
        with self.assertRaises(OSError):
            bridge.open_nofollow(selected)

    def test_echo_suppression_allows_later_user_copy(self):
        echo = bridge.Echo()
        echo.expect("text", "one")
        self.assertFalse(echo.observe("text", "one"))
        self.assertFalse(echo.observe("text", "one"))
        self.assertTrue(echo.observe("text", "two"))
        self.assertTrue(echo.observe("text", "one"))

    def test_commit_publishes_only_after_bytes_arrive(self):
        commands, output = [], []
        app = bridge.Bridge(commands.append, self.root, output.append, threading.Event())
        app.command({"type": "upload-begin", "id": "x", "entries": [{"path": "file", "size": 1, "directory": False}]})
        self.assertEqual(commands, [])
        app.command({"type": "upload-chunk", "id": "x", "path": "file", "offset": 0, "data": "YQ=="})
        self.assertEqual(commands, [])
        app.command({"type": "upload-commit", "id": "x"})
        self.assertTrue(commands[0].startswith("FILES\tx\t"))
        self.assertEqual(output, [])
        app.native_event("ACK\tx\n")
        self.assertEqual(output, [{"type": "ack", "id": "x"}])

    def test_new_upload_and_cancel_discard_only_incomplete(self):
        app = bridge.Bridge(lambda _: None, self.root, lambda _: None, threading.Event())
        item = [{"path": "file", "size": 1, "directory": False}]
        app.command({"type": "upload-begin", "id": "one", "entries": item})
        old = app.upload.root
        app.command({"type": "upload-begin", "id": "two", "entries": item})
        self.assertFalse(old.exists())
        app.command({"type": "upload-cancel", "id": "two"})
        self.assertIsNone(app.upload)

    def test_download_only_copied_latest_offer(self):
        file = self.root / "source.txt"
        file.write_text("copied")
        output = []
        app = bridge.Bridge(lambda _: None, self.root, output.append, threading.Event())
        app.native_event("FILES\t" + base64.b64encode(file.as_uri().encode()).decode() + "\n")
        identifier = output[-1]["id"]
        with self.assertRaises(bridge.Invalid):
            app.command({"type": "download", "id": "unrelated"})
        app.command({"type": "download", "id": identifier})
        app.download_job[2].join(2)
        self.assertEqual(output[-1]["type"], "download-complete")
        self.assertEqual(base64.b64decode(output[-2]["data"]), b"copied")
        app.command({"type": "ping"})
        app.close()

    def test_unsupported_clipboard_change_invalidates_old_download(self):
        file = self.root / "source.txt"
        file.write_text("copied")
        output = []
        app = bridge.Bridge(lambda _: None, self.root, output.append, threading.Event())
        app.native_event("FILES\t" + base64.b64encode(file.as_uri().encode()).decode() + "\n")
        identifier = output[-1]["id"]
        app.native_event("CHANGED\n")
        with self.assertRaises(bridge.Invalid):
            app.command({"type": "download", "id": identifier})

    def test_cancel_download_ack_after_worker_stops(self):
        file = self.root / "source.txt"
        file.write_bytes(b"a" * (bridge.CHUNK * 10))
        output = []
        app = bridge.Bridge(lambda _: None, self.root, output.append, threading.Event())
        app.native_event("FILES\t" + base64.b64encode(file.as_uri().encode()).decode() + "\n")
        identifier = output[-1]["id"]
        app.command({"type": "download", "id": identifier})
        app.command({"type": "download-cancel", "id": identifier})
        self.assertIsNone(app.download_job)
        self.assertEqual(output[-1], {"type": "ack", "id": identifier})


if __name__ == "__main__":
    unittest.main()
