"""验证 OLED 工具的任务时间、手动唤醒语义和设备身份保护；不连接真机。"""
from datetime import datetime, timedelta, timezone
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("tablet_oled", ROOT / "scripts/tablet-oled.py")
oled = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(oled)


class ScheduleTests(unittest.TestCase):
    def xml(self, at="08:00", action="sleep", now=None):
        return ET.fromstring(oled.task_xml(
            Path("D:/project folder/tablet-oled.py"), Path("D:/local folder/tablet.json"),
            Path("C:/Program Files/Python313/pythonw.exe"), "S-1-5-21-123", at, action,
            now or datetime(2026, 11, 26, 3, 0, tzinfo=timezone(timedelta(hours=8)))))

    def value(self, root, path):
        return root.find(path, {"t": oled.NS}).text

    def test_four_daily_boundaries_only(self):
        self.assertEqual(oled.EVENTS, (("08:00", "sleep"), ("12:00", "wake"),
                                      ("18:00", "sleep"), ("02:00", "wake")))
        for at, action in oled.EVENTS:
            with self.subTest(at=at):
                root = self.xml(at, action)
                self.assertEqual(self.value(root, "t:Triggers/t:CalendarTrigger/t:ScheduleByDay/t:DaysInterval"), "1")
                self.assertIsNone(root.find(".//t:Repetition", {"t": oled.NS}))
                self.assertEqual(self.value(root, "t:Settings/t:StartWhenAvailable"), "false")
                self.assertEqual(self.value(root, "t:Settings/t:WakeToRun"), "false")
                self.assertIsNone(root.find(".//t:RestartOnFailure", {"t": oled.NS}))

    def test_passed_boundary_is_tomorrow_not_immediately(self):
        root = self.xml("02:00", "wake")
        self.assertEqual(self.value(root, "t:Triggers/t:CalendarTrigger/t:StartBoundary"), "2026-11-27T02:00:00")

    def test_upcoming_boundary_uses_local_time(self):
        root = self.xml()
        self.assertEqual(self.value(root, "t:Triggers/t:CalendarTrigger/t:StartBoundary"), "2026-11-26T08:00:00")

    def test_task_has_no_password_or_elevation_or_policy_override(self):
        root = self.xml()
        self.assertEqual(self.value(root, "t:Principals/t:Principal/t:LogonType"), "InteractiveToken")
        self.assertEqual(self.value(root, "t:Principals/t:Principal/t:RunLevel"), "LeastPrivilege")
        arguments = self.value(root, "t:Actions/t:Exec/t:Arguments")
        self.assertTrue(arguments.startswith('-I "D:'))
        self.assertIn(subprocess.list2cmdline([str(Path("D:/local folder/tablet.json"))]), arguments)
        self.assertNotIn("Bypass", arguments)
        self.assertNotIn("POWER", arguments)


class DeviceTests(unittest.TestCase):
    def setUp(self):
        directory = ROOT / ".temp_files"
        directory.mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=directory)
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        config = root / "config.json"
        config.write_text(json.dumps({"adb": "sdk-adb.exe", "device_serial": "tablet-id",
                                    "model": "tablet", "endpoint": "old-ip:5555",
                                    "evidence_dir": str(root / "evidence")}), encoding="utf-8")
        self.tablet = oled.Tablet(config)

    @staticmethod
    def result(value="", code=0):
        return subprocess.CompletedProcess([], code, value.encode(), b"")

    def test_identity_not_ip_selects_tablet(self):
        def adb(*args, **kwargs):
            if args == ("devices",):
                return self.result("List of devices attached\nphone-ip:5555\tdevice\nnew-ip:5555\tdevice\n")
            if args[3:] == ("getprop", "ro.serialno"):
                return self.result("tablet-id" if args[1] == "new-ip:5555" else "phone-id")
            return self.result("tablet")
        with patch.object(self.tablet, "adb", side_effect=adb) as calls:
            self.assertEqual(self.tablet.resolve(), "new-ip:5555")
            self.assertFalse(any(call.args[0] == "connect" for call in calls.call_args_list))

    def test_unknown_device_is_never_controlled(self):
        def adb(*args, **kwargs):
            return self.result("other\tdevice\n" if args == ("devices",) else "not-tablet")
        with patch.object(self.tablet, "adb", side_effect=adb) as calls:
            with self.assertRaisesRegex(RuntimeError, "未找到"):
                self.tablet.resolve()
            self.assertFalse(any("input" in call.args for call in calls.call_args_list))

    def test_model_mismatch_fails_closed(self):
        def adb(*args, **kwargs):
            if args == ("devices",):
                return self.result("target\tdevice\n")
            return self.result("tablet-id" if args[-1] == "ro.serialno" else "wrong model")
        with patch.object(self.tablet, "adb", side_effect=adb):
            with self.assertRaisesRegex(RuntimeError, "型号"):
                self.tablet.resolve()

    def test_sleep_and_wake_do_not_toggle_or_dismiss_lock(self):
        for action, key, state in (("sleep", "KEYCODE_SLEEP", "Asleep"),
                                   ("wake", "KEYCODE_WAKEUP", "Awake")):
            with self.subTest(action=action), patch.object(self.tablet, "shell", side_effect=("", f"mWakefulness={state}")) as shell, patch.object(oled.time, "sleep"):
                self.tablet.act(action)
                self.assertEqual(shell.call_args_list[0].args, ("input", "keyevent", key))
                self.assertEqual(shell.call_count, 2)

    def test_power_key_test_does_not_flip_an_unexpected_state(self):
        with patch.object(self.tablet, "shell", return_value="mWakefulness=Asleep") as shell:
            with self.assertRaisesRegex(RuntimeError, "初始状态"):
                self.tablet.act("sleep", power_key=True)
            self.assertEqual(shell.call_count, 1)

    def test_failed_screen_change_is_reported(self):
        with patch.object(self.tablet, "shell", return_value="mWakefulness=Awake"), patch.object(oled.time, "sleep"):
            with self.assertRaisesRegex(RuntimeError, "不符合预期"):
                self.tablet.act("sleep")

    def test_doze_clock_is_not_accepted_as_true_sleep(self):
        with patch.object(self.tablet, "shell", return_value="mWakefulness=Dozing"), patch.object(oled.time, "sleep"):
            with self.assertRaisesRegex(RuntimeError, "不符合预期"):
                self.tablet.act("sleep")

    def test_early_manual_wake_is_not_misreported_as_sleep(self):
        with patch.object(self.tablet, "shell", return_value="mWakefulness=Awake"):
            with self.assertRaisesRegex(RuntimeError, "状态已变化"):
                self.tablet.expect_state("sleep")

    def test_evidence_label_cannot_escape_directory(self):
        with self.assertRaises(ValueError):
            self.tablet.snapshot("../outside")


if __name__ == "__main__":
    unittest.main(verbosity=2)
