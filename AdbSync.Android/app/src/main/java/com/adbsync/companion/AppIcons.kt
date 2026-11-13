package com.adbsync.companion

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import java.util.concurrent.Executors

/** Job app icons from the server (/jobs/{id}/app-icon), cached for the life of the process. */
object AppIcons {
    private val bitmaps = HashMap<String, Bitmap>()
    private val missed = HashSet<String>()
    private val loading = HashSet<String>()
    private val pool = Executors.newFixedThreadPool(2) { Thread(it, "app-icons").apply { isDaemon = true } }

    @Synchronized
    fun cached(pkg: String): Bitmap? = bitmaps[pkg]

    /** Fetches the icon once per package; [done] runs (off the main thread) only when a new icon arrived. */
    fun request(pkg: String, fetch: () -> ByteArray?, done: () -> Unit) {
        synchronized(this) {
            if (pkg in bitmaps || pkg in missed || !loading.add(pkg)) return
        }
        pool.execute {
            val bitmap = try {
                fetch()?.let { BitmapFactory.decodeByteArray(it, 0, it.size) }
            } catch (_: Exception) {
                synchronized(this) { loading.remove(pkg) }
                return@execute
            }
            synchronized(this) {
                loading.remove(pkg)
                if (bitmap != null) bitmaps[pkg] = bitmap else missed.add(pkg)
            }
            if (bitmap != null) done()
        }
    }
}
