"""Mock-only checks: never runs sudo, systemd, or NVIDIA management tools."""

import importlib.machinery
import importlib.util
import io
import os
from pathlib import Path
import unittest
from unittest.mock import patch

loader = importlib.machinery.SourceFileLoader("clock_helper", str(Path(__file__).with_name("lanview-gpu-clock")))
spec = importlib.util.spec_from_loader(loader.name, loader)
clock = importlib.util.module_from_spec(spec)
loader.exec_module(clock)

CONFIG = {"managedBy": "LanViewGpuClock/1", "uid": 1000, "gpuPci": "0000:01:00.0", "clockMHz": 1200}
TOKEN = "a" * 32
HOST_TOKEN = "b" * 32
MARKER = {"invocationId": TOKEN, "gpuPci": CONFIG["gpuPci"], "clockMHz": 1200}


class ClockTests(unittest.TestCase):
    def test_valid_fixed_config(self):
        self.assertEqual(clock.validate_config(CONFIG.copy()), CONFIG)

    def test_reject_unsafe_config(self):
        for key, values in {
            "uid": [0, -1, True, "1000", 2147483648],
            "gpuPci": ["0", "--help", "0000:01:00.0;id", "0000:01:00.0\n", None],
            "clockMHz": [True, "1200", 0, 299, 3001],
            "managedBy": ["unknown"],
        }.items():
            for value in values:
                with self.subTest(key=key, value=value), self.assertRaises(clock.ClockError):
                    clock.validate_config(dict(CONFIG, **{key: value}))
        with self.assertRaises(clock.ClockError):
            clock.validate_config(dict(CONFIG, command="id"))

    def test_hold_rejects_wrong_user(self):
        with patch.object(clock, "read_config", return_value=CONFIG), patch.dict(os.environ, {"SUDO_UID": "0"}):
            with self.assertRaises(clock.ClockError):
                clock.hold()

    def test_hold_fixed_systemd_contract(self):
        with patch.object(clock, "read_config", return_value=CONFIG), patch.dict(os.environ, {"SUDO_UID": "1000"}), patch.object(clock.subprocess, "call", return_value=0) as call:
            self.assertEqual(clock.hold(), 0)
        command = call.call_args.args[0]
        for value in ["--pipe", "--wait", "--unit=lanview-gpu-clock.service", "--property=Restart=no", "--property=KillMode=control-group", "--property=ExecStopPost=/usr/local/libexec/lanview-gpu-clock reset", "--property=ProtectHome=read-only"]:
            self.assertIn(value, command)
        self.assertEqual(command[-2:], [clock.HELPER, "lease"])
        self.assertEqual(call.call_args.kwargs["env"], clock.ENV)

    def test_reset_only_owned_marker(self):
        with patch.object(clock, "invocation_id", return_value=TOKEN), patch.object(clock, "read_marker", return_value=dict(MARKER, invocationId="c" * 32)), patch.object(clock, "reset_marker") as reset:
            with self.assertRaises(clock.ClockError):
                clock.reset()
            reset.assert_not_called()

    def test_reset_no_marker_no_driver(self):
        with patch.object(clock, "invocation_id", return_value=TOKEN), patch.object(clock, "read_marker", return_value=None), patch.object(clock, "reset_marker") as reset:
            clock.reset()
            reset.assert_not_called()

    def test_reset_fail_keeps_marker(self):
        with patch.object(clock, "run_command", side_effect=clock.ClockError("driver error")), patch.object(clock, "MARKER") as marker:
            with self.assertRaises(clock.ClockError):
                clock.reset_marker(MARKER)
            marker.unlink.assert_not_called()

    def test_reset_all_done_is_success(self):
        with patch.object(clock, "run_command", return_value="All done.\n") as command, patch.object(clock, "MARKER") as marker:
            clock.reset_marker(MARKER)
            marker.unlink.assert_called_once_with()
            self.assertEqual(command.call_args.args[0], ["/usr/bin/nvidia-smi", "-i", CONFIG["gpuPci"], "-rgc", "--error-on-warning"])

    def run_lease(self, data=b"", output="GPU clocks set to test\n", old_marker=None, host_results=None):
        events = []
        with patch.object(clock, "invocation_id", return_value=TOKEN), patch.object(clock, "read_config", return_value=CONFIG), patch.object(clock, "host_invocation", side_effect=host_results or [HOST_TOKEN, HOST_TOKEN]), patch.object(clock, "read_marker", return_value=old_marker), patch.object(clock, "reset_marker", side_effect=lambda value: events.append("reset")), patch.object(clock, "write_marker", side_effect=lambda config, token: events.append("marker")), patch.object(clock, "run_command", side_effect=lambda *args, **kwargs: (events.append("apply"), output)[1]), patch.object(clock.select, "select", return_value=([0], [], [])), patch.object(clock.os, "read", return_value=data), patch.object(clock.sys, "stdin") as stdin, patch.object(clock.sys, "stdout", new_callable=io.StringIO) as stdout:
            stdin.fileno.return_value = 0
            try:
                clock.lease()
            except clock.ClockError as error:
                return events, stdout.getvalue(), error
            return events, stdout.getvalue(), None

    def test_eof_returns_after_ready(self):
        events, output, error = self.run_lease()
        self.assertEqual(events, ["marker", "apply"])
        self.assertEqual(output, "READY\n")
        self.assertIsNone(error)

    def test_prior_marker_reset_before_new_apply(self):
        events, _, error = self.run_lease(old_marker=MARKER)
        self.assertEqual(events, ["reset", "marker", "apply"])
        self.assertIsNone(error)

    def test_unsupported_driver_never_ready(self):
        _, output, error = self.run_lease(output="Setting locked GPU clocks is not supported\nAll done.\n")
        self.assertEqual(output, "")
        self.assertIsInstance(error, clock.ClockError)

    def test_inactive_host_never_applies(self):
        events, output, error = self.run_lease(host_results=[clock.ClockError("not active")])
        self.assertEqual(events, [])
        self.assertEqual(output, "")
        self.assertIsInstance(error, clock.ClockError)

    def test_changed_session_never_ready(self):
        _, output, error = self.run_lease(host_results=[HOST_TOKEN, "c" * 32])
        self.assertEqual(output, "")
        self.assertIsInstance(error, clock.ClockError)

    def test_reject_nonempty_heartbeats(self):
        for data in [b"id\n", b"\r\n", b"\0", b"\n" * 65]:
            with self.subTest(data=data):
                _, _, error = self.run_lease(data=data)
                self.assertIsInstance(error, clock.ClockError)


if __name__ == "__main__":
    unittest.main()
