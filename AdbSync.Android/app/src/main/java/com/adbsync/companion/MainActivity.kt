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
import com.adbsync.companion.Ui.chip
import com.adbsync.companion.Ui.statTile
import com.adbsync.companion.Ui.syncChip
import com.adbsync.companion.Ui.statStrip
import com.adbsync.companion.Ui.phaseGraph
import com.adbsync.companion.Ui.infoCell
import com.adbsync.companion.Ui.infoPanel
import com.adbsync.companion.Ui.iconButton
import com.adbsync.companion.Ui.pageHead
import com.adbsync.companion.Ui.appIcon
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import com.adbsync.companion.Ui.header
import com.adbsync.companion.Ui.toggle
import com.adbsync.companion.Ui.page
import com.adbsync.companion.Ui.loader
import com.adbsync.companion.Ui.spacer
import java.util.concurrent.ExecutorService
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
    private lateinit var statsContainer: LinearLayout
    private lateinit var jobsPage: View
    private lateinit var settingsPage: View
    private lateinit var jobsToolbar: LinearLayout
    private lateinit var history: HistoryScreen
    private lateinit var conflictsScreen: ConflictsScreen
    private lateinit var devicesScreen: DevicesScreen
    private lateinit var serverSettings: SettingsScreen
    private lateinit var screens: List<Screen?>
    private lateinit var allPages: List<View>
    private lateinit var tabBar: Ui.TabBar
    private lateinit var brandMark: Ui.BrandMark
    private var schedulerPaused: Boolean? = null
    private var jobsLoaded = false
    private var jobMeta: Map<String, JobMeta> = emptyMap()
    private var deviceStates: Map<String, String> = emptyMap()
    private var knownRunning = emptySet<String>()
    private var knownConflicts = -1
    internal val pool: ExecutorService = Executors.newCachedThreadPool()
    private var currentTab = 0
    private lateinit var wifiDebugStatus: LinearLayout
    private lateinit var autoEnableSwitch: Switch
    private lateinit var serviceStatus: TextView
    private lateinit var browserCard: LinearLayout

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
        window.setBackgroundDrawable(Ui.windowBackground(this))
        window.isNavigationBarContrastEnforced = false
        window.insetsController?.setSystemBarsAppearance(
            0, WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS or WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS,
        )

        pcStatus = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dp(2), 0, dp(6))
        }
        conflictsBanner = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dp(12), 0, 0)
        }
        statsContainer = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dp(12), 0, 0)
        }
        jobsContainer = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        val landscape = resources.configuration.orientation == android.content.res.Configuration.ORIENTATION_LANDSCAPE
        jobsToolbar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(0, 0, 0, dp(12))
        }
        jobsPage = page(jobsToolbar, *listOfNotNull(if (landscape) null else pcStatus).toTypedArray(), conflictsBanner, statsContainer, jobsContainer)
        renderToolbar()

        wifiDebugStatus = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        autoEnableSwitch = toggle("Turn back on when Wi-Fi connects").apply {
            setOnCheckedChangeListener { _, checked ->
                WirelessDebugging.setAutoEnable(this@MainActivity, checked)
                if (checked) CompanionJobService.scheduleEnable(this@MainActivity)
            }
        }
        serviceStatus = caption()
        setupCard = card()
        browserCard = card()
        history = HistoryScreen(this)
        conflictsScreen = ConflictsScreen(this)
        devicesScreen = DevicesScreen(this)
        serverSettings = SettingsScreen(this)
        settingsPage = page(
            pageHead("Settings", iconButton(R.drawable.ic_save, "Save server settings", filled = true) { serverSettings.saveNow() }),
            subSectionLabel("This phone"),
            sectionLabel("Wireless debugging").apply { setPadding(dp(4), 0, 0, dp(8)) },
            card().apply {
                addView(wifiDebugStatus)
                addView(divider())
                addView(autoEnableSwitch)
                addView(divider())
                addView(button("Pair with PC over Wi-Fi", fill = true) { startPairing() })
            },
            subSectionLabel("Setup"),
            setupCard,
            subSectionLabel("Web UI"),
            browserCard,
            subSectionLabel("Diagnostics"),
            card().apply {
                addView(serviceStatus)
                addView(View(this@MainActivity).apply { minimumHeight = dp(10) })
                addView(button("Simulate a 10s sync", fill = true) { simulateSync() })
            },
            subSectionLabel("Server"),
            serverSettings.view,
        )
        screens = listOf(null, history, conflictsScreen, devicesScreen, serverSettings)

        brandMark = Ui.BrandMark(this)
        val appBar = header(iconButton(R.drawable.ic_public, "Open web UI") { openWebUi() }, brandMark)
        allPages = listOf(jobsPage, history.view, conflictsScreen.view, devicesScreen.view, settingsPage)
        val pages = FrameLayout(this).apply { allPages.forEach { addView(it) } }
        tabBar = Ui.TabBar(
            this,
            listOf(
                R.drawable.ic_sync to "Jobs", R.drawable.ic_history to "History", R.drawable.ic_conflict to "Conflicts",
                R.drawable.ic_phone to "Devices", R.drawable.ic_settings to "Settings",
            ),
            rail = landscape,
        ) { showTab(it) }
        if (landscape) {
            pcStatus.setPadding(dp(12), 0, dp(12), 0)
            appBar.addView(pcStatus, 2)
            statsContainer.setPadding(0, 0, 0, 0)
        }
        val verticalPad = if (landscape) dp(6) else dp(10)
        appBar.setPadding(appBar.paddingLeft, verticalPad, appBar.paddingRight, verticalPad)
        setContentView(if (landscape) {
            LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                addView(tabBar, LinearLayout.LayoutParams(dp(72), MATCH_PARENT))
                addView(LinearLayout(context).apply {
                    orientation = LinearLayout.VERTICAL
                    addView(appBar)
                    addView(pages, LinearLayout.LayoutParams(MATCH_PARENT, 0, 1f))
                }, LinearLayout.LayoutParams(0, MATCH_PARENT, 1f))
                setOnApplyWindowInsetsListener { _, insets ->
                    val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
                    setPadding(0, 0, bars.right, bars.bottom)
                    tabBar.setPadding(bars.left, bars.top, 0, 0)
                    appBar.setPadding(appBar.paddingLeft, bars.top + verticalPad, appBar.paddingRight, verticalPad)
                    insets
                }
            }
        } else {
            LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                addView(appBar)
                addView(pages, LinearLayout.LayoutParams(MATCH_PARENT, 0, 1f))
                addView(tabBar)
                setOnApplyWindowInsetsListener { view, insets ->
                    val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
                    view.setPadding(bars.left, 0, bars.right, 0)
                    appBar.setPadding(appBar.paddingLeft, bars.top + verticalPad, appBar.paddingRight, verticalPad)
                    tabBar.setPadding(0, 0, 0, bars.bottom)
                    insets
                }
            }
        })
        showTab(savedInstanceState?.getInt(STATE_TAB) ?: 0)
        handlePairingIntent(intent)
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        outState.putInt(STATE_TAB, currentTab)
    }

    internal fun showTab(index: Int) {
        if (::screens.isInitialized) screens.getOrNull(currentTab)?.hide()
        currentTab = index
        allPages.forEachIndexed { i, page -> page.visibility = if (i == index) View.VISIBLE else View.GONE }
        tabBar.select(index)
        screens.getOrNull(index)?.show()
    }

    internal fun <T> async(work: (PcClient) -> T, done: (Result<T>) -> Unit) {
        val config = PcConfig.load(this)
        if (config == null) {
            done(Result.failure(NotConnectedException()))
            return
        }
        pool.execute {
            val result = runCatching { work(PcClient(config)) }
            ui { done(result) }
        }
    }

    internal fun toast(message: String) = Toast.makeText(this, message, Toast.LENGTH_SHORT).show()

    internal fun ui(block: () -> Unit) {
        handler.post { if (!isDestroyed) block() }
    }

    internal fun setConflictCount(count: Int) {
        tabBar.setBadge(2, count > 0)
    }

    override fun onResume() {
        super.onResume()
        resumed = true
        PcConfig.prefs(this).registerOnSharedPreferenceChangeListener(prefsListener)
        refreshSetup()
        startFeed()
        loadScheduler()
        loadMeta()
        screens.getOrNull(currentTab)?.show()
        CompanionJobService.armWatch(this)
        CompanionJobService.scheduleEnable(this)
    }

    override fun onPause() {
        resumed = false
        screens.getOrNull(currentTab)?.hide()
        PcConfig.prefs(this).unregisterOnSharedPreferenceChangeListener(prefsListener)
        stopFeed()
        super.onPause()
    }

    override fun onDestroy() {
        io.shutdownNow()
        pool.shutdownNow()
        conflictsScreen.shutdown()
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

    private fun uiPrefs() = getSharedPreferences("ui", MODE_PRIVATE)

    private fun browserList(): List<android.content.pm.ResolveInfo> {
        val probe = Intent(Intent.ACTION_VIEW, Uri.parse("https://example.com"))
            .addCategory(Intent.CATEGORY_BROWSABLE)
        return packageManager.queryIntentActivities(probe, PackageManager.MATCH_ALL)
            .distinctBy { it.activityInfo.packageName }
            .sortedBy { it.loadLabel(packageManager).toString().lowercase() }
    }

    private fun launchInBrowser(target: String, pkg: String) =
        startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(target)).setPackage(pkg))

    private fun chooseBrowser(target: String) {
        val browsers = browserList()
        if (browsers.isEmpty()) {
            Toast.makeText(this, "No browser found to open the web UI.", Toast.LENGTH_LONG).show()
            return
        }
        val remembered = uiPrefs().getString(PREF_BROWSER, null)
        if (remembered != null && browsers.any { it.activityInfo.packageName == remembered }) {
            launchInBrowser(target, remembered)
            return
        }
        val list = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(4), dp(16), dp(8))
        }
        val dialog = AlertDialog.Builder(this)
            .setTitle("Open web UI with")
            .setView(ScrollView(this).apply { addView(list) })
            .setNegativeButton("Cancel", null)
            .create()
            .styled()
        browsers.forEach { browser ->
            val pkg = browser.activityInfo.packageName
            list.addView(LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(dp(12), dp(10), dp(12), dp(10))
                isClickable = true
                background = android.graphics.drawable.RippleDrawable(
                    android.content.res.ColorStateList.valueOf(0x33FFFFFF),
                    Ui.glass(0x0FFFFFFF, 0x1AFFFFFF, dp(8), dp(1)),
                    Ui.rounded(0xFFFFFFFF.toInt(), dp(8)),
                )
                layoutParams = LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply { bottomMargin = dp(8) }
                addView(android.widget.ImageView(context).apply {
                    setImageDrawable(browser.loadIcon(packageManager))
                }, LinearLayout.LayoutParams(dp(32), dp(32)).apply { marginEnd = dp(14) })
                addView(body(browser.loadLabel(packageManager).toString(), Ui.TEXT_PRIMARY, 15f))
                setOnClickListener {
                    uiPrefs().edit().putString(PREF_BROWSER, pkg).apply()
                    refreshSetup()
                    dialog.dismiss()
                    launchInBrowser(target, pkg)
                }
            })
        }
        list.addView(caption("Your choice is remembered. Clear it from the Device tab.").apply { setPadding(dp(4), dp(4), 0, 0) })
        dialog.show()
    }

    private fun rememberedBrowserLabel(): String? {
        val pkg = uiPrefs().getString(PREF_BROWSER, null) ?: return null
        return runCatching { packageManager.getApplicationInfo(pkg, 0).loadLabel(packageManager).toString() }.getOrDefault(pkg)
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
                jobsLoaded = true
                this@MainActivity.jobs.clear()
                jobs.forEach { this@MainActivity.jobs[it.name] = it }
                setPcStatus("Connected to $pc", Ui.SUCCESS)
                renderJobs()
                loadMeta()
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
        conflictsBanner.addView(card(Ui.WARNING).apply {
            addView(sectionLabel(if (total == 1) "1 file needs a decision" else "$total files need a decision"))
            addView(caption("Changed in two places, so AdbSync left it alone. Pick which version to keep.").apply {
                setPadding(0, dp(4), 0, dp(10))
            })
            addView(button("Resolve", ButtonKind.Primary, fill = true) {
                showTab(2)
            })
        })
    }

    private fun syncSideScreens() {
        val running = jobs.values.filter { it.running }.map { it.name }.toSet()
        brandMark.setSyncing(running.isNotEmpty())
        if (running != knownRunning) {
            val firstTime = knownRunning.isEmpty() && knownConflicts < 0
            knownRunning = running
            if (!firstTime) history.jobsChanged()
        }
        val conflicts = jobs.values.sumOf { it.pendingConflicts }
        setConflictCount(conflicts)
        if (conflicts != knownConflicts) {
            val firstTime = knownConflicts < 0
            knownConflicts = conflicts
            if (!firstTime) conflictsScreen.conflictsChanged()
        }
    }

    private fun renderJobs() {
        syncSideScreens()
        renderToolbar()
        renderConflictsBanner()
        renderStats()
        jobsContainer.removeAllViews()
        if (jobs.isEmpty() && PcConfig.load(this) != null && !jobsLoaded) {
            jobsContainer.addView(loader())
            return
        }
        if (jobs.isEmpty()) {
            jobsContainer.addView(card().apply {
                addView(caption(if (PcConfig.load(this@MainActivity) == null) "Jobs appear here once the PC is connected." else "No jobs configured on the PC."))
            })
            return
        }
        val columns = ((resources.configuration.screenWidthDp - 32) / MIN_CARD_WIDTH_DP).coerceAtLeast(1)
        if (columns == 1) {
            for (job in jobs.values) jobsContainer.addView(jobCard(job))
            return
        }
        for (rowJobs in jobs.values.chunked(columns)) {
            jobsContainer.addView(LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                isBaselineAligned = false
                for (i in 0 until columns) {
                    val cell = rowJobs.getOrNull(i)?.let { jobCard(it) } ?: View(this@MainActivity)
                    addView(cell, LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f).apply {
                        if (i < columns - 1) marginEnd = dp(12)
                        bottomMargin = dp(12)
                    })
                }
            })
        }
    }

    private fun renderStats() {
        statsContainer.removeAllViews()
        if (jobs.isEmpty()) return
        val all = jobs.values
        val running = all.count { it.running }
        val conflicts = all.sumOf { it.pendingConflicts }
        val failed = all.count { it.lastFailed }
        val next = all.filter { it.enabled }.mapNotNull { it.nextRunAt }.minOrNull()
        val nextText = when {
            next == null -> "-"
            !next.isAfter(OffsetDateTime.now()) -> "now"
            else -> DateUtils.getRelativeTimeSpanString(
                next.toInstant().toEpochMilli(), System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS, DateUtils.FORMAT_ABBREV_RELATIVE,
            ).toString()
        }
        statsContainer.addView(statStrip(listOf(
            statTile("$running", "Running", Ui.ACCENT_FROM),
            statTile("${all.count { it.enabled }}/${all.size}", "Enabled", Ui.SUCCESS),
            statTile("$conflicts", "Conflicts", Ui.WARNING) {
                if (conflicts > 0) showTab(2)
            },
            statTile("$failed", "Failed", Ui.DANGER),
            statTile(nextText, "Next run", 0xFF38BDF8.toInt()),
        )))
    }

    private fun jobCard(job: RemoteJob): View {
        val stateColor = when {
            job.running -> Ui.ACCENT_FROM
            !job.enabled -> 0xFF5C5870.toInt()
            job.lastFailed -> Ui.DANGER
            job.pendingConflicts > 0 -> Ui.WARNING
            else -> Ui.SUCCESS
        }
        return card(stateColor, glow = job.running).apply {
            val header = LinearLayout(this@MainActivity).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
            }
            header.addView(jobIcon(job))
            header.addView(LinearLayout(this@MainActivity).apply {
                orientation = LinearLayout.VERTICAL
                addView(sectionLabel(job.name).apply {
                    textSize = 17f
                    if (!job.enabled) setTextColor(Ui.TEXT_SECONDARY)
                    maxLines = 2
                    ellipsize = TextUtils.TruncateAt.END
                })
                addView(caption(job.appPackage ?: "No linked app").apply {
                    typeface = Typeface.MONOSPACE
                    textSize = 11f
                    maxLines = 1
                    ellipsize = TextUtils.TruncateAt.MIDDLE
                })
            }, LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f))
            header.addView(toggle("").apply {
                isChecked = job.enabled
                setOnCheckedChangeListener { _, checked ->
                    act(job.name, "Update") { it.setEnabled(job.name, checked) }
                }
            })
            addView(header)

            val chips = LinearLayout(this@MainActivity).apply {
                orientation = LinearLayout.HORIZONTAL
                setPadding(0, dp(12), 0, 0)
            }
            val waiting = job.phase == "WaitingForAppClose" || job.devices.any { it.second == "WaitingForAppClose" }
            when {
                waiting -> chips.addView(chip("Waiting · app is open", Ui.WARNING))
                job.running -> chips.addView(syncChip(
                    "Running" + (job.phase?.let { " · " + Protocol.phaseLabel(it) } ?: ""), Ui.ACCENT_FROM,
                ))
                else -> chips.addView(chip(if (job.enabled) "Idle" else "Disabled", Ui.TEXT_SECONDARY))
            }
            if (job.pendingConflicts > 0) {
                chips.addView(chip(
                    "${job.pendingConflicts} conflict${if (job.pendingConflicts == 1) "" else "s"}", Ui.WARNING,
                ) { showTab(2) })
            }
            if (job.folderProblem != null) chips.addView(chip("Folder unavailable", Ui.DANGER))
            if (job.hasCheckpoint) chips.addView(chip("Resumable", CHECKPOINT_BLUE))
            addView(chips)

            addView(phaseGraph(phaseSteps(job)).apply { if (!job.running) visibility = View.INVISIBLE })

            addView(View(this@MainActivity).apply { minimumHeight = dp(8) })
            val last = job.lastResultAt?.let {
                DateUtils.getRelativeTimeSpanString(it.toInstant().toEpochMilli(), System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS).toString()
            }
            val lastCell = infoCell("Last run", body(
                if (job.lastResult == null) "Never" else job.lastResult + (last?.let { " · $it" } ?: ""),
                if (job.lastResult == null) Ui.TEXT_PRIMARY else Ui.resultColor(job.lastResult, job.lastFailed), 14f,
            )).apply {
                val runId = job.lastRunId
                if (runId != null) {
                    isClickable = true
                    setOnClickListener { openRunDetail(job.name, "${job.name} \u00b7 last run", runId) }
                }
            }
            addView(infoPanel(listOf(
                lastCell,
                infoCell("Devices", deviceRows(job)),
                infoCell("Trigger", body(triggerText(job), Ui.TEXT_PRIMARY, 14f)),
            )))
            if (job.lastMessage != null && (job.lastFailed || job.lastStatus == "skipped" || job.lastStatus == "cancelled")) {
                addView(body(job.lastMessage, Ui.WARNING, 12f).apply { setPadding(0, dp(6), 0, 0) })
            }
            if (job.hasCheckpoint && !job.running) {
                job.checkpointSummary?.let { summary ->
                    addView(body(summary, CHECKPOINT_BLUE, 12f).apply { setPadding(0, dp(6), 0, 0) })
                }
            }

            val buttons = LinearLayout(this@MainActivity).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(0, dp(12), 0, 0)
            }
            if (job.running) {
                buttons.addView(button("Stop", ButtonKind.Danger, icon = R.drawable.ic_stop) { confirmStop(job) })
                buttons.addView(button("Watch") { openRunDetail(job.name, "${job.name} \u00b7 running", null) })
            } else {
                buttons.addView(button("Run now", ButtonKind.Success, icon = R.drawable.ic_play) { act(job.name, "Run") { it.run(job.name) } })
            }
            buttons.addView(View(this@MainActivity), LinearLayout.LayoutParams(0, 1, 1f))
            if (!job.running) buttons.addView(iconButton(R.drawable.ic_edit, "Edit job") { openEditor(job.name) })
            buttons.addView(iconButton(R.drawable.ic_history, "Run history") { history.focusJob(job.name) })
            lateinit var more: View
            more = iconButton(R.drawable.ic_more, "More actions") { showMoreMenu(more, job) }
            buttons.addView(more)
            addView(buttons)
        }
    }

    private fun jobIcon(job: RemoteJob): View {
        val pkg = job.appPackage
        val bitmap = pkg?.let { AppIcons.cached(it) }
        if (pkg != null && bitmap == null) {
            val config = PcConfig.load(this)
            if (config != null) AppIcons.request(pkg, { PcClient(config).appIcon(job.name) }) { ui { renderJobs() } }
        }
        return appIcon(pkg, bitmap)
    }

    private fun phaseSteps(job: RemoteJob): List<Triple<Int, String, Ui.StepState>> {
        val order = listOf(
            Triple("PreConnect", "Connect", R.drawable.ic_link),
            Triple("Pull", "Pull", R.drawable.ic_download),
            Triple("Merge", "Merge", R.drawable.ic_merge),
            Triple("Push", "Push", R.drawable.ic_upload),
        )
        val current = order.indexOfFirst { it.first == (job.phase ?: job.devices.firstOrNull()?.second) }
        return order.mapIndexed { i, (_, label, icon) ->
            Triple(icon, label, when {
                current < 0 -> Ui.StepState.Pending
                i < current -> Ui.StepState.Done
                i == current -> Ui.StepState.Active
                else -> Ui.StepState.Pending
            })
        }
    }

    private fun nextRunText(job: RemoteJob): String = when {
        !job.enabled || job.nextRunAt == null -> "-"
        !job.nextRunAt.isAfter(OffsetDateTime.now()) -> "due now"
        else -> job.nextRunAt.atZoneSameInstant(ZoneId.systemDefault()).format(NEXT_RUN_FORMAT)
    }

    private fun deviceRows(job: RemoteJob): View = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        val bound = jobMeta[job.name]?.devices?.map { it.second } ?: job.devices.map { it.first }
        if (bound.isEmpty()) addView(body("-", Ui.TEXT_PRIMARY, 13f))
        bound.forEach { device ->
            val phase = if (job.running) job.devices.firstOrNull { it.first == device }?.second else null
            val state = deviceStates[device]
            val color = when {
                phase != null -> Ui.ACCENT_FROM
                state == "connected" -> Ui.SUCCESS
                state == "unreachable" || state == "unauthorized" -> Ui.DANGER
                else -> 0xFF5C5870.toInt()
            }
            addView(statusLine(if (phase != null) "$device · ${Protocol.phaseLabel(phase)}" else device, color, Ui.TEXT_PRIMARY))
        }
    }

    private fun triggerText(job: RemoteJob): String {
        val schedule = jobMeta[job.name]?.schedule ?: "-"
        val next = job.nextRunAt
        if (!job.enabled || next == null) return schedule
        val whenText = if (!next.isAfter(OffsetDateTime.now())) "due now" else DateUtils.getRelativeTimeSpanString(
            next.toInstant().toEpochMilli(), System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS,
        ).toString()
        return "$schedule · next $whenText"
    }

    private fun loadMeta() {
        async({ client -> client.jobMetas() to client.devices() }) { result ->
            result.onSuccess { (metas, devices) ->
                jobMeta = metas.associateBy { it.name }
                deviceStates = devices.associate { it.name to it.state }
                renderJobs()
            }
        }
    }

    private fun showMoreMenu(anchor: View, job: RemoteJob) {
        val menu = PopupMenu(this, anchor)
        val meta = jobMeta[job.name]
        var order = 0
        fun add(id: Int, text: String) = menu.menu.add(0, id, order++, text)
        if (!job.running) add(MENU_DRY_RUN, "Dry run")
        add(MENU_LOG, "Latest log")
        if (job.pendingConflicts > 0) add(MENU_CONFLICTS, "Resolve conflicts…")
        if (job.appPackage != null && !job.running && meta != null) {
            meta.devices.forEachIndexed { i, (_, name) ->
                add(MENU_OPEN_APP + i, "Open app on $name")
                add(MENU_CLOSE_APP + i, "Close app and sync $name")
            }
        }
        if (!job.running) add(MENU_SNAPSHOT, "Take snapshot")
        add(MENU_SNAPSHOTS, "Snapshots and rollback")
        if (job.hasCheckpoint && !job.running) add(MENU_DISCARD, "Discard checkpoint")
        if (job.canForcePush && !job.running) add(MENU_FORCE, "Force push (bypass safety check)")
        if (!job.running) add(MENU_REMOVE, "Remove")
        menu.setOnMenuItemClickListener { item ->
            val id = item.itemId
            when {
                id == MENU_DRY_RUN -> act(job.name, "Dry run") { it.dryRun(job.name) }
                id == MENU_LOG -> showLog(job.name)
                id == MENU_CONFLICTS -> showTab(2)
                id in MENU_OPEN_APP until MENU_OPEN_APP + 50 -> meta?.devices?.getOrNull(id - MENU_OPEN_APP)?.let { (deviceId, name) ->
                    async({ c -> c.openApp(c.jobIdOf(job.name), deviceId) }) { r ->
                        r.onSuccess { toast("Opened ${job.name} on $name") }.onFailure { toast(errorText(it)) }
                    }
                }
                id in MENU_CLOSE_APP until MENU_CLOSE_APP + 50 -> meta?.devices?.getOrNull(id - MENU_CLOSE_APP)?.let { (deviceId, name) ->
                    async({ c -> c.closeAppAndSync(c.jobIdOf(job.name), deviceId) }) { r ->
                        r.onSuccess { toast("Closing the app on $name and syncing") }.onFailure { toast(errorText(it)) }
                    }
                }
                id == MENU_SNAPSHOT -> async({ c -> c.createSnapshot(c.jobIdOf(job.name)) }) { r ->
                    r.onSuccess { toast("Creating a snapshot of ${job.name}") }.onFailure { toast(errorText(it)) }
                }
                id == MENU_SNAPSHOTS -> SnapshotsDialog(this, job.name, job.running).show()
                id == MENU_DISCARD -> confirmDiscardCheckpoint(job)
                id == MENU_FORCE -> Dialogs.confirm(
                    this, "Force push", "Push ${job.name} to the devices even though the safety check blocked it?", "Force push", danger = true,
                ) { act(job.name, "Force push") { it.forcePush(job.name) } }
                id == MENU_REMOVE -> Dialogs.confirm(
                    this, "Remove job", "Remove ${job.name}? Its project folder is kept.", "Remove", danger = true,
                ) {
                    async({ c -> c.deleteJob(c.jobIdOf(job.name)) }) { r ->
                        r.onSuccess { toast("${job.name} removed") }.onFailure { toast(errorText(it)) }
                    }
                }
            }
            true
        }
        menu.show()
    }

    private fun openRunDetail(jobName: String, title: String, runId: String?) {
        val config = PcConfig.load(this) ?: return
        RunDetailDialog(this, config, jobName, title, runId).show()
    }

    private fun openEditor(jobName: String?) {
        if (jobName == null) {
            startActivity(Intent(this, JobEditActivity::class.java))
            return
        }
        async({ it.jobIdOf(jobName) }) { result ->
            result.onSuccess { startActivity(Intent(this, JobEditActivity::class.java).putExtra(JobEditActivity.EXTRA_JOB_ID, it)) }
                .onFailure { toast(errorText(it)) }
        }
    }

    private fun renderToolbar() {
        jobsToolbar.removeAllViews()
        jobsToolbar.addView(title("Jobs"), LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f))
        if (PcConfig.load(this) == null) return
        schedulerPaused?.let { paused ->
            jobsToolbar.addView(iconButton(
                if (paused) R.drawable.ic_play_circle else R.drawable.ic_pause_circle,
                if (paused) "Resume scheduler" else "Pause scheduler",
                tint = if (paused) Ui.WARNING else Ui.TEXT_PRIMARY,
            ) {
                async({ it.setSchedulerPaused(!paused) }) { result ->
                    result.onSuccess { loadScheduler() }.onFailure { toast(errorText(it)) }
                }
            })
        }
        jobsToolbar.addView(iconButton(R.drawable.ic_playlist_play, "Run all") {
            Dialogs.confirm(this, "Run every job?", "Starts every enabled job now.", "Run all") {
                async({ it.runAll() }) { result ->
                    result.onSuccess { toast(if (it in 200..299) "Started" else "Run all failed (HTTP $it)") }
                        .onFailure { toast(errorText(it)) }
                }
            }
        })
        jobsToolbar.addView(iconButton(R.drawable.ic_refresh, "Refresh") {
            startFeed()
            loadScheduler()
            loadMeta()
        })
        jobsToolbar.addView(iconButton(R.drawable.ic_add, "New job", filled = true) { openEditor(null) })
    }

    private fun loadScheduler() {
        async({ it.schedulerPaused() }) { result ->
            schedulerPaused = result.getOrNull()
            renderToolbar()
        }
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
        var problems = 0
        fun item(ok: Boolean?, okText: String, problemText: String, actionText: String?, action: (() -> Unit)?) {
            if (!first) setupCard.addView(divider())
            first = false
            if (ok != true) problems++
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

        tabBar.setBadge(4, problems > 0)

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

        browserCard.removeAllViews()
        val browser = rememberedBrowserLabel()
        browserCard.addView(statusLine(
            if (browser != null) "Opens in $browser" else "Asks which browser each time",
            if (browser != null) Ui.SUCCESS else Ui.TEXT_SECONDARY, Ui.TEXT_PRIMARY,
        ))
        if (browser != null) {
            browserCard.addView(View(this).apply { minimumHeight = dp(10) })
            browserCard.addView(button("Clear remembered browser", fill = true) {
                uiPrefs().edit().remove(PREF_BROWSER).apply()
                refreshSetup()
            })
        }

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

    private companion object {
        const val MIN_CARD_WIDTH_DP = 340
        const val STATE_TAB = "tab"
        const val PREF_BROWSER = "browser"
        const val REQUEST_NOTIFICATIONS = 1
        const val MENU_DRY_RUN = 1
        const val MENU_DISCARD = 2
        const val MENU_EDIT = 3
        const val MENU_SNAPSHOTS = 4
        const val MENU_LOG = 5
        const val MENU_CONFLICTS = 6
        const val MENU_SNAPSHOT = 7
        const val MENU_FORCE = 8
        const val MENU_REMOVE = 9
        const val MENU_OPEN_APP = 100
        const val MENU_CLOSE_APP = 200
        const val CHECKPOINT_BLUE = 0xFF4FC3F7.toInt()
        val NEXT_RUN_FORMAT: DateTimeFormatter = DateTimeFormatter.ofLocalizedDateTime(FormatStyle.MEDIUM, FormatStyle.SHORT)
    }
}
