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
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.content.res.Configuration
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import java.net.HttpURLConnection
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

class MainActivity : Activity() {
    private val handler = Handler(Looper.getMainLooper())
    private val io = Executors.newSingleThreadExecutor()

    private lateinit var pcStatus: TextView
    private lateinit var jobsContainer: LinearLayout
    private lateinit var notificationStatus: TextView
    private lateinit var batteryStatus: TextView
    private lateinit var adbTimeoutStatus: TextView
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
        val night = (resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES
        val lightBars = WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS or WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS
        window.insetsController?.setSystemBarsAppearance(if (night) 0 else lightBars, lightBars)
        val pad = dp(16)
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(pad, pad, pad, pad)
        }

        column.addView(heading("AdbSync Companion"))
        pcStatus = body()
        column.addView(pcStatus)
        jobsContainer = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        column.addView(jobsContainer)

        column.addView(heading("Setup").apply { setPadding(0, dp(24), 0, dp(4)) })

        notificationStatus = body()
        column.addView(notificationStatus)
        column.addView(button("Allow notifications") {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), REQUEST_NOTIFICATIONS)
        })

        batteryStatus = body()
        column.addView(batteryStatus)
        column.addView(button("Exempt from battery optimization") { requestBatteryExemption() })

        adbTimeoutStatus = body()
        column.addView(adbTimeoutStatus)
        column.addView(button("Open developer options") {
            startActivity(Intent(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS))
        })

        serviceStatus = body()
        column.addView(serviceStatus)
        column.addView(button("Simulate a 10s sync") { simulateSync() })

        setContentView(ScrollView(this).apply {
            addView(column)
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

    private fun startFeed() {
        stopFeed()
        val config = PcConfig.load(this) ?: run {
            pcStatus.text = "Not set up yet. Connect this phone to AdbSync over adb (USB or wireless debugging) " +
                "and it will send its address here within a few minutes."
            return
        }

        val active = AtomicBoolean(true)
        feedActive = active
        val client = PcClient(config)
        val listener = object : PcClient.FeedListener {
            override fun onConnected() = ui { if (active.get()) pcStatus.text = "Connected to ${config.pcName}" }
            override fun onSnapshot(pc: String, jobs: List<RemoteJob>) = ui {
                if (!active.get()) return@ui
                this@MainActivity.jobs.clear()
                jobs.forEach { this@MainActivity.jobs[it.name] = it }
                pcStatus.text = "Connected to $pc"
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
                        pcStatus.text = if (e.code == 401) {
                            "${config.pcName} rejected this phone's token. Connect over adb so AdbSync can resend it."
                        } else {
                            "${config.pcName} returned HTTP ${e.code}"
                        }
                    }
                } catch (e: javax.net.ssl.SSLException) {
                    ui {
                        if (active.get()) pcStatus.text = "${config.pcName}'s certificate doesn't match the one this phone trusts. " +
                            "Connect over adb so AdbSync can resend it."
                    }
                } catch (e: Exception) {
                    ui {
                        if (active.get()) pcStatus.text = "Can't reach ${config.pcName} at ${config.url} (${e.javaClass.simpleName}). Retrying…"
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
            jobsContainer.addView(body().apply { text = "No jobs configured on the PC." })
            return
        }
        for (job in jobs.values) jobsContainer.addView(jobRow(job))
    }

    private fun jobRow(job: RemoteJob): View {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dp(12), 0, dp(4))
        }
        row.addView(TextView(this).apply {
            text = if (job.enabled) job.name else "${job.name} (disabled)"
            textSize = 17f
            setTypeface(typeface, Typeface.BOLD)
        })
        row.addView(TextView(this).apply {
            text = job.statusText()
            textSize = 14f
            if (!job.running && job.lastFailed) setTextColor(0xFFE5484D.toInt())
        })

        val buttons = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        if (job.running) {
            buttons.addView(smallButton("Stop") { confirmStop(job) })
        } else {
            buttons.addView(smallButton("Run now") { act(job.name, "Run") { it.run(job.name) } })
        }
        buttons.addView(smallButton("Log") { showLog(job.name) })
        row.addView(buttons)
        return row
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
                    setTextIsSelectable(true)
                    setPadding(dp(16), dp(8), dp(16), dp(8))
                }
                val scroll = ScrollView(this).apply { addView(view) }
                AlertDialog.Builder(this)
                    .setTitle(jobName)
                    .setView(scroll)
                    .setPositiveButton("Close", null)
                    .show()
                scroll.post { scroll.fullScroll(View.FOCUS_DOWN) }
            }
        }
    }

    private fun refreshSetup() {
        val notificationsOk = checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
        notificationStatus.text = if (notificationsOk) "✓ Notifications allowed" else "✗ Notifications blocked: progress won't be visible"

        val batteryOk = getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(packageName)
        batteryStatus.text = if (batteryOk) {
            "✓ Exempt from battery optimization"
        } else {
            "✗ Battery optimized: Android may refuse to start the sync service from the background"
        }

        adbTimeoutStatus.text = when (readAdbAuthTimeoutDisabled()) {
            true -> "✓ adb authorization timeout disabled"
            false -> "! adb authorization timeout is on: the PC's key is revoked after 7 days unused. " +
                "Turn on \"Disable adb authorization timeout\" in developer options."
            null -> "? Couldn't read the adb authorization timeout setting. Check \"Disable adb authorization timeout\" " +
                "in developer options."
        }

        serviceStatus.text = if (SyncService.isRunning) "Sync service running (locks held)" else "Sync service idle"
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

    private fun heading(text: String) = TextView(this).apply {
        this.text = text
        textSize = 22f
        setPadding(0, 0, 0, dp(4))
    }

    private fun body() = TextView(this).apply {
        textSize = 15f
        setPadding(0, dp(12), 0, dp(6))
    }

    private fun button(text: String, onClick: () -> Unit) = Button(this).apply {
        this.text = text
        layoutParams = LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT)
        setOnClickListener { onClick() }
    }

    private fun smallButton(text: String, onClick: () -> Unit) = Button(this).apply {
        this.text = text
        layoutParams = LinearLayout.LayoutParams(WRAP_CONTENT, WRAP_CONTENT).apply { marginEnd = dp(8) }
        setOnClickListener { onClick() }
    }

    private fun dp(value: Int) = (value * resources.displayMetrics.density).toInt()

    private companion object {
        const val REQUEST_NOTIFICATIONS = 1
    }
}
