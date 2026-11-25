package net.oxog.phonebox.deskpanel

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.net.Uri
import android.os.Build
import android.os.SystemClock
import android.view.InputDevice
import android.view.MotionEvent
import android.util.Log
import android.view.View
import android.view.WindowManager
import android.webkit.PermissionRequest
import android.webkit.WebView
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONObject
import org.json.JSONTokener
import org.junit.After
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Test
import java.io.File
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/** Uses the actual site's WebView factory, without editing the user's pages or supplying remote credentials. */
class BrowserMediaTest {
    private val instrumentation = InstrumentationRegistry.getInstrumentation()
    private lateinit var activity: Activity
    private lateinit var view: WebView

    @Before fun setUp() {
        activity = instrumentation.startActivitySync(Intent(instrumentation.targetContext, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        instrumentation.runOnMainSync {
            val factory = activity.javaClass.getDeclaredMethod("siteView").apply { isAccessible = true }
            view = factory.invoke(activity) as WebView
            val size = activity.resources.displayMetrics
            view.layoutParams.width = size.widthPixels
            view.layoutParams.height = size.heightPixels
            view.visibility = View.VISIBLE
            view.onResume()
            activity.window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON or
                WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)
        }
    }

    @After fun tearDown() {
        instrumentation.runOnMainSync {
            (view.parent as? android.view.ViewGroup)?.removeView(view)
            val active = activity.javaClass.getDeclaredField("pageWebView").apply { isAccessible = true }
            if (active.get(activity) === view) active.set(activity, null)
            view.destroy()
        }
        // The panel's normal resume path restores its audio mode and display behavior.
        activity.finish()
    }

    @Test fun crossDeskPageLoads() {
        instrumentation.runOnMainSync { view.loadUrl("https://web.crossdesk.cn") }
        val report = awaitReport("""
            JSON.stringify({state: document.readyState === 'complete' &&
              document.querySelector('input') && document.body.innerText.includes('CrossDesk') ? 'passed' : 'running',
              secure: isSecureContext, rtc: typeof RTCPeerConnection === 'function',
              wasm: typeof WebAssembly === 'object', title: document.title,
              status: document.querySelector('[role=status]')?.textContent || ''})
        """.trimIndent(), 45)
        Log.i("PhoneBoxBrowserTest", "CrossDesk page: $report")
        assertTrue(report.getBoolean("secure"))
        assertTrue(report.getBoolean("rtc"))
        assertTrue(report.getBoolean("wasm"))
        // web_client.js keeps the Connect button disabled while signaling is down, and clears its feedback line once the
        // official signaling websocket accepts the anonymous login. An empty feedback therefore proves the handshake
        // worked from this WebView. The form may hold a device ID saved by earlier use, so its lengths are logged but
        // never printed, and the test neither submits the form nor reads or clears that saved state.
        val statuses = LinkedHashSet<String>()
        val timeline = ArrayList<String>()
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(25)
        var loggedIn = false
        while (System.nanoTime() < deadline) {
            val value = JSONTokener(evaluate("""
                JSON.stringify({feedback: document.getElementById('connection-feedback')?.textContent || '',
                  retry: document.getElementById('retry-signaling')?.style.display || '',
                  connecting: document.getElementById('connecting-overlay')?.style.display || '',
                  connectingText: document.getElementById('connecting-message')?.textContent || '',
                  readyState: document.getElementById('video')?.readyState ?? -1,
                  idLength: document.getElementById('transmission-id')?.value?.length || 0})
            """.trimIndent())).nextValue() as String
            val state = JSONObject(value)
            statuses.add(state.optString("feedback"))
            // Empty feedback is what web_client.js leaves after the signaling login; it proves the handshake.
            if (state.optString("feedback").isEmpty()) loggedIn = true
            val line = state.toString()
            if (timeline.isEmpty() || timeline.last() != line) timeline.add(line)
            Thread.sleep(1000)
        }
        Log.i("PhoneBoxBrowserTest", "CrossDesk timeline: $timeline")
        screenshot("crossdesk-page.png")
        assertTrue("Signaling never reached the logged-in state; statuses: $statuses", loggedIn)
    }

    @Test fun remoteVideoAutoplaysWithoutCapturePermission() {
        // WebView settings are a WebView method too: they may only be read on the view's own thread.
        var js = false
        var storage = false
        var gesture = true
        instrumentation.runOnMainSync {
            js = view.settings.javaScriptEnabled
            storage = view.settings.domStorageEnabled
            gesture = view.settings.mediaPlaybackRequiresUserGesture
        }
        assertTrue(js)
        assertTrue(storage)
        assertFalse(gesture)
        val html = instrumentation.context.assets.open("webrtc-receive.html").bufferedReader().use { it.readText() }
        instrumentation.runOnMainSync { view.loadDataWithBaseURL("https://web.crossdesk.cn/", html, "text/html", "UTF-8", null) }
        val report = awaitReport("JSON.stringify(window.result || {state: 'running'})", 30)
        Log.i("PhoneBoxBrowserTest", "WebRTC receive: $report")
        assertEquals("passed", report.getString("state"))
        screenshot("webrtc-receive.png")
        evaluate("window.cleanup && window.cleanup(); true")
    }

    @Test fun microphoneRequestGrantsOnlyAudio() {
        assumeTrue("Do not alter Android microphone permission during this test",
            Build.VERSION.SDK_INT < Build.VERSION_CODES.M ||
                activity.checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED)
        val request = CaptureRequest(arrayOf(PermissionRequest.RESOURCE_AUDIO_CAPTURE, PermissionRequest.RESOURCE_VIDEO_CAPTURE))
        grant(request)
        assertArrayEquals(arrayOf(PermissionRequest.RESOURCE_AUDIO_CAPTURE), request.granted)
        assertFalse(request.denied)
    }

    @Test fun cameraOnlyRequestIsDenied() {
        val request = CaptureRequest(arrayOf(PermissionRequest.RESOURCE_VIDEO_CAPTURE))
        grant(request)
        assertTrue(request.denied)
        assertNull(request.granted)
    }

    // 仅做本地检查，不更改已安装的页面、凭据或站点内容。
    @Test fun dockCutoutTracksOverlapAfterSiteScrollAndClears() {
        instrumentation.runOnMainSync {
            panelView().visibility = View.INVISIBLE
            (view.parent as View).setBackgroundColor(Color.GREEN)
            view.setBackgroundColor(Color.BLUE)
            view.loadDataWithBaseURL("https://phonebox.test/", "<meta name=viewport content='width=device-width'><body style='margin:0;background:blue;height:4000px'>", "text/html", "UTF-8", null)
        }
        awaitReport("JSON.stringify({state: document.readyState === 'complete' ? 'passed' : 'running'})", 10)
        instrumentation.waitForIdleSync()
        instrumentation.runOnMainSync {
            val root = view.parent as android.view.ViewGroup
            // 让站点保持内缩，以确保裁剪区域必须从根视图坐标转换为站点坐标。
            view.layout(20, 30, root.width - 20, root.height - 30)
            val x = view.left + view.width / 2
            val y = view.bottom - 25
            val bounds = listOf(view.left, view.top, view.right, view.bottom)
            dock(x - 30f, y - 10f, 60f, 20f)
            fun pixels() {
                val bitmap = Bitmap.createBitmap(root.width, root.height, Bitmap.Config.ARGB_8888)
                root.draw(Canvas(bitmap))
                assertEquals("Only the overlap reveals the underlying panel surface", Color.GREEN, bitmap.getPixel(x, y))
                assertEquals("No full-width bottom strip", Color.BLUE, bitmap.getPixel(x - 60, y))
                assertEquals("Site still fills the body below the dock", Color.BLUE, bitmap.getPixel(x, y + 15))
                bitmap.recycle()
            }
            pixels()
            view.scrollTo(0, 120)
            assertTrue("The draw check must include a scrolled WebView", view.scrollY > 0)
            pixels()
            assertEquals(bounds, listOf(view.left, view.top, view.right, view.bottom))
            dock(0f, 0f, 0f, 0f)
            val bitmap = Bitmap.createBitmap(root.width, root.height, Bitmap.Config.ARGB_8888)
            root.draw(Canvas(bitmap))
            assertEquals(Color.BLUE, bitmap.getPixel(x, y))
            bitmap.recycle()
        }
    }

    @Test fun dockOwnsOnlyGesturesStartingInOverlapIncludingPrimaryMouse() {
        instrumentation.runOnMainSync {
            val panel = panelView()
            val panelActions = ArrayList<Int>()
            val siteActions = ArrayList<Int>()
            val tools = ArrayList<Int>()
            panel.setOnTouchListener { _, e -> panelActions.add(e.actionMasked); tools.add(e.getToolType(0)); true }
            view.setOnTouchListener { _, e -> siteActions.add(e.actionMasked); true }
            activity.javaClass.getDeclaredField("pageWebView").apply { isAccessible = true }.set(activity, view)
            val x = view.width / 2f
            val y = view.height / 2f
            val location = IntArray(2)
            (view.parent as View).getLocationInWindow(location)
            fun send(action: Int, px: Float, mouse: Boolean = false) {
                val props = arrayOf(MotionEvent.PointerProperties().apply { id = 0; toolType = if (mouse) MotionEvent.TOOL_TYPE_MOUSE else MotionEvent.TOOL_TYPE_FINGER })
                val coords = arrayOf(MotionEvent.PointerCoords().apply { this.x = px + location[0]; this.y = y + location[1]; pressure = 1f; size = 1f })
                val now = SystemClock.uptimeMillis()
                val event = MotionEvent.obtain(now, now, action, 1, props, coords, 0, if (mouse && action != MotionEvent.ACTION_UP) MotionEvent.BUTTON_PRIMARY else 0,
                    1f, 1f, 0, 0, if (mouse) InputDevice.SOURCE_MOUSE else InputDevice.SOURCE_TOUCHSCREEN, 0)
                try { activity.dispatchTouchEvent(event) } finally { event.recycle() }
            }
            val gesture = listOf(MotionEvent.ACTION_DOWN, MotionEvent.ACTION_MOVE, MotionEvent.ACTION_UP)
            dock(x - 10, y - 10, 20f, 20f)
            send(MotionEvent.ACTION_DOWN, x)
            dock(0f, 0f, 0f, 0f) // 关闭或移动 Dock 时不能丢弃已捕获的手势
            send(MotionEvent.ACTION_MOVE, x + 40)
            send(MotionEvent.ACTION_UP, x + 40)
            assertEquals(gesture, panelActions)
            assertTrue(siteActions.isEmpty())
            panelActions.clear(); tools.clear()
            dock(x - 10, y - 10, 20f, 20f)
            send(MotionEvent.ACTION_DOWN, x + 40) // 从站点开始的滚动即使移入 Dock，仍归站点处理
            send(MotionEvent.ACTION_MOVE, x)
            send(MotionEvent.ACTION_UP, x)
            assertEquals(gesture, siteActions)
            assertTrue(panelActions.isEmpty())
            send(MotionEvent.ACTION_DOWN, x, true)
            send(MotionEvent.ACTION_MOVE, x + 40, true)
            send(MotionEvent.ACTION_UP, x + 40, true)
            assertEquals(gesture, panelActions)
            assertEquals(List(3) { MotionEvent.TOOL_TYPE_FINGER }, tools)
            panel.setOnTouchListener(null); view.setOnTouchListener(null)
        }
    }

    private fun panelView(): WebView = activity.javaClass.getDeclaredField("web").apply { isAccessible = true }.get(activity) as WebView

    /** 测试坐标使用根视图的物理像素，而经过身份验证的桥接接口使用 CSS 像素。 */
    private fun dock(x: Float, y: Float, w: Float, h: Float) {
        val d = activity.resources.displayMetrics.density
        activity.javaClass.getDeclaredMethod("updatePageDock", Float::class.javaPrimitiveType, Float::class.javaPrimitiveType,
            Float::class.javaPrimitiveType, Float::class.javaPrimitiveType).apply { isAccessible = true }.invoke(activity, x / d, y / d, w / d, h / d)
    }

    private fun grant(request: PermissionRequest) = instrumentation.runOnMainSync {
        activity.javaClass.getDeclaredMethod("grantWebMic", PermissionRequest::class.java)
            .apply { isAccessible = true }.invoke(activity, request)
    }

    private fun evaluate(script: String): String {
        val ready = CountDownLatch(1)
        var result = "null"
        instrumentation.runOnMainSync { view.evaluateJavascript(script) { result = it; ready.countDown() } }
        check(ready.await(5, TimeUnit.SECONDS)) { "JavaScript callback timed out" }
        return result
    }

    private fun awaitReport(script: String, seconds: Long): JSONObject {
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(seconds)
        var report = JSONObject()
        while (System.nanoTime() < deadline) {
            val value = JSONTokener(evaluate(script)).nextValue()
            if (value is String) report = JSONObject(value)
            if (report.optString("state") == "passed") return report
            check(report.optString("state") != "failed") { report.toString() }
            Thread.sleep(200)
        }
        error("Browser check timed out: $report")
    }

    private fun screenshot(name: String) {
        val bitmap = instrumentation.uiAutomation.takeScreenshot() ?: error("Screenshot unavailable")
        File(instrumentation.targetContext.getExternalFilesDir(null), name).outputStream().use {
            bitmap.compress(Bitmap.CompressFormat.PNG, 100, it)
        }
        bitmap.recycle()
    }

    private class CaptureRequest(private val requested: Array<String>) : PermissionRequest() {
        var granted: Array<String>? = null
        var denied = false
        override fun getOrigin(): Uri = Uri.parse("https://web.crossdesk.cn")
        override fun getResources(): Array<String> = requested
        override fun grant(resources: Array<String>) { granted = resources }
        override fun deny() { denied = true }
    }
}
