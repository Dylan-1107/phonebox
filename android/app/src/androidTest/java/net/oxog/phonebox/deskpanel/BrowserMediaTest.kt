package net.oxog.phonebox.deskpanel

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.net.Uri
import android.os.Build
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
