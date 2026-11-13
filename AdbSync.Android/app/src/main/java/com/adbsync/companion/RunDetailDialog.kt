package com.adbsync.companion

import android.app.Activity
import android.app.AlertDialog
import android.content.res.ColorStateList
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Handler
import android.os.Looper
import android.text.Editable
import android.text.InputType
import android.text.TextUtils
import android.text.TextWatcher
import android.view.Gravity
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.HorizontalScrollView
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView
import com.adbsync.companion.Ui.body
import com.adbsync.companion.Ui.button
import com.adbsync.companion.Ui.caption
import com.adbsync.companion.Ui.chip
import com.adbsync.companion.Ui.dp
import com.adbsync.companion.Ui.loader
import com.adbsync.companion.Ui.statTile
import com.adbsync.companion.Ui.styled
import org.json.JSONObject
import java.time.Duration
import java.time.LocalDateTime
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/** Narrows a captured run log to one device (and optionally one step's time window); mirrors AdbSync.Core's RunLogFilter. */
object RunLogFilter {
    private const val TIMESTAMP_LENGTH = 23
    private const val TOLERANCE_MS = 50L
    private val format = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss.SSS")

    fun filter(log: String, device: String, from: OffsetDateTime?, to: OffsetDateTime?): String {
        val needle = "'$device'"
        // Log timestamps are in the PC's local time, which is the offset the step times were recorded with.
        val start = from?.toLocalDateTime()?.minusNanos(TOLERANCE_MS * 1_000_000)
        val end = to?.toLocalDateTime()?.plusNanos(TOLERANCE_MS * 1_000_000)
        val kept = ArrayList<String>()
        var include = false
        for (line in log.lineSequence()) {
            val timestamp = parseTimestamp(line)
            if (timestamp != null) {
                include = line.contains(needle, ignoreCase = true) &&
                    (start == null || !timestamp.isBefore(start)) &&
                    (end == null || !timestamp.isAfter(end))
            }
            if (include) kept.add(line)
        }
        return kept.joinToString("\n")
    }

    private fun parseTimestamp(line: String): LocalDateTime? {
        if (line.length <= TIMESTAMP_LENGTH + 1 || line[0] != '[' || line[TIMESTAMP_LENGTH + 1] != ']') return null
        return try {
            LocalDateTime.parse(line.substring(1, TIMESTAMP_LENGTH + 1), format)
        } catch (e: Exception) {
            null
        }
    }
}

/**
 * One run on the phone, laid out like the web's run dialog: status and mode chips, a stat grid, the trigger line, the
 * failure message, then Steps / Files / Log tabs. Tapping a device or step filters the files and log to it. A live
 * run ([runId] null) polls the server until it finishes.
 */
