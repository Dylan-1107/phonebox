#!/usr/bin/env python3
"""通过已安装的 SDK adb 保存实机截图、可选界面树和带本地时区的证据索引。"""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import struct
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--adb', required=True, type=Path)
    parser.add_argument('--port', type=int, default=5037)
    parser.add_argument('--serial', required=True)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--name', required=True)
    parser.add_argument('--ui', action='store_true')
    args = parser.parse_args()
    if Path(args.name).name != args.name or not args.name:
        parser.error('--name 只能是文件名，不能带目录')
    args.output.mkdir(parents=True, exist_ok=True)
    image = args.output / (args.name + '.png')
    if image.exists():
        parser.error('截图已存在，请换一个名称，不覆盖旧证据')
    adb = [str(args.adb), '-P', str(args.port), '-s', args.serial]
    result = subprocess.run(adb + ['exec-out', 'screencap', '-p'],
                            check=True, capture_output=True, timeout=20)
    data = result.stdout
    if len(data) < 24 or data[:8] != b'\x89PNG\r\n\x1a\n':
        raise RuntimeError('设备没有返回有效 PNG，未写入截图')
    width, height = struct.unpack('>II', data[16:24])
    image.write_bytes(data)
    record = {
        'time': datetime.datetime.now().astimezone().isoformat(),
        'serial': args.serial, 'image': image.name,
        'width': width, 'height': height, 'bytes': len(data),
        'sha256': hashlib.sha256(data).hexdigest(),
    }
    if args.ui:
        result = subprocess.run(adb + ['exec-out', 'uiautomator', 'dump', '/dev/tty'],
                                check=True, capture_output=True, timeout=30)
        text = result.stdout.decode('utf-8')
        start, end = text.find('<?xml'), text.rfind('</hierarchy>')
        if start < 0 or end < 0:
            raise RuntimeError('界面树未返回完整 XML；截图已经保留')
        tree = args.output / (args.name + '.xml')
        tree.write_text(text[start:end + len('</hierarchy>')], encoding='utf-8')
        record['ui'] = tree.name
    with (args.output / 'captures.jsonl').open('a', encoding='utf-8') as output:
        output.write(json.dumps(record, ensure_ascii=False) + '\n')
    print(json.dumps(record, ensure_ascii=False))


if __name__ == '__main__':
    main()
