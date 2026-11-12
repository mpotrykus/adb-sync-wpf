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
import com.adbsync.companion.Ui.header
import com.adbsync.companion.Ui.toggle
import android.text.TextUtils
import android.text.format.DateUtils
import android.text.format.Formatter
import android.widget.PopupMenu
import android.widget.FrameLayout
import android.widget.EditText
import java.time.OffsetDateTime
import java.net.HttpURLConnection
import java.time.Duration
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

class MainActivity : Activity() {
    private val handler = Handler(Looper.getMainLooper())
    private val io = Executors.newSingleThreadExecutor()

    private lateinit var pcStatus: LinearLayout
    private lateinit var conflictsBanner: LinearLayout
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

        column.addView(header(button("Web UI", fill = false) { openWebUi() }.apply {
            (layoutParams as LinearLayout.LayoutParams).marginEnd = 0
        }))
        pcStatus = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dp(6), 0, 0)
        }
        column.addView(pcStatus)

        conflictsBanner = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dp(12), 0, 0)
        }
        column.addView(conflictsBanner)

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
        handlePairingIntent(intent)
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

    private fun openWebUi() {
        val config = PcConfig.load(this)
        if (config == null) {
            Toast.makeText(this, "Connect to a server first.", Toast.LENGTH_SHORT).show()
            return
        }
        io.execute {
            val code = runCatching { PcClient(config).createPairingCode() }.getOrNull()
            val target = if (code != null) "${config.url.trimEnd('/')}/signin?code=${Uri.encode(code)}" else config.url
            ui { chooseBrowser(target) }
        }
    }

    private fun chooseBrowser(target: String) {
        val probe = Intent(Intent.ACTION_VIEW, Uri.parse("https://example.com"))
            .addCategory(Intent.CATEGORY_BROWSABLE)
        val browsers = packageManager.queryIntentActivities(probe, PackageManager.MATCH_ALL)
            .distinctBy { it.activityInfo.packageName }
        if (browsers.isEmpty()) {
            Toast.makeText(this, "No browser found to open the web UI.", Toast.LENGTH_LONG).show()
            return
        }
        AlertDialog.Builder(this)
            .setTitle("Open web UI with")
            .setItems(browsers.map { it.loadLabel(packageManager).toString() }.toTypedArray()) { _, which ->
                startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(target)).setPackage(browsers[which].activityInfo.packageName))
            }
            .setNegativeButton("Cancel", null)
            .create()
            .styled()
            .show()
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

    private fun renderConflictsBanner() {
        conflictsBanner.removeAllViews()
        val total = jobs.values.sumOf { it.pendingConflicts }
        if (total == 0) return
        conflictsBanner.addView(card().apply {
            addView(sectionLabel(if (total == 1) "1 file needs a decision" else "$total files need a decision"))
            addView(caption("Changed in two places, so AdbSync left it alone. Pick which version to keep.").apply {
                setPadding(0, dp(4), 0, dp(10))
            })
            addView(button("Resolve", ButtonKind.Primary, fill = true) {
                startActivity(Intent(this@MainActivity, ConflictsActivity::class.java))
            })
        })
    }

    private fun renderJobs() {
        renderConflictsBanner()
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
            maxLines = 2
            ellipsize = TextUtils.TruncateAt.END
        })
        if (job.needsAttention) header.addView(badge("⚠", Ui.WARNING))
        if (job.hasCheckpoint) header.addView(badge("↻", CHECKPOINT_BLUE))
        header.addView(View(this@MainActivity).apply { layoutParams = LinearLayout.LayoutParams(0, 0, 1f) })
        header.addView(toggle("").apply {
            isChecked = job.enabled
            setOnCheckedChangeListener { _, checked ->
                act(job.name, "Update") { it.setEnabled(job.name, checked) }
            }
        })
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
        addView(caption(scheduleText(job)).apply { setPadding(dp(15), dp(4), 0, 0) })
        job.staleDevices.forEach { (device, reason) ->
            addView(body("$device is out of sync: $reason", Ui.WARNING, 12f).apply { setPadding(dp(15), dp(4), 0, 0) })
        }
        if (job.hasCheckpoint && !job.running) {
            job.checkpointSummary?.let { summary ->
                addView(body(summary, CHECKPOINT_BLUE, 12f).apply { setPadding(dp(15), dp(4), 0, 0) })
            }
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
        buttons.addView(button("History") { showHistory(job.name, job.running) })
        if (!job.running || job.hasCheckpoint) {
            lateinit var more: View
            more = button("⋯") { showMoreMenu(more, job) }
            buttons.addView(more)
        }
        addView(buttons)
    }

    private fun badge(glyph: String, color: Int) = TextView(this).apply {
        text = glyph
        textSize = 14f
        setTextColor(color)
        setPadding(dp(8), 0, 0, 0)
    }

    private fun scheduleText(job: RemoteJob): String {
        val last = job.lastResultAt?.let {
            DateUtils.getRelativeTimeSpanString(it.toInstant().toEpochMilli(), System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS).toString()
        } ?: "never"
        val next = when {
            !job.enabled -> "-"
            job.nextRunAt == null -> "-"
            !job.nextRunAt.isAfter(OffsetDateTime.now()) -> "due now"
            else -> job.nextRunAt.atZoneSameInstant(ZoneId.systemDefault()).format(NEXT_RUN_FORMAT)
        }
        return "Last run $last  ·  Next run $next"
    }

    private fun showMoreMenu(anchor: View, job: RemoteJob) {
        val menu = PopupMenu(this, anchor)
        if (!job.running) menu.menu.add(0, MENU_DRY_RUN, 0, "Dry run")
        if (job.hasCheckpoint && !job.running) menu.menu.add(0, MENU_DISCARD, 1, "Discard checkpoint")
        menu.setOnMenuItemClickListener { item ->
            when (item.itemId) {
                MENU_DRY_RUN -> act(job.name, "Dry run") { it.dryRun(job.name) }
                MENU_DISCARD -> confirmDiscardCheckpoint(job)
            }
            true
        }
        menu.show()
    }

    private fun confirmDiscardCheckpoint(job: RemoteJob) {
        AlertDialog.Builder(this)
            .setTitle("Discard checkpoint?")
            .setMessage(
                "${job.name} was interrupted (${job.checkpointSummary}).\n\n" +
                    "Discarding its checkpoint means the next run starts over from the very beginning instead of continuing where it left off.\n\n" +
                    "Files already synced are not affected.",
            )
            .setPositiveButton("Discard") { _, _ -> act(job.name, "Discard") { it.discardCheckpoint(job.name) } }
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

    private fun showLog(jobName: String) = showLogDialog(jobName) { it.log(jobName) }

    private fun showLogDialog(title: String, fetch: (PcClient) -> String) {
        val config = PcConfig.load(this) ?: return
        io.execute {
            val text = try {
                fetch(PcClient(config))
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
                    .setTitle(title)
                    .setView(scroll)
                    .setPositiveButton("Close", null)
                    .create()
                    .styled()
                    .show()
                scroll.post { scroll.fullScroll(View.FOCUS_DOWN) }
            }
        }
    }

    private fun showHistory(jobName: String, running: Boolean) {
        val config = PcConfig.load(this) ?: return
        io.execute {
            val runs = try {
                PcClient(config).history(jobName)
            } catch (e: HttpStatusException) {
                ui { Toast.makeText(this, "Couldn't load history (HTTP ${e.code})", Toast.LENGTH_SHORT).show() }
                return@execute
            } catch (e: Exception) {
                ui { Toast.makeText(this, "Couldn't load history: ${e.javaClass.simpleName}", Toast.LENGTH_SHORT).show() }
                return@execute
            }
            ui { renderHistoryDialog(jobName, runs, running) }
        }
    }

    private fun openRunDetail(jobName: String, title: String, runId: String?) {
        val config = PcConfig.load(this) ?: return
        RunDetailDialog(this, config, jobName, title, runId).show()
    }

    private fun renderHistoryDialog(jobName: String, runs: List<RemoteRun>, running: Boolean) {
        val list = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(4), dp(20), dp(8))
        }
        val scroll = ScrollView(this).apply { addView(list) }
        val dialog = AlertDialog.Builder(this)
            .setTitle("$jobName history")
            .setView(scroll)
            .setPositiveButton("Close", null)
            .create()
            .styled()

        if (running) {
            list.addView(LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                isClickable = true
                setOnClickListener { openRunDetail(jobName, "$jobName · running", null) }
                addView(statusLine("Running now", Ui.ACCENT, Ui.TEXT_PRIMARY))
                addView(caption("Tap to watch it live").apply { setPadding(dp(15), dp(2), 0, 0) })
            })
        }
        if (runs.isEmpty() && !running) list.addView(caption("No runs recorded yet."))
        val format = DateTimeFormatter.ofLocalizedDateTime(FormatStyle.MEDIUM, FormatStyle.SHORT)
        runs.forEachIndexed { i, run ->
            if (i > 0 || running) list.addView(divider())
            val color = when {
                run.failed -> Ui.DANGER
                run.skipped -> Ui.WARNING
                else -> Ui.SUCCESS
            }
            val started = run.startedAt.atZoneSameInstant(ZoneId.systemDefault()).format(format)
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                isClickable = true
                setOnClickListener { openRunDetail(jobName, "$jobName · $started", run.runId) }
            }
            row.addView(statusLine("${run.outcomeText()} · $started", color, Ui.TEXT_PRIMARY))
            val seconds = Duration.between(run.startedAt, run.completedAt).seconds
            val details = buildList {
                if (run.filesCopied > 0) add("${run.filesCopied} copied")
                if (run.filesDeleted > 0) add("${run.filesDeleted} deleted")
                if (run.bytesCopied > 0) add(Formatter.formatShortFileSize(this@MainActivity, run.bytesCopied))
                if (run.errorCount > 0) add("${run.errorCount} errors")
                add("${seconds}s")
            }
            row.addView(caption(details.joinToString(" · ")).apply { setPadding(dp(15), dp(2), 0, 0) })
            run.errorMessage?.let { row.addView(body(it, Ui.DANGER, 12f).apply { setPadding(dp(15), dp(2), 0, 0) }) }
            list.addView(row)
        }
        dialog.show()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handlePairingIntent(intent)
    }

    private fun handlePairingIntent(intent: Intent?) {
        val link = intent?.data?.toString()?.let { PairingLink.parse(it) } ?: return
        intent.data = null
        showConnectDialog(link)
    }

    /** Asks for a pairing link (pasted, or opened from the QR code) and pairs this phone with the server. */
    private fun showConnectDialog(prefilled: PairingLink?) {
        val input = EditText(this).apply {
            hint = "adbsync://pair?url=…"
            setSingleLine(false)
            minLines = 2
            textSize = 13f
            setTextColor(Ui.TEXT_PRIMARY)
            setHintTextColor(Ui.TEXT_SECONDARY)
            if (prefilled != null) setText("adbsync://pair?url=${Uri.encode(prefilled.url)}&code=${Uri.encode(prefilled.code)}&fp=${prefilled.certSha256}")
        }
        val holder = FrameLayout(this).apply {
            setPadding(dp(20), dp(8), dp(20), 0)
            addView(input)
        }
        AlertDialog.Builder(this)
            .setTitle("Connect to server")
            .setMessage("In the web UI, open the account menu → Pair another client, then scan the QR code with this phone's camera, or paste the pairing link here.")
            .setView(holder)
            .setPositiveButton("Connect") { _, _ -> connect(input.text.toString()) }
            .setNegativeButton("Cancel", null)
            .create()
            .styled()
            .show()
        if (prefilled != null) connect(input.text.toString())
    }

    private fun connect(text: String) {
        val link = PairingLink.parse(text)
        if (link == null) {
            Toast.makeText(this, "That isn't an AdbSync pairing link.", Toast.LENGTH_LONG).show()
            return
        }
        val version = runCatching { packageManager.getPackageInfo(packageName, 0).versionName }.getOrNull()
        Toast.makeText(this, "Connecting…", Toast.LENGTH_SHORT).show()
        io.execute {
            val result = runCatching { Enrollment.pair(link, version) }
            ui {
                result.onSuccess { config ->
                    PcConfig.save(this, config)
                    chooseDevice(config)
                }.onFailure {
                    val message = (it as? EnrollmentException)?.message ?: "Couldn't reach the server: ${it.javaClass.simpleName}"
                    Toast.makeText(this, message, Toast.LENGTH_LONG).show()
                }
            }
        }
    }

    /** Which of the server's devices is this phone? The server needs to know to reach it and to guard its apps. */
    private fun chooseDevice(config: PcConfig) {
        io.execute {
            val devices = runCatching { PcClient(config).deviceList() }.getOrNull()
            ui {
                if (devices.isNullOrEmpty()) {
                    Toast.makeText(this, "The server has no devices yet. Add this phone in the web UI first, then choose it here.", Toast.LENGTH_LONG).show()
                    return@ui
                }
                AlertDialog.Builder(this)
                    .setTitle("Which device is this phone?")
                    .setItems(devices.map { it.name + (it.host?.let { h -> " · $h" } ?: "") }.toTypedArray()) { _, which ->
                        PcConfig.save(this, config.copy(deviceName = devices[which].name))
                        Toast.makeText(this, "Connected as ${devices[which].name}", Toast.LENGTH_SHORT).show()
                    }
                    .setNegativeButton("Later", null)
                    .create()
                    .styled()
                    .show()
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

        val connection = PcConfig.load(this)
        item(
            connection?.deviceName != null,
            "Connected to ${connection?.pcName} as ${connection?.deviceName}",
            if (connection == null) "Not connected to an AdbSync server" else "Connected to ${connection.pcName}, but this phone isn't chosen yet",
            if (connection == null) "Connect to server" else "Choose this phone",
        ) { if (connection == null) showConnectDialog(null) else chooseDevice(connection) }

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

        item(
            AppGate.isEnabled(this),
            "App gate on: you're warned before opening an out-of-sync app here",
            "App gate off: nothing warns you before opening an out-of-sync app here",
            "Turn on app gate",
        ) { enableAppGate() }

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

    private fun enableAppGate() {
        if (AppGate.canEnableDirectly(this) && AppGate.enable(this)) {
            refreshSetup()
            return
        }
        Toast.makeText(this, "Turn on \"AdbSync app gate\" in the accessibility settings.", Toast.LENGTH_LONG).show()
        startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
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
        const val MENU_DRY_RUN = 1
        const val MENU_DISCARD = 2
        const val CHECKPOINT_BLUE = 0xFF4FC3F7.toInt()
        val NEXT_RUN_FORMAT: DateTimeFormatter = DateTimeFormatter.ofLocalizedDateTime(FormatStyle.MEDIUM, FormatStyle.SHORT)
    }
}
