**在 PhoneBox 浏览器页使用 CrossDesk**

[返回使用指南](GUIDE.md)

固定站点已经支持用户自行配置，不需要在 APK 中新增内置站点，也不需要重装 PhoneBox。

1. 在电脑上启动 PhoneBox Agent，打开设置：<http://127.0.0.1:8765>。
2. 进入页面编辑器，选中现有「浏览器页」（带卡片和固定站点标签的那一页）。它应已启用「网页页面 / Web page」。不要把普通小组件页或 Codex 配额页直接改成网页页：启用该开关会清空该页的小组件。
3. 站点文本框保留原有各行的同时，把指向本地端口的行（例如 `Desk | http://192.168.1.240:52888/`）换成：

   ```text
   CrossDesk | https://web.crossdesk.cn
   ```

   名称也可以写「远程桌面」。每行格式是 `名称 | URL`，每页最多 12 个站点。局域网 IP 那一行留着也不会成功，因为网页版不认 IP 直连。
4. 点击「保存 / Save」，等待平板同步配置，在浏览器页顶部点「CrossDesk」标签。
5. 输入电脑端 CrossDesk 显示的 **9 位设备 ID 和密码**，再点「连接」。凭据只在 CrossDesk 页面输入，不要写进 PhoneBox 站点配置或仓库。

若设置地址打不开，先确认电脑端 PhoneBox Agent 已运行；不需要为了添加这个站点重编译 Agent。若远控时自动跳回首页，可将这一个浏览器页的「空闲后返回首页 / Back home after」设为「从不 / Never」，不要改其它页面或平板亮度设置。

**连接方式与现有设备**

CrossDesk 官方网页版使用 9 位 ID + 密码连接，握手依赖官方信令服务器。它不支持局域网 `IP:端口` 直连；IP 直连是原生 App 的功能。电脑和平板即使在同一局域网，也不能用局域网地址代替网页里的设备 ID。

本次接入环境（用户提供，2026-11-24）：电脑已装 CrossDesk 便携版 v1.7.7，局域网地址为 `192.168.1.240:52888`；平板是联想 TB-J716F，已安装原生包 `cn.crossdesk.mobile`。该地址只用于原生客户端直连，不用于上述网页配置。

**被控端必须先关掉「仅允许可信设备控制」**

2026-11-24 实测：电脑端 CrossDesk 开着「设置 › 常规 › 可信设备 › 仅允许可信设备控制」时，浏览器客户端能连上信令、也能发起会话，但被控端日志报
`device_trust_runtime.cpp: Reject controller: trusted device verification required; reason=proof_timeout`，随后断开，控制端只看到黑屏。电脑端 Chrome 和 WebView 表现一致，所以这不是浏览器或内核的问题：浏览器没有设备身份，无法加入可信设备列表。

把关掉的办法：CrossDesk 设置 › 常规，滚动到「可信设备」，关闭「仅允许可信设备控制」，保存。该开关在 `data/config.ini` 里是 `trusted_devices_only`；关掉后只要知道 9 位 ID 和密码就能连，需要时可以再打开。仅修改 config.ini 不会影响正在运行的进程，必须走界面或重启。

同页还有两个会造成困扰的开关：`lock_on_disconnect`（连接断开后锁屏，会在控制端断开时锁住被控电脑）和「被连接时开启隐私屏」（被控端显示器会看到隐私屏）。提前按需要调整。

**PhoneBox 的 WebView 设置**

浏览器页使用独立 Android WebView，不是 iframe，不向外部站点附加 PhoneBox 的 Android JS bridge。

现有 `MainActivity.siteView()` 已开启 JavaScript、DOM storage 和 `mediaPlaybackRequiresUserGesture = false`（即 `setMediaPlaybackRequiresUserGesture(false)`）。因此不需要为 CrossDesk 重复增加自动播放开关。

接收远端 WebRTC 视频、声音和数据通道不需要平板的摄像头或麦克风采集权限。若站点确实请求本地麦克风，现有 `WebChromeClient` 会经过 Android `RECORD_AUDIO` 授权，仅授予 `RESOURCE_AUDIO_CAPTURE`；清单也已声明 `MODIFY_AUDIO_SETTINGS`。不要为了播放远端桌面直接放行全部 `request.resources`，也不要新增摄像头或本地屏幕采集授权。当前麦克风授权逻辑不是仅限 CrossDesk，故只添加自己信任的站点。

