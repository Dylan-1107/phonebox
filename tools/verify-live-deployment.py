#!/usr/bin/env python3
"""只读核验线上配置、传感器、USB 连接，以及 Agent 与指定 APK 的页面资源。"""
import argparse
import copy
import datetime
import hashlib
import json
from pathlib import Path
import urllib.request
import zipfile


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def public_config(config):
    result = copy.deepcopy(config['config'])
    # 图标缓存版本由运行时生成，不属于用户配置；其他字段一律参与比较。
    for shortcut in result.get('shortcuts', []):
        shortcut.pop('iconRev', None)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url', default='http://127.0.0.1:8765')
    parser.add_argument('--before-public', required=True, type=Path)
    parser.add_argument('--before-disk', required=True, type=Path)
    parser.add_argument('--config', required=True, type=Path)
    parser.add_argument('--apk', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    if args.output.exists():
        parser.error('证据文件已存在，请换一个名称，不覆盖旧证据')

    def fetch(path):
        with urllib.request.urlopen(args.url.rstrip('/') + path, timeout=10) as response:
            return response.read()

    def get(path):
        return json.loads(fetch(path))

    info, state, batteries = get('/api/info'), get('/api/state'), get('/api/batteries')
    pc = state.get('pc', {})
    resources = {}
    with zipfile.ZipFile(args.apk) as apk:
        for path in ('main.js', 'style.css', 'widgets/basic.js', 'widgets/helpers.js', 'widgets/pc.js'):
            packaged = apk.read('assets/www/' + path)
            resources[path] = {
                'matches_apk': fetch('/panel/' + path) == packaged,
                'sha256': hashlib.sha256(packaged).hexdigest(),
            }
    result = {
        'time': datetime.datetime.now().astimezone().isoformat(),
        'config_disk_unchanged': args.config.read_bytes() == args.before_disk.read_bytes(),
        'config_public_unchanged_except_iconRev':
            public_config(get('/api/config')) == public_config(read_json(args.before_public)),
        'info': {key: info.get(key) for key in ('connected', 'via', 'usbProblem', 'appMissing')},
        'pc': {key: pc.get(key) for key in ('cpuTemp', 'cpuPower', 'temperatures')},
        'battery_percentages': [
            {key: item.get(key) for key in ('name', 'level')}
            for item in batteries.get('devices', [])
        ],
        'apk_sha256': hashlib.sha256(args.apk.read_bytes()).hexdigest(),
        'agent_resources': resources,
    }
    checks = {
        'config_disk': result['config_disk_unchanged'],
        'config_public': result['config_public_unchanged_except_iconRev'],
        'lhm_connected': pc.get('temperatures', {}).get('lhmConnected') is True,
        'cpu_temperature': isinstance(pc.get('cpuTemp'), (int, float)),
        'cpu_power': isinstance(pc.get('cpuPower'), (int, float)),
        'usb_connected': info.get('connected') is True and info.get('via') == 'USB',
        'agent_apk_resources_match': all(item['matches_apk'] for item in resources.values()),
    }
    result['checks'] = checks
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'time': result['time'], 'checks': checks}, ensure_ascii=True))
    if not all(checks.values()):
        raise SystemExit('核验失败，原因已保存到证据文件')


if __name__ == '__main__':
    main()
