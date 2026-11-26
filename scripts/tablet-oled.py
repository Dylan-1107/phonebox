"""用 Windows 每日任务控制 OLED 平板；手动唤醒后不在本时段重复熄屏。

用法：python -I scripts/tablet-oled.py --config <本机配置.json> status|cycle|install
配置只存本机：adb、device_serial、model、endpoint、evidence_dir。
install 注册四个每日边界动作，不轮询，不修改 APP 或 Android 亮度。
"""
import argparse
import csv
from datetime import datetime, timedelta
import io
import json
from pathlib import Path
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

EVENTS = (("08:00", "sleep"), ("12:00", "wake"),
          ("18:00", "sleep"), ("02:00", "wake"))
NS = "http://schemas.microsoft.com/windows/2004/02/mit/task"
ET.register_namespace("", NS)


def command(args, *, timeout=15, check=True):
    result = subprocess.run([str(a) for a in args], capture_output=True, timeout=timeout,
                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    if check and result.returncode:
        raise RuntimeError(f"命令失败 ({result.returncode}): "
                           + result.stderr.decode("utf-8", errors="replace"))
    return result


def text(result):
    return result.stdout.decode("utf-8", errors="replace").strip()


def task_xml(script, config, pythonw, sid, at, action, now=None):
    now = now or datetime.now().astimezone()
    hour, minute = map(int, at.split(":"))
    start = now.replace(hour=hour, minute=minute, second=0, microsecond=0)
    if start <= now:
        start += timedelta(days=1)
    root = ET.Element(f"{{{NS}}}Task", version="1.4")

    def add(parent, name, value=None, **attrs):
        element = ET.SubElement(parent, f"{{{NS}}}{name}", attrs)
        if value is not None:
            element.text = str(value)
        return element

    info = add(root, "RegistrationInfo")
    add(info, "Description", "PhoneBox OLED：仅在每日边界熄屏/唤醒，尊重本轮手动唤醒。")
    trigger = add(add(root, "Triggers"), "CalendarTrigger")
    # 不写固定 UTC 偏移：每天按 Windows 本地时间执行。
    add(trigger, "StartBoundary", start.replace(tzinfo=None).isoformat())
    add(trigger, "Enabled", "true")
    add(add(trigger, "ScheduleByDay"), "DaysInterval", "1")
    principal = add(add(root, "Principals"), "Principal", id="Author")
    add(principal, "UserId", sid)
    add(principal, "LogonType", "InteractiveToken")
    add(principal, "RunLevel", "LeastPrivilege")
    settings = add(root, "Settings")
    for name, value in (
        ("MultipleInstancesPolicy", "IgnoreNew"),
        ("DisallowStartIfOnBatteries", "false"),
        ("StopIfGoingOnBatteries", "false"),
        ("StartWhenAvailable", "false"),
        ("Enabled", "true"), ("Hidden", "true"),
        ("WakeToRun", "false"), ("ExecutionTimeLimit", "PT2M"),
    ):
        add(settings, name, value)
    execute = add(add(root, "Actions", Context="Author"), "Exec")
    add(execute, "Command", pythonw)
    add(execute, "Arguments", subprocess.list2cmdline(
        ["-I", str(script), "--config", str(config), action]))
    add(execute, "WorkingDirectory", config.parent)
    return ET.tostring(root, encoding="utf-16", xml_declaration=True)


class Tablet:
    def __init__(self, config):
        self.path = Path(config).resolve()
        self.cfg = json.loads(self.path.read_text(encoding="utf-8-sig"))
        self.out = Path(self.cfg["evidence_dir"])
        self.out.mkdir(parents=True, exist_ok=True)
        self.target = None

    def log(self, event, **data):
        record = {"time": datetime.now().astimezone().isoformat(), "event": event, **data}
        line = json.dumps(record, ensure_ascii=False)
        with (self.out / "events.jsonl").open("a", encoding="utf-8") as stream:
            stream.write(line + "\n")
        if sys.stdout is not None:
            print(line)

    def adb(self, *args, **kwargs):
        return command([self.cfg["adb"], *args], **kwargs)

    def resolve(self):
        if self.target is not None:
            return self.target
        def connected_target():
            candidates = [line.split()[0] for line in text(self.adb("devices")).splitlines()
                          if re.match(r"^\S+\s+device\s*$", line)]
            for candidate in candidates:
                serial = self.adb("-s", candidate, "shell", "getprop", "ro.serialno", check=False)
                if serial.returncode or text(serial) != self.cfg["device_serial"]:
                    continue
                model = text(self.adb("-s", candidate, "shell", "getprop", "ro.product.model"))
                if model != self.cfg["model"]:
                    raise RuntimeError("平板型号与配置不符，未执行屏幕控制")
                return candidate
            return None

        self.target = connected_target()
        # 优先使用已连接设备，兼容 DHCP 换地址；仅重连已知地址，不扫描局域网。
        if self.target is None and self.cfg.get("endpoint"):
            self.adb("connect", self.cfg["endpoint"], check=False)
            self.target = connected_target()
        if self.target is None:
            raise RuntimeError("未找到身份匹配的平板，未控制其他设备")
        return self.target

    def shell(self, *args):
        return text(self.adb("-s", self.resolve(), "shell", *args))

    def state(self):
        match = re.search(r"mWakefulness=(\w+)", self.shell("dumpsys", "power"))
        return match.group(1) if match else "unknown"

    def expect_state(self, action):
        expected = {"sleep": "Asleep", "wake": "Awake"}[action]
        current = self.state()
        if current != expected:
            raise RuntimeError(f"验收时电源状态已变化：预期 {expected}，实际 {current}")

    def act(self, action, *, power_key=False):
        # 定时任务只用幂等的 SLEEP/WAKEUP；POWER 仅用于显式真机验收。
        key = {"sleep": "KEYCODE_SLEEP", "wake": "KEYCODE_WAKEUP"}[action]
        if power_key:
            awake = self.state() == "Awake"
            if awake != (action == "sleep"):
                raise RuntimeError("电源键验收的初始状态不符，未发送翻转按键")
            key = "KEYCODE_POWER"
        self.shell("input", "keyevent", key)
        for _ in range(8):
            time.sleep(0.5)
            state = self.state()
            if (action == "wake" and state == "Awake") or (
                    action == "sleep" and state == "Asleep"):
                self.log(action, target=self.target, wakefulness=state)
                return
        raise RuntimeError(f"{action} 后电源状态不符合预期：{state}")

    def snapshot(self, label):
        if not re.fullmatch(r"[A-Za-z0-9_-]+", label):
            raise ValueError("证据标签只能包含字母、数字、下划线和连字符")
        prefix = self.out / (datetime.now().strftime("%Y%m%d-%H%M%S-%f") + "-" + label)
        for name, args in (("power", ("dumpsys", "power")),
                           ("keyguard", ("dumpsys", "window", "policy")),
                           ("window", ("dumpsys", "window"))):
            Path(str(prefix) + f"-{name}.log").write_text(self.shell(*args), encoding="utf-8")
        image = self.adb("-s", self.resolve(), "exec-out", "screencap", "-p").stdout
        Path(str(prefix) + ".png").write_bytes(image)
        values = {
            "screen_off_timeout": self.shell("settings", "get", "system", "screen_off_timeout"),
            "stay_on_while_plugged_in": self.shell("settings", "get", "global", "stay_on_while_plugged_in"),
            "keyguard_disabled": self.shell("locksettings", "get-disabled"),
            "double_tap_to_wake": self.shell("settings", "get", "secure", "double_tap_to_wake"),
            "wakefulness": self.state(),
        }
        self.log("snapshot", label=label, prefix=str(prefix), **values)
        return prefix

    def verify_tasks(self):
        for at, action in EVENTS:
            name = f"PhoneBox OLED {action.title()} {at.replace(':', '')}"
            script = (
                f"$t=Get-ScheduledTask -TaskName '{name}'; $i=$t | Get-ScheduledTaskInfo; "
                "[pscustomobject]@{state=$t.State.ToString(); result=$i.LastTaskResult; "
                "last=$i.LastRunTime.ToString('o')} | ConvertTo-Json -Compress")

            def info():
                return json.loads(text(command(["powershell.exe", "-NoProfile", "-NonInteractive",
                                                 "-Command", script])))

            previous = info()["last"]
            command(["schtasks.exe", "/Run", "/TN", name])
            for _ in range(30):
                time.sleep(0.5)
                current = info()
                if current["last"] != previous and current["state"] == "Ready":
                    break
            else:
                raise RuntimeError(f"计划任务未在期限内完成：{name}")
            self.log("task-result", name=name, **current)
            if current["result"] != 0:
                raise RuntimeError(f"计划任务执行失败：{name}，结果 {current['result']}")
            self.snapshot("task-" + action + "-" + at.replace(":", ""))
            self.expect_state(action)

    def install(self):
        pythonw = Path(sys.executable).with_name("pythonw.exe")
        if not pythonw.is_file():
            raise RuntimeError("未找到 pythonw.exe；不注册会弹出控制台的任务")
        sid_result = text(command(["whoami.exe", "/user", "/fo", "csv", "/nh"]))
        sid = next(csv.reader(io.StringIO(sid_result)))[1]
        if not re.fullmatch(r"S-1-[0-9-]+", sid):
            raise RuntimeError("未取得当前用户 SID，未注册任务")
        names = [(at, action, f"PhoneBox OLED {action.title()} {at.replace(':', '')}")
                 for at, action in EVENTS]
        # 先检查所有目标，绝不覆盖已有同名任务。
        for _, _, name in names:
            if command(["schtasks.exe", "/Query", "/TN", name], check=False).returncode == 0:
                raise RuntimeError(f"任务已存在，未覆盖：{name}")
        for at, action, name in names:
            path = self.out / (name.replace(" ", "-") + ".xml")
            path.write_bytes(task_xml(Path(__file__).resolve(), self.path, pythonw, sid, at, action))
            command(["schtasks.exe", "/Create", "/TN", name, "/XML", path])
            self.log("task-installed", name=name, at=at, action=action, xml=str(path))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("action", choices=("status", "sleep", "wake", "cycle", "install", "verify-tasks"))
    parser.add_argument("--label", default="status")
    parser.add_argument("--power-key", action="store_true", help="cycle 用电源键验收；不改变定时动作")
    parser.add_argument("--hold-seconds", type=float, default=8, help="cycle 熄屏后的等待秒数")
    args = parser.parse_args()
    tablet = Tablet(args.config)
    try:
        if args.action == "status":
            tablet.snapshot(args.label)
        elif args.action == "cycle":
            tablet.snapshot(args.label + "-before")
            tablet.act("sleep", power_key=args.power_key)
            time.sleep(max(0, args.hold_seconds))
            tablet.snapshot(args.label + "-asleep")
            tablet.expect_state("sleep")
            tablet.act("wake", power_key=args.power_key)
            time.sleep(1)
            tablet.snapshot(args.label + "-awake")
        elif args.action == "install":
            tablet.install()
        elif args.action == "verify-tasks":
            tablet.verify_tasks()
        else:
            tablet.act(args.action)
    except Exception as exc:
        tablet.log("failed", action=args.action, error=str(exc))
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