切换站点通常保留原 WebView，但隐藏时会暂停它；最多缓存 6 个站点，超过后可能销毁隐藏项。不要把保留表单误认为远控连接一定会一直保持。长按站点标签可重新加载；结束远控后再切换或返回 Panel。

**如何验证**

Android 的 `BrowserMediaTest` 使用实际的 `siteView()` 工厂和权限处理方法，测试：

- 在 HTTPS WebView 内加载 CrossDesk 连接表单，并确认安全上下文、WebRTC 和 WebAssembly API 存在。
- 用本地生成的视频和静音音轨建立两个 PeerConnection，不调用摄像头或麦克风、不依赖外部信令；确认接收端自动播放、解码帧数、画面尺寸与像素，并保存平板截图。
- 麦克风授权只授予音频资源，摄像头单独请求被拒绝。麦克风用例在系统权限未授权时跳过，测试不更改系统权限。

从 `android` 目录运行 `./gradlew connectedDebugAndroidTest`；电脑需要 Android SDK、JDK 和已授权的测试设备。该命令通常会安装 debug APK，使用正式签名 APK 的设备请先确认签名兼容，不要卸载现有应用或清空数据。测试不修改站点列表或保存远程凭据。截图保存在目标应用的外部 files 目录，文件名为 `crossdesk-page.png` 和 `webrtc-receive.png`。

2026-11-24 在联想 TB-J716F（Android WebView 158.0.8127.2）上的实测结果：

- `crossDeskPageLoads` 通过：页面处于安全上下文，`RTCPeerConnection` 与 `WebAssembly` 可用，页面标题为「CrossDesk Web Client - 浏览器远程桌面客户端」。状态提示依次是「正在连接服务器...」→ 空（`web_client.js` 只在官方信令登录成功后清空该提示），说明网页到官方信令服务器的 WebSocket 与匿名登录在 WebView 内完成。测试不输入 ID 和密码；页面若从本机存储恢复了之前用过的设备 ID，只记录长度，不打印内容，也不提交表单。
- `remoteVideoAutoplaysWithoutCapturePermission` 通过：接收端 ICE 与连接状态均为 `connected`，视频轨与音频轨 `live`，`videoWidth/Height` 320×180，`paused=false`、`muted=false`（未静音自动播放），`framesDecoded` 达 8 以上，编码 `video/VP8`，从解码画面取样的像素为预期绿色。该 WebView 未触发 `requestVideoFrameCallback`，所以判定以 `getStats()` 的解码帧数为准。
- `microphoneRequestGrantsOnlyAudio` 与 `cameraOnlyRequestIsDenied` 通过：麦克风请求只授予音频资源，纯摄像头请求被拒绝。

平板锁屏时 `takeScreenshot` 只能拍到锁屏壁纸，因此这两个 PNG 不能作为画面证据；上述结论来自页面内的 DOM 状态、`getStats()` 计数和解码画面取样。

**测试范围**

本地 WebRTC 接收测试不等于 CrossDesk 端到端远控通过。真实验证还必须使用电脑的 9 位 ID + 密码，完成官方信令握手，检查持续桌面画面、声音和触摸操作，以及电脑端实际编码器与网页客户端版本的兼容性。不能仅凭网页打开成功、API 存在或自动播放配置，就声称远控已经可用。

2026-11-24 端到端实测（TB-J716F 的 PhoneBox 浏览器页）：在关掉「仅允许可信设备控制」后，从 CrossDesk 标签输入电脑端显示的 9 位 ID 与 6 位密码并点连接，平板显示整块 Windows 桌面，被控端日志出现 `Start screen capturer` 与 `System audio capture started`，不再有 `Reject controller`。此前同一步骤在平板 WebView、手机 Chrome 上都是黑屏，原因即上述可信设备开关。

实测中还看到这台平板的 WebView 存有 9 位设备 ID，页面加载后会自行发起一次连接会话（`connecting` 浮层短暂出现，曾出现「连接已断开，请检查网络后重试」）。那是网页自动续连上次设备，不是本次配置产生的问题。
