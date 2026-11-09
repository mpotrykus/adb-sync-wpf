package com.adbsync.companion

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.SharedPreferences
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.provider.Settings
import android.view.Gravity
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast
import com.adbsync.companion.Ui.ButtonKind
import com.adbsync.companion.Ui.body
import com.adbsync.companion.Ui.button
import com.adbsync.companion.Ui.caption
import com.adbsync.companion.Ui.card
import com.adbsync.companion.Ui.divider
import com.adbsync.companion.Ui.dp
import com.adbsync.companion.Ui.indeterminateBar
import com.adbsync.companion.Ui.sectionLabel
import com.adbsync.companion.Ui.statusLine
import com.adbsync.companion.Ui.styled
import com.adbsync.companion.Ui.subSectionLabel
import com.adbsync.companion.Ui.title
import com.adbsync.companion.Ui.toggle
import java.net.HttpURLConnection
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

class MainActivity : Activity() {
    private val handler = Handler(Looper.getMainLooper())
    private val io = Executors.newSingleThreadExecutor()

    private lateinit var pcStatus: LinearLayout
    private lateinit var jobsContainer: LinearLayout
    private lateinit var setupCard: LinearLayout
    private lateinit var wifiDebugStatus: LinearLayout
    private lateinit var autoEnableSwitch: Switch
    private lateinit var serviceStatus: TextView

    private val jobs = linkedMapOf<String, RemoteJob>()
    private var feedActive: AtomicBoolean? = null
    @Volatile private var feedConn: HttpURLConnection? = null
    private var resumed = false

