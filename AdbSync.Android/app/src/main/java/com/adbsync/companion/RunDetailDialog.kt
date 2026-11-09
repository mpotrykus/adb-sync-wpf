package com.adbsync.companion

import android.app.Activity
import android.app.AlertDialog
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Handler
import android.os.Looper
import android.text.TextUtils
import android.text.format.Formatter
import android.view.Gravity
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.adbsync.companion.Ui.body
import com.adbsync.companion.Ui.caption
import com.adbsync.companion.Ui.dp
import com.adbsync.companion.Ui.styled
import java.time.Duration
import java.time.LocalDateTime
import java.time.OffsetDateTime
import java.time.format.DateTimeFormatter
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
 * One run's execution graph on the phone: a card per device with its Connect > Pull > Merge > Push steps, and the
 * run log below it. Tapping a device or a step filters the log to it. A live run ([runId] null) polls the PC.
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
    private val live get() = runId == null

    @Volatile private var stopped = false
    private var finished = false
    private var loaded = false
    private var steps: List<RemoteStep> = emptyList()
    private var fullLog = ""
    private var error: String? = null
    private var selectedDevice: String? = null
    private var selectedPhase: String? = null

    private lateinit var graph: LinearLayout
    private lateinit var hint: TextView
    private lateinit var logView: TextView

    fun show() {
        val root = LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(activity.dp(20), activity.dp(4), activity.dp(20), activity.dp(8))
        }
        hint = activity.caption("Loading...").apply { setPadding(0, 0, 0, activity.dp(10)) }
        graph = LinearLayout(activity).apply { orientation = LinearLayout.VERTICAL }
        logView = TextView(activity).apply {
            textSize = 11f
            typeface = Typeface.MONOSPACE
            setTextColor(Ui.TEXT_PRIMARY)
            setTextIsSelectable(true)
            setPadding(0, activity.dp(12), 0, 0)
        }
        root.addView(hint)
        root.addView(graph)
        root.addView(logView)

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
        io.execute {
            var newSteps = steps
            var newLog = fullLog
            var newError: String? = null
            try {
                val client = PcClient(config)
                newSteps = if (runId == null) client.liveSteps(jobName) else client.runSteps(jobName, runId)
                newLog = try {
                    if (runId == null) client.log(jobName) else client.runLog(jobName, runId)
                } catch (e: HttpStatusException) {
                    if (e.code == 404) "No log yet." else "Couldn't load log (HTTP ${e.code})"
                }
            } catch (e: Exception) {
                newError = "Couldn't load run: ${e.javaClass.simpleName}"
            }
            handler.post {
                busy.set(false)
                if (!stopped) apply(newSteps, newLog, newError)
            }
        }
    }

    private fun apply(newSteps: List<RemoteStep>, newLog: String, newError: String?) {
        loaded = true
        error = newError
        if (newError == null) {
            if (live && newSteps.isEmpty() && steps.isNotEmpty()) {
                finished = true
            } else {
                steps = newSteps
                fullLog = newLog
            }
        }
        render()
        if (live && !finished) handler.postDelayed({ load() }, 1_000)
    }

    private fun render() {
        graph.removeAllViews()
        hint.text = when {
            error != null -> error
            finished -> "Run finished - open History for its result"
            !loaded -> "Loading..."
            steps.isEmpty() -> if (live) "Waiting for the run to start..." else "No step data was recorded for this run."
            selectedDevice != null -> "Log filtered to ${selectionLabel()} - tap again to clear"
            else -> "Tap a device or step to filter the log"
        }

        val devices = steps.map { it.device }.distinct()
        devices.forEachIndexed { index, device ->
            graph.addView(deviceCard(device, steps.filter { it.device == device }.sortedBy { phaseOrder(it.phase) }, index))
        }
        logView.text = buildLog()
    }

    private fun selectionLabel() = selectedDevice + (selectedPhase?.let { " · ${phaseTitle(it)}" } ?: "")

    private fun deviceCard(device: String, deviceSteps: List<RemoteStep>, index: Int): View {
        val deviceSelected = selectedDevice == device
        val card = LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(activity.dp(12), activity.dp(10), activity.dp(12), activity.dp(10))
            background = GradientDrawable().apply {
                setColor(Ui.SURFACE)
                cornerRadius = activity.dp(8).toFloat()
                if (deviceSelected && selectedPhase == null) setStroke(activity.dp(1), Ui.ACCENT)
            }
            layoutParams = LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply {
                if (index > 0) topMargin = activity.dp(8)
            }
            isClickable = true
            setOnClickListener { select(device, null) }
        }
        card.addView(activity.body(device, Ui.TEXT_PRIMARY, 13f).apply {
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
        })

        val row = LinearLayout(activity).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(0, activity.dp(8), 0, 0)
        }
        deviceSteps.forEachIndexed { i, step ->
            if (i > 0) {
                row.addView(TextView(activity).apply {
                    text = "›"
                    textSize = 16f
                    setTextColor(Ui.DIVIDER or 0x40000000)
                    gravity = Gravity.CENTER
                    layoutParams = LinearLayout.LayoutParams(WRAP_CONTENT, WRAP_CONTENT).apply { topMargin = activity.dp(2) }
                })
            }
            row.addView(stepColumn(step))
        }
        card.addView(row)
        return card
    }

    private fun stepColumn(step: RemoteStep): View {
        val color = statusColor(step.status)
        val selected = selectedDevice == step.device && selectedPhase == step.phase
        return LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(activity.dp(2), activity.dp(6), activity.dp(2), activity.dp(6))
            background = GradientDrawable().apply {
                setColor(if (step.status == "Pending" || step.status == "Skipped") 0x00000000 else (color and 0x00FFFFFF) or 0x1F000000)
                cornerRadius = activity.dp(6).toFloat()
                if (selected) setStroke(activity.dp(2), Ui.ACCENT)
            }
            layoutParams = LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f)
            isClickable = true
            setOnClickListener { select(step.device, step.phase) }
            addView(TextView(activity).apply {
                text = statusGlyph(step.status)
                textSize = 16f
                setTextColor(color)
                gravity = Gravity.CENTER
            })
            addView(activity.body(phaseTitle(step.phase), Ui.TEXT_PRIMARY, 11f).apply { gravity = Gravity.CENTER })
            addView(activity.body(detailText(step), Ui.TEXT_SECONDARY, 10f).apply {
                gravity = Gravity.CENTER
                maxLines = 3
                ellipsize = TextUtils.TruncateAt.END
            })
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
        render()
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

    private fun detailText(step: RemoteStep): String {
        val elapsed = step.startedAt?.let {
            val seconds = Duration.between(it, step.endedAt ?: OffsetDateTime.now()).seconds.coerceAtLeast(0)
            if (seconds >= 60) "${seconds / 60}m ${seconds % 60}s" else "${seconds}s"
        }
        when (step.status) {
            "Pending" -> return "waiting"
            "Skipped" -> return "skipped"
            "Waiting" -> return "waiting for device"
            "Running" -> return elapsed ?: "0s"
        }
        val parts = ArrayList<String>()
        val touched = step.filesCopied + step.filesDeleted
        when (step.phase) {
            "Pull", "Push" -> {
                parts.add("$touched file${if (touched == 1) "" else "s"}")
                if (step.bytesCopied > 0) parts.add(Formatter.formatShortFileSize(activity, step.bytesCopied))
            }
            "Merge" -> {
                parts.add("$touched changed")
                if (step.conflicts > 0) parts.add("${step.conflicts} conflict${if (step.conflicts == 1) "" else "s"}")
            }
        }
        if (step.errors > 0) parts.add("${step.errors} error${if (step.errors == 1) "" else "s"}")
        if (elapsed != null) parts.add(elapsed) else if (step.status == "Cancelled") parts.add("stopped")
        return if (parts.isEmpty()) step.status.lowercase() else parts.joinToString(" · ")
    }

    private fun phaseTitle(phase: String) = if (phase == "PreConnect") "Connect" else phase

    private fun phaseOrder(phase: String) = when (phase) {
        "PreConnect" -> 0
        "Pull" -> 1
        "Merge" -> 2
        "Push" -> 3
        else -> 4
    }

    private fun statusGlyph(status: String) = when (status) {
        "Pending" -> "○"
        "Waiting" -> "…"
        "Running" -> "●"
        "Succeeded" -> "✓"
        "Failed" -> "✕"
        "Skipped" -> "–"
        "Cancelled" -> "■"
        else -> ""
    }

    private fun statusColor(status: String) = when (status) {
        "Running" -> Ui.ACCENT
        "Waiting", "Cancelled" -> Ui.WARNING
        "Succeeded" -> Ui.SUCCESS
        "Failed" -> Ui.DANGER
        else -> Ui.TEXT_SECONDARY
    }
}
