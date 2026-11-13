package com.adbsync.companion

import android.app.Activity
import android.app.AlertDialog
import android.view.Gravity
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.LinearLayout
import android.widget.ScrollView
import com.adbsync.companion.Ui.ButtonKind
import com.adbsync.companion.Ui.body
import com.adbsync.companion.Ui.button
import com.adbsync.companion.Ui.caption
import com.adbsync.companion.Ui.chip
import com.adbsync.companion.Ui.divider
import com.adbsync.companion.Ui.dp
import com.adbsync.companion.Ui.indeterminateBar
import com.adbsync.companion.Ui.styled
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

/** A job's manual snapshots and automatic rollback points: take one, or restore the job's files to one. */
class SnapshotsDialog(private val activity: Activity, private val jobName: String, private val running: Boolean) {
    private val list = LinearLayout(activity).apply { orientation = LinearLayout.VERTICAL }
    private val progress = activity.caption("").apply { visibility = View.GONE; setPadding(0, activity.dp(8), 0, 0) }
    private val bar = activity.indeterminateBar().apply { visibility = View.GONE }
    private lateinit var dialog: AlertDialog
    private lateinit var jobId: String
    private var busy = false

    fun show() {
        val root = LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(activity.dp(20), activity.dp(4), activity.dp(20), activity.dp(8))
            addView(activity.caption(
                if (running) "The job is running; wait for it to finish before taking or restoring a snapshot."
                else "A snapshot captures every device's files and the server copy. Restoring one puts the job's files back as they were then.",
            ))
            addView(bar)
            addView(progress)
            addView(list)
        }
        dialog = AlertDialog.Builder(activity)
            .setTitle("$jobName snapshots")
            .setView(ScrollView(activity).apply { addView(root) })
            .setPositiveButton("Take snapshot", null)
            .setNegativeButton("Close", null)
            .create()
            .styled()
        dialog.show()
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).apply {
            isEnabled = !running
            setOnClickListener { take() }
        }
        Backend.async(activity, { it.jobIdOf(jobName) }) { result ->
            result.onSuccess {
                jobId = it
                load()
            }.onFailure { list.addView(activity.body(errorText(it), Ui.DANGER, 13f)) }
        }
    }

    private fun load() {
        Backend.async(activity, { it.snapshots(jobId) }) { result ->
            list.removeAllViews()
            result.onSuccess { items ->
                if (items.isEmpty()) {
                    list.addView(activity.caption("No snapshots yet.").apply { setPadding(0, activity.dp(12), 0, 0) })
                }
                items.sortedByDescending { it.createdAt }.forEach { list.addView(row(it)) }
            }.onFailure { list.addView(activity.body(errorText(it), Ui.DANGER, 13f)) }
        }
    }

    private fun row(snapshot: Snapshot): View = LinearLayout(activity).apply {
        orientation = LinearLayout.VERTICAL
        addView(activity.divider())
        addView(LinearLayout(activity).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            addView(activity.body(
                snapshot.createdAt.atZoneSameInstant(ZoneId.systemDefault()).format(FORMAT), Ui.TEXT_PRIMARY, 14f,
            ), LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f))
            addView(activity.chip(if (snapshot.rollbackPoint) "Rollback point" else "Manual", if (snapshot.rollbackPoint) Ui.WARNING else Ui.ACCENT_FROM))
        })
        addView(activity.caption(snapshot.devices.joinToString(", ").ifEmpty { "No devices" }))
        addView(activity.button("Restore", ButtonKind.Danger) { confirmRestore(snapshot) }.apply {
            isEnabled = !running
            alpha = if (running) 0.4f else 1f
            (layoutParams as LinearLayout.LayoutParams).topMargin = activity.dp(8)
        })
    }

    private fun confirmRestore(snapshot: Snapshot) {
        val when_ = snapshot.createdAt.atZoneSameInstant(ZoneId.systemDefault()).format(FORMAT)
        Dialogs.confirm(
            activity, "Restore $jobName?",
            "Every device and the server copy go back to how they were on $when_. Changes made since then are lost" +
                (if (snapshot.rollbackPoint) "." else ", unless a rollback point was taken first."),
            "Restore", danger = true,
        ) { run("Restoring…", "Restored") { it.restoreSnapshot(jobId, snapshot.id) } }
    }

    private fun take() = run("Taking snapshot…", "Snapshot saved") { it.createSnapshot(jobId) }

    private fun run(startText: String, doneText: String, start: (PcClient) -> String) {
        if (busy) return
        busy = true
        bar.visibility = View.VISIBLE
        progress.visibility = View.VISIBLE
        progress.text = startText
        Backend.async(activity, { client ->
            val outcome = client.awaitOperation(start(client)) { message ->
                (activity as? android.app.Activity)?.runOnUiThread { progress.text = message }
            }
            outcome
        }) { result ->
            busy = false
            bar.visibility = View.GONE
            result.onSuccess { outcome ->
                progress.text = if (outcome.succeeded) doneText else outcome.message ?: "That didn't work."
                progress.setTextColor(if (outcome.succeeded) Ui.SUCCESS else Ui.DANGER)
                if (outcome.succeeded) load()
            }.onFailure {
                progress.text = errorText(it)
                progress.setTextColor(Ui.DANGER)
            }
        }
    }

    private companion object {
        val FORMAT: DateTimeFormatter = DateTimeFormatter.ofLocalizedDateTime(FormatStyle.MEDIUM, FormatStyle.SHORT)
    }
}
