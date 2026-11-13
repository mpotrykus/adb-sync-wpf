package com.adbsync.companion

import android.accessibilityservice.AccessibilityService
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.PixelFormat
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.Settings
import android.util.Log
import android.view.Gravity
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import android.view.inputmethod.InputMethodManager
import android.widget.FrameLayout
import android.widget.LinearLayout
import com.adbsync.companion.Ui.ButtonKind
import com.adbsync.companion.Ui.body
import com.adbsync.companion.Ui.button
import com.adbsync.companion.Ui.dp
import com.adbsync.companion.Ui.indeterminateBar
import com.adbsync.companion.Ui.sectionLabel
import java.util.concurrent.Executors

/** Turning the gate on means enabling [AppGateService]; WRITE_SECURE_SETTINGS (granted by AdbSync over adb) lets us do that without the Settings UI. */
object AppGate {
    private const val SEPARATOR = ':'

    private fun component(context: Context) = ComponentName(context, AppGateService::class.java)

    private fun enabledServices(context: Context): List<String> =
        Settings.Secure.getString(context.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES)
            ?.split(SEPARATOR)?.filter { it.isNotEmpty() }.orEmpty()

    fun isEnabled(context: Context): Boolean {
        val own = component(context)
        return enabledServices(context).any { ComponentName.unflattenFromString(it) == own }
    }

    fun canEnableDirectly(context: Context) = WirelessDebugging.canWrite(context)

    fun enable(context: Context): Boolean {
        if (isEnabled(context)) return true
        return try {
            val services = enabledServices(context) + component(context).flattenToString()
            Settings.Secure.putString(
                context.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES, services.joinToString(SEPARATOR.toString()),
            ) && Settings.Secure.putInt(context.contentResolver, Settings.Secure.ACCESSIBILITY_ENABLED, 1)
        } catch (e: SecurityException) {
            Log.w("AdbSyncGate", "Not allowed to enable the app gate", e)
            false
        }
    }
}

/**
 * Watches for a job's linked app coming to the foreground on this phone. If the PC says this phone is behind the
 * job's other devices, puts a prompt over the app: sync first (the PC closes the app, syncs, and we reopen it) or
 * open anyway. Android can't stop another app from opening, so this is a warning over it, not a block.
 */
class AppGateService : AccessibilityService() {
    private val main = Handler(Looper.getMainLooper())
    private val io = Executors.newSingleThreadExecutor()

    private var watched: Set<String> = emptySet()
    private var watchedAt = 0L
    private var foreground: String? = null
    private var overlay: GateOverlay? = null
    private var ignoredPackages: Set<String> = emptySet()

    override fun onServiceConnected() {
        super.onServiceConnected()
        ignoredPackages = setOf("android", "com.android.systemui", packageName) + inputMethodPackages()
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent) {
        if (event.eventType != AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED) return
        val pkg = event.packageName?.toString() ?: return
        Log.i(TAG, "window event pkg=$pkg class=${event.className} foreground=$foreground overlay=${overlay != null}")
        if (pkg in ignoredPackages || pkg == foreground) return
        // The launcher emits a bare FrameLayout window event for its launch transition right after an app opens;
        // real screens report an activity class, so anything under android.* isn't a navigation.
        if (event.className?.startsWith("android.") == true) return

        foreground = pkg
        overlay?.takeIf { it.dismissibleByNavigation }?.let {
            Log.i(TAG, "dismissing overlay because $pkg came to the front")
            it.dismiss()
        }
        onForeground(pkg)
    }

    override fun onInterrupt() = Unit

    override fun onUnbind(intent: Intent?): Boolean {
        overlay?.dismiss()
        return super.onUnbind(intent)
    }

    override fun onDestroy() {
        io.shutdownNow()
        super.onDestroy()
    }

    private fun onForeground(pkg: String) {
        val now = SystemClock.elapsedRealtime()
        if (now - watchedAt < WATCH_CACHE_MS && pkg !in watched) return
        val config = PcConfig.load(this) ?: return
        val device = config.deviceName ?: return

        io.execute {
            val jobs = try {
                PcClient(config).status()
            } catch (e: Exception) {
                Log.d(TAG, "Couldn't check $pkg against the PC: ${e.message}")
                main.post { watchedAt = SystemClock.elapsedRealtime() }
                return@execute
            }
            main.post {
                watched = jobs.mapNotNull { it.appPackage }.toSet()
                watchedAt = SystemClock.elapsedRealtime()
                if (foreground != pkg || overlay != null) return@post

                val job = jobs.firstOrNull { it.appPackage == pkg && (it.staleDevices.any { (d, _) -> d == device } || it.isSyncingOn(device)) }
                    ?: return@post
                val reason = job.staleDevices.firstOrNull { (d, _) -> d == device }?.second
                GateOverlay(pkg, job.name, reason, config, device).show()
            }
        }
    }

    private fun inputMethodPackages(): Set<String> =
        getSystemService(InputMethodManager::class.java).inputMethodList.map { it.packageName }.toSet()

    private fun appLabel(pkg: String): String = try {
        packageManager.getApplicationLabel(packageManager.getApplicationInfo(pkg, 0)).toString()
    } catch (_: PackageManager.NameNotFoundException) {
        pkg
    }

    private inner class GateOverlay(
        private val pkg: String,
        private val job: String,
        private var reason: String?,
        private val config: PcConfig,
        private val device: String,
    ) {
        private val windowManager = getSystemService(WindowManager::class.java)
        private val content = LinearLayout(this@AppGateService).apply { orientation = LinearLayout.VERTICAL }
        private val root = FrameLayout(this@AppGateService).apply {
            setBackgroundColor(0xE60D0B12.toInt())
            isClickable = true
            addView(content.apply {
                background = Ui.rounded(Ui.SURFACE_ALT, dp(12))
                setPadding(dp(20), dp(18), dp(20), dp(18))
            }, FrameLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT, Gravity.CENTER).apply {
                marginStart = dp(20)
                marginEnd = dp(20)
            })
        }
        private val label = appLabel(pkg)
        private var shown = false
        private var closed = false