    private val prefsListener = SharedPreferences.OnSharedPreferenceChangeListener { _, _ ->
        if (resumed) startFeed()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        actionBar?.hide()
        window.setBackgroundDrawable(Ui.windowBackground())
        window.isNavigationBarContrastEnforced = false
        window.insetsController?.setSystemBarsAppearance(
            0, WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS or WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS,
        )

        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(20), dp(16), dp(24))
        }

        column.addView(title("AdbSync"))
        pcStatus = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dp(6), 0, 0)
        }
        column.addView(pcStatus)

        column.addView(subSectionLabel("Jobs"))
        jobsContainer = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        column.addView(jobsContainer)

        column.addView(subSectionLabel("Wireless debugging"))
        column.addView(card().apply {
            wifiDebugStatus = LinearLayout(this@MainActivity).apply { orientation = LinearLayout.VERTICAL }
            addView(wifiDebugStatus)
            autoEnableSwitch = toggle("Turn back on when Wi-Fi connects").apply {
                setOnCheckedChangeListener { _, checked ->
                    WirelessDebugging.setAutoEnable(this@MainActivity, checked)
                    if (checked) CompanionJobService.scheduleEnable(this@MainActivity)
                }
            }
            addView(divider())
            addView(autoEnableSwitch)
            addView(divider())
            addView(button("Pair with PC over Wi-Fi", fill = true) { startPairing() })
        })

        column.addView(subSectionLabel("Setup"))
        setupCard = card()
        column.addView(setupCard)

        column.addView(subSectionLabel("Diagnostics"))
        column.addView(card().apply {
            serviceStatus = caption()
            addView(serviceStatus)
            addView(View(this@MainActivity).apply { minimumHeight = dp(10) })
            addView(button("Simulate a 10s sync", fill = true) { simulateSync() })
        })

        setContentView(ScrollView(this).apply {
            addView(column)
            isVerticalScrollBarEnabled = false
            setOnApplyWindowInsetsListener { view, insets ->
                val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
                view.setPadding(bars.left, bars.top, bars.right, bars.bottom)
                insets
            }
        })
    }

    override fun onResume() {
        super.onResume()
        resumed = true
        PcConfig.prefs(this).registerOnSharedPreferenceChangeListener(prefsListener)
        refreshSetup()
        startFeed()
        CompanionJobService.armWatch(this)
        CompanionJobService.scheduleEnable(this)
    }

    override fun onPause() {
        resumed = false
        PcConfig.prefs(this).unregisterOnSharedPreferenceChangeListener(prefsListener)
        stopFeed()
        super.onPause()
    }

    override fun onDestroy() {
        io.shutdownNow()
        super.onDestroy()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        refreshSetup()
    }

    private fun setPcStatus(text: String, color: Int) {
        pcStatus.removeAllViews()
        pcStatus.addView(statusLine(text, color, if (color == Ui.SUCCESS) Ui.TEXT_SECONDARY else color))
    }

    private fun startFeed() {
        stopFeed()
        val config = PcConfig.load(this) ?: run {
            setPcStatus("Not set up yet. Connect this phone to AdbSync over adb (USB or wireless debugging); " +
                "it sends its address here within a few minutes.", Ui.WARNING)
            renderJobs()
            return
        }

        val active = AtomicBoolean(true)
        feedActive = active
        setPcStatus("Connecting to ${config.pcName}…", Ui.TEXT_SECONDARY)
        val client = PcClient(config)
        val listener = object : PcClient.FeedListener {
            override fun onConnected() = ui { if (active.get()) setPcStatus("Connected to ${config.pcName}", Ui.SUCCESS) }
            override fun onSnapshot(pc: String, jobs: List<RemoteJob>) = ui {
                if (!active.get()) return@ui
                this@MainActivity.jobs.clear()
                jobs.forEach { this@MainActivity.jobs[it.name] = it }
                setPcStatus("Connected to $pc", Ui.SUCCESS)
                renderJobs()
            }
            override fun onJob(job: RemoteJob) = ui {
                if (!active.get()) return@ui
                jobs[job.name] = job
                renderJobs()
            }
        }

        Thread {
            var backoffMs = 1_000L
            while (active.get()) {
                try {
                    client.streamEvents(listener, { feedConn = it }, { active.get() })
                    backoffMs = 1_000L
                } catch (e: HttpStatusException) {
                    ui {
                        if (!active.get()) return@ui
                        if (e.code == 401) {
                            setPcStatus("${config.pcName} rejected this phone's token. Connect over adb so AdbSync can resend it.", Ui.DANGER)
                        } else {
                            setPcStatus("${config.pcName} returned HTTP ${e.code}", Ui.DANGER)
                        }
                    }
                } catch (e: javax.net.ssl.SSLException) {
                    ui {
                        if (active.get()) setPcStatus("${config.pcName}'s certificate doesn't match the one this phone trusts. " +
                            "Connect over adb so AdbSync can resend it.", Ui.DANGER)
                    }
                } catch (e: Exception) {
                    ui {
                        if (active.get()) setPcStatus("Can't reach ${config.pcName}. Retrying…", Ui.WARNING)
                    }
                }
                if (!active.get()) break
                try {
                    Thread.sleep(backoffMs)
                } catch (_: InterruptedException) {
                    break
                }
                backoffMs = (backoffMs * 2).coerceAtMost(30_000L)
            }
        }.apply {
            isDaemon = true
            name = "pc-feed"
            start()
        }
    }

    private fun stopFeed() {
        feedActive?.set(false)
        feedActive = null
        val conn = feedConn
        feedConn = null
        if (conn != null) io.execute { conn.disconnect() }
    }

    private fun renderJobs() {
        jobsContainer.removeAllViews()
        if (jobs.isEmpty()) {
            jobsContainer.addView(card().apply {
                addView(caption(if (PcConfig.load(this@MainActivity) == null) "Jobs appear here once the PC is connected." else "No jobs configured on the PC."))
            })
            return
        }
        for (job in jobs.values) jobsContainer.addView(jobCard(job))
    }

    private fun jobCard(job: RemoteJob): View = card().apply {
        val header = LinearLayout(this@MainActivity).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        header.addView(sectionLabel(job.name).apply {
            if (!job.enabled) setTextColor(Ui.TEXT_SECONDARY)
            layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
        })
        if (!job.enabled) header.addView(caption("Disabled"))
        addView(header)
        addView(View(this@MainActivity).apply { minimumHeight = dp(6) })

        if (job.running) {
            addView(statusLine(job.statusText(), Ui.ACCENT, Ui.ACCENT))
            addView(indeterminateBar())
        } else {
            addView(statusLine("Idle", Ui.TEXT_SECONDARY, Ui.TEXT_SECONDARY))
        }
        job.lastResult?.let { result ->
            addView(body(result, Ui.resultColor(result, job.lastFailed), 13f).apply { setPadding(dp(15), dp(4), 0, 0) })
        }

        val buttons = LinearLayout(this@MainActivity).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(0, dp(12), 0, 0)
        }
        if (job.running) {
            buttons.addView(button("Stop", ButtonKind.Danger) { confirmStop(job) })
        } else {
            buttons.addView(button("Run now", ButtonKind.Success) { act(job.name, "Run") { it.run(job.name) } })
        }
        buttons.addView(button("Log") { showLog(job.name) })
        addView(buttons)
    }

    private fun confirmStop(job: RemoteJob) {
        if (!job.isPushing) {
            act(job.name, "Stop") { it.stop(job.name) }
            return
        }
        AlertDialog.Builder(this)
            .setTitle("Stop ${job.name}?")
            .setMessage("It is currently pushing to a device. Stopping now may leave some devices updated and others not until the job runs again.")
            .setPositiveButton("Stop job") { _, _ -> act(job.name, "Stop") { it.stop(job.name) } }
            .setNegativeButton("Cancel", null)
            .create()
            .apply {
                setOnShowListener {
                    window?.setBackgroundDrawable(Ui.rounded(Ui.SURFACE_ALT, dp(12)))
                    getButton(AlertDialog.BUTTON_POSITIVE)?.setTextColor(Ui.DANGER)
                    getButton(AlertDialog.BUTTON_NEGATIVE)?.setTextColor(Ui.TEXT_SECONDARY)
                }
            }
            .show()
    }

    private fun act(jobName: String, verb: String, call: (PcClient) -> Int) {
        val config = PcConfig.load(this) ?: return
        io.execute {
            val message = try {
                when (val code = call(PcClient(config))) {
                    in 200..299 -> null
                    401 -> "Token rejected"
                    404 -> "$jobName no longer exists on the PC"
                    409 -> "$jobName isn't running"
                    else -> "$verb failed (HTTP $code)"
                }
            } catch (e: Exception) {
                "$verb failed: ${e.javaClass.simpleName}"
            }
            if (message != null) ui { Toast.makeText(this, message, Toast.LENGTH_SHORT).show() }
        }
    }

    private fun showLog(jobName: String) {
        val config = PcConfig.load(this) ?: return
        io.execute {
            val text = try {
                PcClient(config).log(jobName)
            } catch (e: HttpStatusException) {
                if (e.code == 404) "No log yet." else "Couldn't load log (HTTP ${e.code})"
            } catch (e: Exception) {
                "Couldn't load log: ${e.javaClass.simpleName}"
            }
            ui {
                val view = TextView(this).apply {
                    this.text = text
                    textSize = 11f
                    typeface = Typeface.MONOSPACE
                    setTextColor(Ui.TEXT_PRIMARY)
                    setTextIsSelectable(true)
                    setPadding(dp(20), dp(4), dp(20), dp(8))
                }
                val scroll = ScrollView(this).apply { addView(view) }
                AlertDialog.Builder(this)
                    .setTitle(jobName)
                    .setView(scroll)
                    .setPositiveButton("Close", null)
                    .create()
                    .styled()
                    .show()
                scroll.post { scroll.fullScroll(View.FOCUS_DOWN) }
            }
        }
    }

    private fun refreshSetup() {
        setupCard.removeAllViews()
        var first = true
        fun item(ok: Boolean?, okText: String, problemText: String, actionText: String?, action: (() -> Unit)?) {
            if (!first) setupCard.addView(divider())
            first = false
            val color = when (ok) {
                true -> Ui.SUCCESS
                false -> Ui.DANGER
                null -> Ui.WARNING
            }
            setupCard.addView(statusLine(if (ok == true) okText else problemText, color, if (ok == true) Ui.TEXT_PRIMARY else color))
            if (ok != true && actionText != null && action != null) {
                setupCard.addView(View(this).apply { minimumHeight = dp(10) })
                setupCard.addView(button(actionText, ButtonKind.Primary, fill = true, onClick = action))
            }
        }

        item(
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED,
            "Notifications allowed",
            "Notifications are blocked, so sync progress won't show",
            "Allow notifications",
        ) { requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), REQUEST_NOTIFICATIONS) }

        item(
            getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(packageName),
            "Exempt from battery optimization",
            "Battery optimized: Android may refuse to keep Wi-Fi awake during syncs",
            "Exempt from battery optimization",
        ) { requestBatteryExemption() }

        val timeoutDisabled = readAdbAuthTimeoutDisabled()
        item(
            timeoutDisabled,
            "adb authorization timeout disabled",
            if (timeoutDisabled == false) {
                "adb authorization timeout is on: the PC's key is revoked after 7 days unused"
            } else {
                "Couldn't read the adb authorization timeout setting"
            },
            "Open developer options",
        ) { startActivity(Intent(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS)) }

        wifiDebugStatus.removeAllViews()
        val canWrite = WirelessDebugging.canWrite(this)
        val enabled = WirelessDebugging.isEnabled(this)
        wifiDebugStatus.addView(statusLine(
            if (enabled) "On" else "Off",
            if (enabled) Ui.SUCCESS else Ui.TEXT_SECONDARY,
            Ui.TEXT_PRIMARY,
        ))
        if (!canWrite) {
            wifiDebugStatus.addView(caption("AdbSync grants this app control of wireless debugging the next time it reaches the phone over adb.")
                .apply { setPadding(dp(15), dp(4), 0, 0) })
        }
        autoEnableSwitch.isChecked = WirelessDebugging.autoEnable(this)
        autoEnableSwitch.isEnabled = canWrite

        serviceStatus.text = if (SyncService.isRunning) "Sync service running, holding wake and Wi-Fi locks" else "Sync service idle"
    }

    private fun startPairing() {
        val config = PcConfig.load(this)
        if (config?.deviceName == null) {
            Toast.makeText(this, "Connect this phone to AdbSync over adb once first (USB works).", Toast.LENGTH_LONG).show()
            return
        }
        if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            Toast.makeText(this, "Allow notifications first; the code is entered in a notification.", Toast.LENGTH_LONG).show()
            return
        }
        Pairing.start(this)
    }

    private fun readAdbAuthTimeoutDisabled(): Boolean? = try {
        Settings.Global.getLong(contentResolver, "adb_allowed_connection_time", -1L)
            .takeIf { it >= 0 }
            ?.let { it == 0L }
    } catch (_: SecurityException) {
        null
    }

    @SuppressLint("BatteryLife")
    private fun requestBatteryExemption() {
        startActivity(
            Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName"))
        )
    }

    private fun simulateSync() {
        val job = "Simulated job"
        fun send(event: String, phase: String? = null, pushed: Boolean = false) {
            startForegroundService(
                SyncEvent(event, job, phase, "this device", null, pushed)
                    .writeTo(Intent(this, SyncService::class.java))
            )
            handler.postDelayed(::refreshSetup, 300)
        }
        send(Protocol.EVENT_PHASE, "Pull")
        handler.postDelayed({ send(Protocol.EVENT_PHASE, "Push") }, 5_000)
        handler.postDelayed({ send(Protocol.EVENT_COMPLETED, pushed = true) }, 10_000)
        handler.postDelayed(::refreshSetup, 21_000)
    }

    private fun ui(block: () -> Unit) {
        handler.post { if (!isDestroyed) block() }
    }

    private companion object {
        const val REQUEST_NOTIFICATIONS = 1
    }
}
