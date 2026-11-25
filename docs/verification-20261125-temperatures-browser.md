# 温度与浏览器/Dock 实机验收

记录时间：2026-11-25 17:50:25 +08:00（本机时区）。

## 结论

- 温度已接通，Agent 与 Lenovo TB-J716F 的 APK 已更新，已实际打开页面查看。
- System 保留原来的 CPU、GPU、内存/磁盘、网络四块；Home 沿用原统计卡片，没有新增区域。
- 浏览器填满原卡片内容区，不再固定减去 Dock 高度。Dock 位置不变，展开后覆盖网页，能够点击切换 Home/System；实际网页滚动后再次验证通过。
- 没有卸载、清理应用数据或回写配置；没有操作另一台设备、CrossDesk、共享 ADB 5037 或原工作区的未提交代码。

## 实机证据

证据和本轮回退备份统一保存在：
`D:/muse/phonebox/phonebox-browser-dock-20261125-yxchy6r2/`

| 文件 | 已亲自查看/核验的内容 |
|---|---|
| `tablet-browser-dock-collapsed.png` | 真实网页已加载，Dock 收起，浏览器底部没有大块固定预留 |
| `tablet-browser-dock-over-page.png` | Dock 展开覆盖真实网页，位置未改 |
| `tablet-home-after-browser-dock-click.png` | 点击网页之上的 Dock 后成功切到 Home |
| `tablet-browser-after-single-scroll.png` | 从总览正常进入浏览器后，网页滚动正常 |
| `tablet-browser-scrolled-dock-verified.png` | 网页滚动后 Dock 仍可展开覆盖 |
| `tablet-system-after-browser-dock-verified.png` | 滚动后点击 Dock 成功切到 System |
| `tablet-home-final.png` | Home 温度/功耗、USB、键盘 37%、鼠标 51% 正常 |
| `tablet-system-final.png` | System 两条 DIMM、两块物理 SSD、CPU/GPU 温度均显示，未映射成逻辑分区温度 |
| `live-final-checks.json` | 配置未变、LHM 接通、真实温度功耗、USB 连通、Agent/APK 页面资源一致，全部核验为 true |
| `installed-apk-verification.log` | 已安装 base.apk 哈希匹配；首次安装时间未变 |
| `captures.jsonl`、`deployment-events.jsonl` | 带本地时区的截图元数据与逐项结果 |

早先的 `tablet-browser-scrolled-dock.png`、`tablet-system-after-scrolled-browser-dock.png`、`tablet-browser-before-scroll-check.png` 操作中进入了页面总览，**不是通过证据**。后续退出原生网页、从总览重新进入浏览器，单独滚动和点击后得到上表的 `verified` 证据。旧截图保留，不覆盖或删除。

## 构建与测试

- Playwright 完整回归：95 passed，见 `ui-95-tests.log`。
- Android JVM：17 项，0 失败/错误/跳过，见 `TEST-net.oxog.phonebox.deskpanel.WakeTest.xml`、`jvm-test-summary.json`。
- Lenovo 真机原生专项：2 项通过，见 `native-dock-tests.log`。分别检查 Dock 裁剪/滚动清理，以及触摸/主键鼠标的手势归属。
- 传感器真实 xUnit 断言：反射 runner 执行 133 passed / 0 failed，见 `sensor-133-assertions.log`；**不是标准 `dotnet test` 成功**，本机 VSTest testhost 的 OpenProcess 权限问题仍在。
- Agent publish、debug APK 与 androidTest APK 构建通过；Agent 原有 CS0649 警告保留。
- 完整 lint **未通过**：未修改的 `strings.xml:19` 中 `web_back_to_panel` 缺少翻译，见 `lint-results-debug.txt/xml`。没有放宽配置绕过。

## 上线版本与保留项

- 最终不可变 APK：上述目录中的 `app-new.apk`。
- 已安装 SHA-256：`3801cbe78fe8f19eb220d075c93a0f432bec0ccfed86b8c5e94c96e7af62a3b9`。
- 平板 lastUpdateTime：2026-11-25 17:40:18；firstInstallTime 仍为 2026-11-24 02:23:40。
- Agent 本轮已替换 454 文件并核对哈希；线上目录 `%LOCALAPPDATA%/PhoneBox/app`。
- 磁盘 `%APPDATA%/PhoneBox/config.json` 与切换前逐字节一致；public config 比较仅排除动态 `shortcuts.iconRev`，其他字段一致。
- `agent-before/`、`roaming-data-before/`、`apk-before.apk` 是本轮回退材料。旧 APK SHA-256 为 `a18044617d6e985834e135b4d9b54d13fac3106066734f9123f8e9bca6bc98e0`。
- 备份中含本机私有配置，禁止提交或对外上传。
- 未提交、未推送。原工作区 `D:/muse/phonebox/src` 仍只有原来的 `main.js` 修改，未触碰。
- 实机验收后，源码中的本次新增注释另行整理为中文，不改行为。为避免仅为注释再次重启设备，没有重新构建/部署这一轮注释文字；上面的包哈希与 Agent/APK 资源一致性指已验收的部署包，不声称后续源码资源仍逐字节相同。

## 运行条件

- LibreHardwareMonitor 官方 0.9.6 已安装在 `%LOCALAPPDATA%/Programs/LibreHardwareMonitor/0.9.6`。追加于 **2026-11-25 17:59:24 +08:00**：用户要求开机自启后，已注册官方同款的根目录计划任务 `\LibreHardwareMonitor`，当前用户登录时以 Highest 权限启动，保留最小化/托盘设置。已短暂停止旧监控进程并实际触发该任务，新进程成功运行、本机 8085 返回 200、Agent CPU/RAM 读数恢复，Agent 未重启；配置和防火墙限制未变。证据：`D:/muse/phonebox/temperature-checks/lhm-autostart-20261125-175812/` 下的 `registration.json`、`task.xml`、`startup-test.json`、`agent-after-startup-test.json`。任务结果 `0x00041301` 表示正在运行，不是失败。**没有为测试重启电脑或重新登录**。关闭监控程序仍会使 CPU/RAM 传感器不可用。
- 没有安装或升级驱动；机器原有 PawnIO 2.2.0。
- LHM 即使设置 127.0.0.1，也会在本机回退到 HTTP.sys 通配监听。必须保留入站阻止规则 `PhoneBox LHM local-only 8085`；本机读取正常，平板通过 LAN 访问原始 8085 接口超时。
- 本会话专用 ADB 5038 曾不响应；核对所属 SDK adb 进程后只重启了该专用服务，恢复安装与验证。未重启共享 5037。线上 Agent 仅使用进程级 `ADB_SERVER_SOCKET=tcp:127.0.0.1:5038`，未修改全局环境。
- 最终核对到平板 reverse：`tcp:8765 → tcp:8403`，8403 是当时线上 Agent 的 USB TLS 监听端口；以后重启会变化，不要硬编码沿用。

## 可复用验证工具

- `tools/capture-tablet.py`：截图/可选界面树、PNG 格式/尺寸检查、时间和哈希索引；拒绝覆盖旧证据。
- `tools/verify-live-deployment.py`：只读比较上线前后配置，读取真实 CPU/RAM/SSD、USB 与外设状态，比较线上 Agent 和指定 APK 的页面资源；不输出完整配置或凭据。
- 两个工具已在本机实际执行并产出上述证据，使用 `python -I ... --help` 查看参数；ADB 使用工作区外已安装的 `D:/devstack/android-sdk/platform-tools/adb.exe`，目标仅 `192.168.1.169:5555`。
