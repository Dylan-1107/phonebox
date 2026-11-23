package net.oxog.phonebox.deskpanel

import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.drawable.Drawable
import android.webkit.WebResourceResponse
import androidx.webkit.WebViewAssetLoader
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileInputStream

/**
 * The apps installed on this tablet — the All apps (this phone) widget.
 *
 * The panel is a web page, so the list comes through the bridge and every icon is written once as a PNG in
 * cacheDir/phoneicons. WebViewAssetLoader serves those files at https://appassets.androidplatform.net/phoneicons/
 * (see MainActivity.assetLoader), which keeps the page free of base64 blobs and lets <img loading="lazy"> work.
 */
class PhoneApps(private val context: Context) {

    private val pm get() = context.packageManager
    private val iconDir get() = File(context.cacheDir, "phoneicons").apply { mkdirs() }

    /** Launchable apps on this tablet, by name; writes the icons it is missing. */
    fun list(): JSONArray {
        val intent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
        @Suppress("DEPRECATION")
        val infos = pm.queryIntentActivities(intent, 0)
        val byPackage = LinkedHashMap<String, Pair<String, Int>>() // package -> label, best priority seen
        for (info in infos) {
            val pkg = info.activityInfo?.packageName ?: continue
            if (pkg == context.packageName) continue // this app is how you get back to the panel, not an app to open
            val label = runCatching { info.loadLabel(pm)?.toString()?.trim() }.getOrNull().orEmpty().ifEmpty { pkg }
            val rank = if (info.activityInfo.exported) 0 else 1 // the launcher-visible activity wins over an exported one
            val seen = byPackage[pkg]
            if (seen == null || rank < seen.second) byPackage[pkg] = label to rank
        }
        val apps = byPackage.map { (pkg, v) -> JSONObject().put("id", pkg).put("name", v.first) }
            .sortedWith { a, b -> a.getString("name").compareTo(b.getString("name"), ignoreCase = true) }
        writeIcons(byPackage.keys)
        return JSONArray(apps)
    }

    /** True when the app was opened. Only a launchable package name is accepted, never a component or an intent. */
    fun launch(pkg: String): Boolean {
        if (!PACKAGE_REGEX.matches(pkg)) return false
        val intent = pm.getLaunchIntentForPackage(pkg) ?: return false
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        return try { context.startActivity(intent); true } catch (_: Exception) { false }
    }

    /** PNGs for the icons that are not cached yet (a second of work at most for a few dozen apps). */
    private fun writeIcons(packages: Collection<String>) {
        val pending = LinkedHashMap<String, Drawable>()
        for (pkg in packages) {
            if (File(iconDir, "$pkg.png").exists()) continue
            @Suppress("DEPRECATION")
            val drawable = runCatching { pm.getApplicationIcon(pkg) }.getOrNull() ?: continue
            pending[pkg] = drawable
        }
        // drawing is cheap; list() itself runs off the UI thread (it is called from a bridge method)
        for ((pkg, drawable) in pending) {
            runCatching {
                val png = renderPng(drawable) ?: return@runCatching
                File(iconDir, "$pkg.png").writeBytes(png)
            }
        }
    }

    private fun renderPng(drawable: Drawable): ByteArray? {
        val size = ICON_PX
        val bitmap = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(bitmap)
        // keep the icon's proportions inside the square
        val w = drawable.intrinsicWidth.takeIf { it > 0 } ?: size
        val h = drawable.intrinsicHeight.takeIf { it > 0 } ?: size
        val scale = minOf(size.toFloat() / w, size.toFloat() / h)
        val dw = (w * scale).toInt().coerceAtLeast(1)
        val dh = (h * scale).toInt().coerceAtLeast(1)
        drawable.setBounds((size - dw) / 2, (size - dh) / 2, (size - dw) / 2 + dw, (size - dh) / 2 + dh)
        drawable.draw(canvas)
        val out = java.io.ByteArrayOutputStream()
        val ok = bitmap.compress(Bitmap.CompressFormat.PNG, 100, out)
        bitmap.recycle()
        return if (ok) out.toByteArray() else null
    }

    /** Serves /phoneicons/<package>.png from the cache directory. */
    class IconHandler(private val dir: File) : WebViewAssetLoader.PathHandler {
        override fun handle(path: String): WebResourceResponse? {
            val name = path.removePrefix("/")
            if (!name.endsWith(".png")) return null
            val pkg = name.removeSuffix(".png")
            if (!PACKAGE_REGEX.matches(pkg)) return null
            val file = File(dir, "$pkg.png")
            if (!file.isFile) return null
            return try {
                WebResourceResponse("image/png", null, FileInputStream(file))
            } catch (_: Exception) {
                null
            }
        }
    }

    companion object {
        private const val ICON_PX = 96
        private val PACKAGE_REGEX = Regex("[A-Za-z0-9_]+(\\.[A-Za-z0-9_]+)+")
        fun iconDir(context: Context): File = File(context.cacheDir, "phoneicons").apply { mkdirs() }
    }
}