class RunDetailDialog(
    private val activity: Activity,
    private val config: PcConfig,
    private val jobName: String,
    private val title: String,
    private val runId: String?,
) {
    private val handler = Handler(Looper.getMainLooper())
    private val io = Executors.newSingleThreadExecutor()
    private val busy = AtomicBoolean(false)

    @Volatile private var stopped = false
    private var resolvedId: String? = runId
    private var loaded = false
    private var run: RemoteRun? = null
    private var mode = "normal"
    private var trigger = ""
    private var errorCode: String? = null
    private var steps: List<RemoteStep> = emptyList()
    private var fullLog = ""
    private var files: RunFiles? = null
    private var filesFailed = false
    private var error: String? = null
    private var selectedDevice: String? = null
    private var selectedPhase: String? = null
    private var tab = Tab.Steps
    private var fileFilter = ""
    private var showPulls = false

    private lateinit var header: LinearLayout
    private lateinit var tabBar: LinearLayout
    private lateinit var content: LinearLayout
    private lateinit var spinner: View
    private var fileList: LinearLayout? = null
    private var fileCount: TextView? = null

    private enum class Tab(val label: String) { Steps("Steps"), Files("Files"), Log("Log") }

    private class Snapshot(
        val id: String?,
        val run: RemoteRun?,
        val mode: String,
        val trigger: String,
        val errorCode: String?,
        val steps: List<RemoteStep>,
        val log: String,
        val files: RunFiles?,
        val filesFailed: Boolean,
    )

    private val live get() = run?.outcome.let { it == null || it == "Queued" || it == "Running" }

    fun show() {
        val root = LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(activity.dp(20), activity.dp(4), activity.dp(20), activity.dp(8))
        }
        spinner = activity.loader()
        header = LinearLayout(activity).apply { orientation = LinearLayout.VERTICAL }
        tabBar = LinearLayout(activity).apply {
            orientation = LinearLayout.HORIZONTAL
            visibility = View.GONE
        }
        content = LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, activity.dp(14), 0, 0)
        }
        root.addView(spinner)
        root.addView(header)
        root.addView(tabBar)
        root.addView(content)

        AlertDialog.Builder(activity)
            .setTitle(title)
            .setView(ScrollView(activity).apply { addView(root) })
            .setPositiveButton("Close", null)
            .create()
            .styled()
            .apply {
                setOnDismissListener {
                    stopped = true
                    handler.removeCallbacksAndMessages(null)
                    io.shutdown()
                }
                show()
            }
        load()
    }

    private fun load() {
        if (stopped || !busy.compareAndSet(false, true)) return
        val wasLive = live
        val haveFiles = files != null
        io.execute {
            var snapshot: Snapshot? = null
            var newError: String? = null
            try {
                val client = PcClient(config)
                val id = resolvedId ?: client.activeRunId(jobName)
                snapshot = if (id == null) {
                    Snapshot(null, null, "normal", "", null, emptyList(), "", null, false)
                } else {
                    val detail = JSONObject(client.getText("/api/v1/runs/$id"))
                    val runJson = detail.getJSONObject("run")
                    val parsed = RemoteRun.parse(runJson)
                    val running = parsed.outcome == "Queued" || parsed.outcome == "Running"
                    val array = detail.optJSONArray("steps")
                    val parsedSteps = if (array == null) emptyList() else (0 until array.length()).map { RemoteStep.parse(array.getJSONObject(it)) }
                    val log = try {
                        client.getText("/api/v1/runs/$id/log" + if (running) "?tail=400" else "")
                    } catch (e: HttpStatusException) {
                        if (e.code == 404) "No log yet." else "Couldn't load log (HTTP ${e.code})"
                    }
                    var newFiles: RunFiles? = null
                    var failed = false
                    if (wasLive || running || !haveFiles) {
                        newFiles = try {
                            client.runFiles(id)
                        } catch (e: Exception) {
                            failed = true
                            null
                        }
                    }
                    Snapshot(
                        id, parsed, runJson.optString("mode", "normal"), runJson.optString("trigger"),
                        runJson.optStringOrNull("errorCode"), parsedSteps, log, newFiles, failed,
                    )
                }
            } catch (e: Exception) {
                newError = "Couldn't load run: ${e.javaClass.simpleName}"
            }
            handler.post {
                busy.set(false)
                if (!stopped) apply(snapshot, newError)
            }
        }
    }

    private fun apply(snapshot: Snapshot?, newError: String?) {
        loaded = true
        spinner.visibility = View.GONE
        error = newError
        if (snapshot != null) {
            resolvedId = snapshot.id
            run = snapshot.run
            mode = snapshot.mode
            trigger = snapshot.trigger
            errorCode = snapshot.errorCode
            steps = snapshot.steps
            fullLog = snapshot.log
            if (snapshot.files != null) {
                files = snapshot.files
                filesFailed = false
            } else if (snapshot.filesFailed && files == null) {
                filesFailed = true
            }
        }
        render()
        if (live) handler.postDelayed({ load() }, 3_000)
    }

    private fun render() {
        renderHeader()
        renderTabs()
        val list = fileList
        if (tab == Tab.Files && list != null && files?.items?.isNotEmpty() == true) renderFileList() else renderContent()
    }

    private fun renderHeader() {
        header.removeAllViews()
        val current = run
        if (error != null) {
            header.addView(alert(error!!, Ui.DANGER))
            return
        }
        if (current == null) {
            header.addView(activity.caption("Waiting for the run to start..."))
            return
        }

        val color = when {
            live -> Ui.ACCENT_FROM
            current.failed -> Ui.DANGER
            current.skipped -> Ui.WARNING
            else -> Ui.SUCCESS
        }
        header.addView(LinearLayout(activity).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            addView(activity.chip(if (live) current.outcome else current.outcomeText(), color))
            if (mode != "normal") addView(activity.chip(Fmt.words(mode), Ui.TEXT_SECONDARY))
        })

        val started = current.startedAt.atZoneSameInstant(ZoneId.systemDefault()).format(STARTED)
        val tiles = listOf(
            activity.statTile(started, "Started", Ui.ACCENT_FROM).apply { (getChildAt(0) as TextView).textSize = 15f },
            activity.statTile(
                Fmt.duration(current.startedAt, if (live) OffsetDateTime.now() else current.completedAt), "Duration", 0xFF38BDF8.toInt(),
            ),
            activity.statTile("${current.filesCopied} / ${current.filesDeleted}", "Copied / deleted", Ui.SUCCESS),
            activity.statTile(Fmt.bytes(activity, current.bytesCopied), "Transferred", 0xFF38BDF8.toInt()),
        )
        header.addView(LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            layoutParams = LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply { topMargin = activity.dp(12) }
            tiles.chunked(2).forEachIndexed { rowIndex, row ->
                addView(LinearLayout(activity).apply {
                    orientation = LinearLayout.HORIZONTAL
                    isBaselineAligned = false
                    row.forEachIndexed { i, tile ->
                        addView(tile, LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f).apply { if (i == 0) marginEnd = activity.dp(8) })
                    }
                }, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply { if (rowIndex > 0) topMargin = activity.dp(8) })
            }
        })

        val errors = "${current.errorCount} error${if (current.errorCount == 1) "" else "s"}"
        header.addView(activity.caption(if (trigger.isEmpty()) errors else "Trigger: ${Fmt.words(trigger)} · $errors")
            .apply { setPadding(0, activity.dp(10), 0, 0) })

        current.errorMessage?.takeIf { it.isNotEmpty() }?.let {
            header.addView(alert(it + (errorCode?.let { code -> " ($code)" } ?: ""), if (current.failed) Ui.DANGER else Ui.WARNING))
        }
    }

    private fun alert(text: String, color: Int) = LinearLayout(activity).apply {
        background = GradientDrawable().apply {
            setColor((color and 0x00FFFFFF) or (0x1F shl 24))
            setStroke(activity.dp(1), (color and 0x00FFFFFF) or (0x59 shl 24))
            cornerRadius = activity.dp(8).toFloat()
        }
        setPadding(activity.dp(12), activity.dp(10), activity.dp(12), activity.dp(10))
        layoutParams = LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply { topMargin = activity.dp(12) }
        addView(activity.body(text, color, 13f))
    }

    private fun renderTabs() {
        tabBar.removeAllViews()
        if (run == null || error != null) {
            tabBar.visibility = View.GONE
            return
        }
        tabBar.visibility = View.VISIBLE
        tabBar.setPadding(0, activity.dp(14), 0, 0)
        Tab.entries.forEach { entry ->
            val selected = entry == tab
            val label = if (entry == Tab.Files && files != null) "Files (${files!!.items.size})" else entry.label
            tabBar.addView(LinearLayout(activity).apply {
                orientation = LinearLayout.VERTICAL
                isClickable = true
                setOnClickListener {
                    tab = entry
                    renderTabs()
                    renderContent()
                }
                addView(TextView(activity).apply {
                    text = label
                    textSize = 13f
                    typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                    gravity = Gravity.CENTER
                    setTextColor(if (selected) Ui.ACCENT_FROM else Ui.TEXT_SECONDARY)
                    setPadding(0, activity.dp(10), 0, activity.dp(10))
                })
                addView(View(activity).apply { setBackgroundColor(if (selected) Ui.ACCENT_FROM else Ui.DIVIDER) },
                    LinearLayout.LayoutParams(MATCH_PARENT, activity.dp(if (selected) 2 else 1)))
            }, LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f))
        }
    }

    private fun renderContent() {
        content.removeAllViews()
        fileList = null
        fileCount = null
        if (run == null || error != null) return
        when (tab) {
            Tab.Steps -> renderSteps()
            Tab.Files -> renderFiles()
            Tab.Log -> content.addView(TextView(activity).apply {
                text = buildLog()
                textSize = 11f
                typeface = Typeface.MONOSPACE
                setTextColor(Ui.TEXT_PRIMARY)
                setTextIsSelectable(true)
            })
        }
    }

    private fun renderSteps() {
        if (steps.isEmpty()) {
            content.addView(activity.caption(if (live) "No steps recorded yet." else "No step data was recorded for this run."))
            return
        }
        content.addView(activity.caption(
            if (selectedDevice != null) "Files and log filtered to ${selectionLabel()} - tap again to clear"
            else "Tap a device or step to filter the files and log",
        ).apply { setPadding(0, 0, 0, activity.dp(10)) })
        steps.map { it.device }.distinct().forEachIndexed { index, device ->
            content.addView(deviceCard(device, steps.filter { it.device == device }.sortedBy { phaseOrder(it.phase) }, index))
        }
    }

    private fun renderFiles() {
        val current = files
        if (current == null) {
            if (filesFailed) {
                content.addView(activity.button("Couldn't load files - retry") {
                    filesFailed = false
                    renderContent()
                    load()
                })
            } else {
                content.addView(activity.loader(32))
            }
            return
        }
        if (current.items.isEmpty()) {
            content.addView(activity.caption(
                when {
                    live -> "Nothing recorded yet."
                    current.retained -> "No file changes."
                    else -> "No file changes recorded for this run."
                },
            ))
            return
        }

        content.addView(EditText(activity).apply {
            hint = "Filter"
            setText(fileFilter)
            setHintTextColor(Ui.TEXT_SECONDARY)
            setTextColor(Ui.TEXT_PRIMARY)
            textSize = 14f
            inputType = InputType.TYPE_CLASS_TEXT
            isSingleLine = true
            addTextChangedListener(object : TextWatcher {
                override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) = Unit
                override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) = Unit
                override fun afterTextChanged(s: Editable?) {
                    fileFilter = s?.toString().orEmpty()
                    renderFileList()
                }
            })
        })
        content.addView(Switch(activity).apply {
            text = "Show pulls into staging"
            textSize = 13f
            setTextColor(Ui.TEXT_PRIMARY)
            isChecked = showPulls
            setPadding(0, activity.dp(8), 0, activity.dp(8))
            setOnCheckedChangeListener { _, checked ->
                showPulls = checked
                renderFileList()
            }
        })
        fileCount = activity.body("", Ui.TEXT_PRIMARY, 13f).apply { typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL) }
        content.addView(fileCount)
        fileList = LinearLayout(activity).apply { orientation = LinearLayout.VERTICAL }
        content.addView(fileList)
        renderFileList()
    }

    private fun renderFileList() {
        val list = fileList ?: return
        val all = files?.items ?: return
        list.removeAllViews()
        val needle = fileFilter.trim()
        val shown = all.filter { file ->
            (showPulls || file.phase != "pull") &&
                (selectedDevice == null || file.device == selectedDevice) &&
                (selectedPhase == null || file.phase.equals(selectedPhase.orEmpty().let { phaseKey(it) }, ignoreCase = true)) &&
                (needle.isEmpty() || file.path.contains(needle, ignoreCase = true) || file.action.contains(needle, ignoreCase = true))
        }
        fileCount?.text = "${shown.size} file${if (shown.size == 1) "" else "s"}" + (selectedDevice?.let { " on $it" } ?: "")
        shown.take(MAX_FILES).forEach { file ->
            val color = when (file.action) {
                "deleted" -> Ui.WARNING
                "failed", "error" -> Ui.DANGER
                else -> Ui.SUCCESS
            }
            list.addView(LinearLayout(activity).apply {
                orientation = LinearLayout.VERTICAL
                setPadding(0, activity.dp(8), 0, 0)
                addView(activity.body(file.path, Ui.TEXT_PRIMARY, 12f).apply { typeface = Typeface.MONOSPACE })
                file.detail?.takeIf { it.isNotEmpty() }?.let { addView(activity.caption(it)) }
                addView(activity.body(
                    listOfNotNull(
                        Fmt.words(file.action), file.phase?.let { Fmt.words(it) }, file.device,
                        file.size?.let { Fmt.bytes(activity, it) },
                    ).joinToString(" · "),
                    color, 11f,
                ))
            })
        }
        if (shown.size > MAX_FILES) list.addView(activity.caption("... and ${shown.size - MAX_FILES} more").apply { setPadding(0, activity.dp(8), 0, 0) })
    }

    private fun selectionLabel() = selectedDevice + (selectedPhase?.let { " · ${phaseTitle(it)}" } ?: "")

    private fun deviceCard(device: String, deviceSteps: List<RemoteStep>, index: Int): View {
        val card = LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(activity.dp(12), activity.dp(12), activity.dp(12), activity.dp(12))
            background = GradientDrawable().apply {
                setColor(0x00000000)
                setStroke(activity.dp(1), if (selectedDevice == device && selectedPhase == null) Ui.ACCENT else 0x29FFFFFF)
                cornerRadius = activity.dp(8).toFloat()
            }
            layoutParams = LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply {
                if (index > 0) topMargin = activity.dp(12)
            }
            isClickable = true
            setOnClickListener { select(device, null) }
        }
        card.addView(activity.body(device, Ui.TEXT_PRIMARY, 14f).apply {
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            gravity = Gravity.CENTER
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
            setPadding(0, 0, 0, activity.dp(10))
        })

        val row = LinearLayout(activity).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(activity.dp(2), activity.dp(2), activity.dp(2), activity.dp(2))
        }
        deviceSteps.forEachIndexed { i, step ->
            if (i > 0) row.addView(link(deviceSteps[i - 1].status))
            row.addView(stepTile(step))
        }
        card.addView(HorizontalScrollView(activity).apply {
            isHorizontalScrollBarEnabled = false
            isFillViewport = true
            addView(row, FrameLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT))
        })
        return card
    }

    private fun link(previous: String) = View(activity).apply {
        background = when (previous) {
            "Succeeded" -> GradientDrawable().apply { setColor(Ui.SUCCESS); cornerRadius = activity.dp(1).toFloat() }
            "Running" -> GradientDrawable(GradientDrawable.Orientation.LEFT_RIGHT, intArrayOf(Ui.ACCENT_FROM, 0x1FFFFFFF))
            else -> GradientDrawable().apply { setColor(0x1FFFFFFF); cornerRadius = activity.dp(1).toFloat() }
        }
        layoutParams = LinearLayout.LayoutParams(activity.dp(20), activity.dp(2)).apply { gravity = Gravity.CENTER_VERTICAL }
    }

    private fun stepTile(step: RemoteStep): View {
        val (color, border, fill) = when (step.status) {
            "Succeeded" -> Triple(Ui.SUCCESS, Ui.SUCCESS, 0x1F5BD98A)
            "Running" -> Triple(0xFFFFFFFF.toInt(), Ui.ACCENT_FROM, 0x477C5CFF)
            "Failed" -> Triple(0xFFD1453B.toInt(), 0xFFD1453B.toInt(), 0x1FD1453B)
            "Waiting", "Cancelled" -> Triple(0xFFD98E04.toInt(), 0xFFD98E04.toInt(), 0x1FD98E04)
            else -> Triple(0xFF6F6A86.toInt(), 0x29FFFFFF, 0x08FFFFFF)
        }
        val selected = selectedDevice == step.device && selectedPhase == step.phase
        return LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(activity.dp(8), activity.dp(10), activity.dp(8), activity.dp(10))
            background = GradientDrawable().apply {
                setColor(fill)
                cornerRadius = activity.dp(16).toFloat()
                setStroke(activity.dp(if (selected) 3 else 2), if (selected) Ui.ACCENT else border)
            }
            layoutParams = LinearLayout.LayoutParams(activity.dp(TILE_SIZE), activity.dp(TILE_SIZE))
            isClickable = true
            setOnClickListener { select(step.device, step.phase) }
            addView(LinearLayout(activity).apply {
                orientation = LinearLayout.VERTICAL
                gravity = Gravity.CENTER
                addView(ImageView(activity).apply {
                    setImageResource(stepIcon(step))
                    imageTintList = ColorStateList.valueOf(color)
                }, LinearLayout.LayoutParams(activity.dp(32), activity.dp(32)))
                addView(activity.body(phaseTitle(step.phase), color, 13f).apply {
                    typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                    gravity = Gravity.CENTER
                    setPadding(0, activity.dp(4), 0, 0)
                })
            }, LinearLayout.LayoutParams(MATCH_PARENT, 0, 1f))
            addView(stepDetail(step, color))
        }
    }

    private fun stepDetail(step: RemoteStep, color: Int) = LinearLayout(activity).apply {
        orientation = LinearLayout.VERTICAL
        background = GradientDrawable().apply {
            setColor((color and 0x00FFFFFF) or 0x1A000000)
            cornerRadius = activity.dp(8).toFloat()
        }
        setPadding(activity.dp(10), activity.dp(8), activity.dp(10), activity.dp(8))
        minimumHeight = activity.dp(88)
        layoutParams = LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply { topMargin = activity.dp(6) }
        alpha = 0.85f

        val none = "-"
        val started = step.startedAt != null
        val touched = step.filesCopied + step.filesDeleted
        val items = ArrayList<Pair<Int, String>>()
        if (step.phase in setOf("Pull", "Push", "Merge")) items.add(R.drawable.ic_file to if (started && touched > 0) "$touched" else none)
        if (step.phase == "Pull" || step.phase == "Push") {
            items.add(R.drawable.ic_storage to if (step.bytesCopied > 0) Fmt.bytes(activity, step.bytesCopied) else none)
        }
        if (step.phase == "Merge") items.add(R.drawable.ic_compare to if (step.conflicts > 0) "${step.conflicts}" else none)
        items.add(R.drawable.ic_error_outline to if (step.errors > 0) "${step.errors}" else none)
        items.add(R.drawable.ic_timer to (step.startedAt?.let { Fmt.duration(it, step.endedAt ?: OffsetDateTime.now()) } ?: none))

        items.forEachIndexed { i, (icon, text) ->
            addView(LinearLayout(activity).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                addView(ImageView(activity).apply {
                    setImageResource(icon)
                    imageTintList = ColorStateList.valueOf(color)
                }, LinearLayout.LayoutParams(activity.dp(14), activity.dp(14)).apply { marginEnd = activity.dp(8) })
                addView(TextView(activity).apply {
                    this.text = text
                    textSize = 11f
                    setTextColor(color)
                    maxLines = 1
                })
            }, LinearLayout.LayoutParams(WRAP_CONTENT, WRAP_CONTENT).apply {
                if (i > 0) topMargin = activity.dp(4)
                gravity = Gravity.CENTER_HORIZONTAL
            })
        }
    }

    private fun stepIcon(step: RemoteStep) = when (step.status) {
        "Succeeded" -> R.drawable.ic_check
        "Failed" -> R.drawable.ic_close
        "Cancelled" -> R.drawable.ic_stop
        "Skipped" -> R.drawable.ic_remove
        else -> when (step.phase) {
            "Pull" -> R.drawable.ic_download
            "Snapshot" -> R.drawable.ic_save
            "Merge" -> R.drawable.ic_merge
            "Push" -> R.drawable.ic_upload
            else -> R.drawable.ic_link
        }
    }

    private fun select(device: String, phase: String?) {
        if (selectedDevice == device && selectedPhase == phase) {
            selectedDevice = null
            selectedPhase = null
        } else {
            selectedDevice = device
            selectedPhase = phase
        }
        renderContent()
    }

    private fun buildLog(): String {
        val device = selectedDevice ?: return fullLog
        var from: OffsetDateTime? = null
        var to: OffsetDateTime? = null
        selectedPhase?.let { phase ->
            val step = steps.firstOrNull { it.device == device && it.phase == phase }
            from = step?.startedAt ?: return "(${phaseTitle(phase)} on $device didn't run - nothing to show)"
            to = step.endedAt
        }
        val filtered = RunLogFilter.filter(fullLog, device, from, to)
        return filtered.ifEmpty { "(no log lines matched ${selectionLabel()})" }
    }

    private fun phaseTitle(phase: String) = when (phase) {
        "PreConnect" -> "Connect"
        "WaitingForDevice" -> "Waiting for device"
        "WaitingForAppClose" -> "Waiting for app"
        "Snapshot" -> "Rollback point"
        else -> Fmt.words(phase)
    }

    private fun phaseKey(phase: String) = phase.lowercase()

    private fun phaseOrder(phase: String) = when (phase) {
        "PreConnect", "WaitingForDevice", "WaitingForAppClose" -> 0
        "Pull" -> 1
        "Snapshot" -> 2
        "Merge" -> 3
        "Push" -> 4
        else -> 5
    }

    private companion object {
        const val MAX_FILES = 300
        const val TILE_SIZE = 180
        val STARTED: DateTimeFormatter = DateTimeFormatter.ofLocalizedDateTime(FormatStyle.MEDIUM, FormatStyle.SHORT)
    }
}