        /** False while a sync is running: closing the app drops the phone to the launcher, which must not dismiss us. */
        var dismissibleByNavigation = true
            private set

        fun show() {
            overlay = this
            if (reason == null) {
                renderSyncing()
                pollUntilSyncEnds()
            } else {
                renderPrompt()
            }
            // Covers the whole screen, system bars included: a gap anywhere would let touches reach the app.
            val params = WindowManager.LayoutParams(
                MATCH_PARENT, MATCH_PARENT, WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY,
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN,
                PixelFormat.TRANSLUCENT,
            )
            try {
                windowManager.addView(root, params)
                shown = true
            } catch (e: Exception) {
                Log.w(TAG, "Couldn't show the gate over $pkg", e)
                overlay = null
            }
        }

        fun dismiss() {
            if (closed) return
            closed = true
            if (shown) runCatching { windowManager.removeView(root) }
            if (overlay === this) overlay = null
        }

        private fun render(vararg views: View) {
            content.removeAllViews()
            views.forEach(content::addView)
        }

        private fun spacer(heightDp: Int = 12) = View(this@AppGateService).apply {
            layoutParams = LinearLayout.LayoutParams(MATCH_PARENT, dp(heightDp))
        }

        private fun renderPrompt() {
            dismissibleByNavigation = true
            render(
                sectionLabel("$label is out of sync"),
                spacer(8),
                body("${reason.orEmpty().replaceFirstChar { it.uppercase() }}. Opening it now and saving can overwrite changes from your other devices.", size = 14f),
                spacer(16),
                button("Sync first", ButtonKind.Primary, fill = true) { startSync() },
                spacer(8),
                button("Open anyway", ButtonKind.Neutral, fill = true) { openAnyway() },
                spacer(8),
                button("Close App", ButtonKind.Neutral, fill = true) { leave() },
            )
        }

        private fun renderSyncing() {
            dismissibleByNavigation = true
            render(
                sectionLabel("$job is syncing"),
                spacer(8),
                body("A sync is running. Opening $label now and saving can conflict with it. This clears when the sync finishes.", size = 14f),
                spacer(12),
                indeterminateBar(),
                spacer(16),
                button("Open anyway", ButtonKind.Neutral, fill = true) { openAnyway() },
                spacer(8),
                button("Close App", ButtonKind.Neutral, fill = true) { leave() },
            )
        }

        private fun pollUntilSyncEnds() {
            main.postDelayed({
                if (closed) return@postDelayed
                io.execute {
                    val status = runCatching { PcClient(config).status().firstOrNull { it.name == job } }.getOrNull()
                    main.post {
                        if (closed) return@post
                        when {
                            status == null || status.isSyncingOn(device) -> pollUntilSyncEnds()
                            else -> {
                                val stale = status.staleDevices.firstOrNull { (d, _) -> d == device }?.second
                                if (stale == null) {
                                    dismiss()
                                } else {
                                    reason = stale
                                    renderPrompt()
                                }
                            }
                        }
                    }
                }
            }, SYNC_POLL_MS)
        }

        private fun renderProblem(message: String) {
            dismissibleByNavigation = true
            render(
                sectionLabel("$label is still out of sync"),
                spacer(8),
                body(message, Ui.WARNING),
                spacer(16),
                button("Try again", ButtonKind.Primary, fill = true) { startSync() },
                spacer(8),
                button("Open anyway", ButtonKind.Neutral, fill = true) { openAnyway() },
                spacer(8),
                button("Close App", ButtonKind.Neutral, fill = true) { leave() },
            )
        }

        private fun startSync() {
            dismissibleByNavigation = false
            render(
                sectionLabel("Syncing $job"),
                spacer(8),
                body("Closing $label on this device and syncing with the PC. It reopens when the sync is done.", Ui.TEXT_SECONDARY),
                indeterminateBar(),
            )
            io.execute {
                val outcome = try {
                    Result.success(PcClient(config).closeAndSync(job, device))
                } catch (e: Exception) {
                    Result.failure(e)
                }
                main.post { if (!closed) onSyncFinished(outcome) }
            }
        }

        private fun onSyncFinished(outcome: Result<CloseAndSyncResult>) {
            val result = outcome.getOrNull()
            when {
                result != null && !result.failed && !result.stillOutOfSync -> {
                    dismiss()
                    launch()
                }
                result != null -> renderProblem(result.reason?.replaceFirstChar { it.uppercase() } ?: result.result)
                (outcome.exceptionOrNull() as? HttpStatusException)?.code == 409 ->
                    renderProblem("A sync of this job is already running. Try again in a moment.")
                else -> renderProblem("Couldn't reach AdbSync on the PC to sync. Is it running and on the same network?")
            }
        }

        private fun openAnyway() {
            dismiss()
            launch()
        }

        private fun leave() {
            dismiss()
            performGlobalAction(GLOBAL_ACTION_HOME)
        }

        private fun launch() {
            if (foreground == pkg) return
            packageManager.getLaunchIntentForPackage(pkg)?.let {
                runCatching { startActivity(it.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
                    .onFailure { e -> Log.w(TAG, "Couldn't reopen $pkg", e) }
            }
        }
    }

    private companion object {
        const val TAG = "AdbSyncGate"
        const val WATCH_CACHE_MS = 60_000L
        const val SYNC_POLL_MS = 3_000L
    }
}
