package com.adbsync.companion

import android.app.AlertDialog
import android.content.Context
import android.text.format.DateUtils
import android.text.format.Formatter
import android.view.View
import android.widget.Toast
import com.adbsync.companion.Ui.styled
import java.time.Duration
import java.time.OffsetDateTime

/** One tab of the main window. */
abstract class Screen(protected val host: MainActivity) {
    abstract val view: View
    protected var visible = false
        private set

    fun show() {
        visible = true
        onShow()
    }

    fun hide() {
        visible = false
        onHide()
    }

    protected open fun onShow() {}
    protected open fun onHide() {}

    protected fun <T> async(work: (PcClient) -> T, done: (Result<T>) -> Unit) = host.async(work, done)

    protected fun toast(message: String) = host.toast(message)

    protected fun dp(value: Int) = with(Ui) { host.dp(value) }
    protected fun title(text: String) = with(Ui) { host.title(text) }
    protected fun sectionLabel(text: String) = with(Ui) { host.sectionLabel(text) }
    protected fun subSectionLabel(text: String) = with(Ui) { host.subSectionLabel(text) }
    protected fun body(text: CharSequence = "", color: Int = Ui.TEXT_PRIMARY, size: Float = 14f) = with(Ui) { host.body(text, color, size) }
    protected fun caption(text: CharSequence = "") = with(Ui) { host.caption(text) }
    protected fun card(accent: Int? = null, glow: Boolean = false) = with(Ui) { host.card(accent, glow) }
    protected fun chip(text: String, color: Int, onClick: (() -> Unit)? = null) = with(Ui) { host.chip(text, color, onClick) }
    protected fun statusLine(text: CharSequence, color: Int, textColor: Int = color) = with(Ui) { host.statusLine(text, color, textColor) }
    protected fun indeterminateBar() = with(Ui) { host.indeterminateBar() }
    protected fun divider() = with(Ui) { host.divider() }
    protected fun spacer(heightDp: Int) = with(Ui) { host.spacer(heightDp) }
    protected fun button(text: String, kind: Ui.ButtonKind = Ui.ButtonKind.Neutral, fill: Boolean = false, onClick: () -> Unit) =
        with(Ui) { host.button(text, kind, fill, null, onClick) }

    protected fun pageHead(title: String, vararg actions: View) = with(Ui) { host.pageHead(title, *actions) }
    protected fun iconButton(icon: Int, description: String, filled: Boolean = false, tint: Int = Ui.TEXT_PRIMARY, onClick: (View) -> Unit) =
        with(Ui) { host.iconButton(icon, description, filled, tint, onClick) }

    protected fun confirm(title: String, message: String, action: String, danger: Boolean = false, onConfirm: () -> Unit) =
        Dialogs.confirm(host, title, message, action, danger, onConfirm)
}

object Dialogs {
    fun confirm(context: Context, title: String, message: String, action: String, danger: Boolean = false, onConfirm: () -> Unit) {
        AlertDialog.Builder(context)
            .setTitle(title)
            .setMessage(message)
            .setPositiveButton(action) { _, _ -> onConfirm() }
            .setNegativeButton("Cancel", null)
            .create()
            .styled()
            .apply {
                if (danger) setOnShowListener {
                    window?.setBackgroundDrawable(Ui.glass(Ui.SURFACE_ALT, Ui.SURFACE_ALT_BORDER, (8 * context.resources.displayMetrics.density).toInt(), 1))
                    getButton(AlertDialog.BUTTON_POSITIVE)?.setTextColor(Ui.DANGER)
                    getButton(AlertDialog.BUTTON_NEGATIVE)?.setTextColor(Ui.TEXT_SECONDARY)
                }
            }
            .show()
    }
}

object Fmt {
    fun ago(context: Context, at: OffsetDateTime): String =
        DateUtils.getRelativeTimeSpanString(at.toInstant().toEpochMilli(), System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS).toString()

    fun bytes(context: Context, count: Long): String = Formatter.formatShortFileSize(context, count)

    fun duration(from: OffsetDateTime, to: OffsetDateTime): String {
        val seconds = Duration.between(from, to).seconds.coerceAtLeast(0)
        return when {
            seconds >= 3600 -> "${seconds / 3600}h ${seconds % 3600 / 60}m"
            seconds >= 60 -> "${seconds / 60}m ${seconds % 60}s"
            else -> "${seconds}s"
        }
    }

    fun words(camel: String): String =
        camel.replace(Regex("([a-z])([A-Z])"), "$1 $2").lowercase().replaceFirstChar { it.uppercaseChar() }
}

class NotConnectedException : Exception("Not connected to a server yet")

/** Runs server calls off the main thread for any activity. */
object Backend {
    private val pool = java.util.concurrent.Executors.newCachedThreadPool()
    private val main = android.os.Handler(android.os.Looper.getMainLooper())

    fun <T> async(activity: android.app.Activity, work: (PcClient) -> T, done: (Result<T>) -> Unit) {
        val config = PcConfig.load(activity)
        if (config == null) {
            done(Result.failure(NotConnectedException()))
            return
        }
        pool.execute {
            val result = runCatching { work(PcClient(config)) }
            main.post { if (!activity.isDestroyed) done(result) }
        }
    }
}

fun errorText(error: Throwable): String = when (error) {
    is NotConnectedException -> error.message ?: "Not connected"
    is ApiException -> error.message ?: "HTTP ${error.code}"
    is HttpStatusException -> if (error.code == 401) "The server rejected this phone's token" else "The server answered HTTP ${error.code}"
    else -> "Couldn't reach the server"
}

fun Context.toastShort(message: String) = Toast.makeText(this, message, Toast.LENGTH_SHORT).show()
