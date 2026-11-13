package com.adbsync.companion

import android.app.AlertDialog
import android.graphics.BitmapFactory
import android.view.Gravity
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import android.text.format.DateUtils
import android.text.format.Formatter
import com.adbsync.companion.Ui.ButtonKind
import com.adbsync.companion.Ui.body
import com.adbsync.companion.Ui.button
import com.adbsync.companion.Ui.caption
import com.adbsync.companion.Ui.card
import com.adbsync.companion.Ui.dp
import com.adbsync.companion.Ui.indeterminateBar
import com.adbsync.companion.Ui.page
import com.adbsync.companion.Ui.sectionLabel
import com.adbsync.companion.Ui.styled
import com.adbsync.companion.Ui.subSectionLabel
import com.adbsync.companion.Ui.title
import org.json.JSONObject
import java.time.OffsetDateTime
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

/** Files that changed on a device and on the server at once, so they can be settled from the phone. */
class ConflictsScreen(host: MainActivity) : Screen(host) {
    private val thumbs: ExecutorService = Executors.newFixedThreadPool(2)
    private val content = LinearLayout(host).apply { orientation = LinearLayout.VERTICAL }
    override val view = host.page(content)

    private var conflicts: List<RemoteConflict> = emptyList()
    private var loaded = false
    private var busy = false
    private var stale = true

    init {
        render()
    }

    override fun onShow() {
        load()
    }

    fun conflictsChanged() {
        stale = true
        if (visible) load()
    }

    fun shutdown() = thumbs.shutdownNow()

    private fun load() {
        stale = false
        async({ it.conflicts() }) { result ->
            result.onSuccess {
                conflicts = it
                loaded = true
                render()
                host.setConflictCount(it.size)
            }.onFailure { toast(errorText(it)) }
        }
    }

    private fun resolve(conflict: RemoteConflict, choice: ConflictChoice) = perform { client ->
        val (code, message) = client.resolveConflict(conflict.id, choice)
        if (code in 200..299) null else failureText(code, message)
    }

    private fun resolveAll(choice: ConflictChoice) = perform { client ->
        val (code, text) = client.resolveAllConflicts(choice)
        if (code !in 200..299) return@perform failureText(code, text)
        val failed = JSONObject(text).optJSONArray("failed")
        if (failed != null && failed.length() > 0) "${failed.length()} couldn't be resolved: ${failed.getString(0)}" else null
    }

    private fun failureText(code: Int, message: String) = when (code) {
        401 -> "Token rejected"
        409 -> message.ifEmpty { "That conflict was already resolved" }
        else -> "Failed (HTTP $code)"
    }

    /** Runs a server call, then reloads the list. The call returns an error message, or null on success. */
    private fun perform(call: (PcClient) -> String?) {
        if (busy) return
        busy = true
        render()
        async({ client ->
            val error = try {
                call(client)
            } catch (e: Exception) {
                errorText(e)
            }
            error to runCatching { client.conflicts() }.getOrNull()
        }) { result ->
            busy = false
            result.onSuccess { (error, refreshed) ->
                if (refreshed != null) {
                    conflicts = refreshed
                    host.setConflictCount(refreshed.size)
                }
                error?.let { host.toast(it) }
            }
            render()
        }
    }

    private fun render() {
        content.removeAllViews()
        content.addView(pageHead(
            "Conflicts",
            iconButton(R.drawable.ic_done_all, "Resolve all", filled = true) { anchor ->
                if (conflicts.isNotEmpty() && !busy) {
                    val menu = android.widget.PopupMenu(host, anchor)
                    menu.menu.add(0, 1, 0, "Keep newest of every file")
                    menu.menu.add(0, 2, 1, "Keep the PC's version of every file")
                    menu.setOnMenuItemClickListener {
                        if (it.itemId == 1) confirmAll(ConflictChoice.NEWEST, "Keep the newest version of every file?")
                        else confirmAll(ConflictChoice.PC, "Keep the PC's version of every file?")
                        true
                    }
                    menu.show()
                }
            }.apply { alpha = if (conflicts.isEmpty() || busy) 0.4f else 1f },
            iconButton(R.drawable.ic_refresh, "Refresh") { load() },
        ))
        content.addView(caption(
            "These files were changed in two places since they last synced, so AdbSync left them alone. " +
                "Pick which version to keep; the PC applies it and syncs. The version you don't keep is backed up on the PC.",
        ).apply { setPadding(0, dp(6), 0, dp(16)) })

        if (!loaded) {
            content.addView(card().apply { addView(caption("Loading…")) })
            return
        }
        if (conflicts.isEmpty()) {
            content.addView(card().apply { addView(body("Nothing to resolve.")) })
            return
        }

        if (busy) content.addView(indeterminateBar())

        for ((job, items) in conflicts.groupBy { it.job }) {
            content.addView(subSectionLabel(job))
            items.forEach { content.addView(conflictCard(it)) }
        }
    }

    private fun confirmAll(choice: ConflictChoice, message: String) {
        AlertDialog.Builder(host)
            .setMessage(message)
            .setPositiveButton("Yes") { _, _ -> resolveAll(choice) }
            .setNegativeButton("Cancel", null)
            .create().styled().show()
    }

    private fun conflictCard(c: RemoteConflict): View = card().apply {
        val isThisPhone = c.device == PcConfig.load(host)?.deviceName
        val deviceLabel = if (isThisPhone) "This phone" else c.device
        val deviceNewer = c.deviceModifiedAt != null && (c.pcModifiedAt == null || c.deviceModifiedAt >= c.pcModifiedAt)
        val pc = Side("pc", "PC", "Keep PC's", c.pcDeleted, c.pcSize, c.pcModifiedAt, !deviceNewer, ConflictChoice.PC)
        val device = Side(
            "device", deviceLabel, if (isThisPhone) "Keep phone's" else "Keep ${c.device}'s",
            c.deviceDeleted, c.deviceSize, c.deviceModifiedAt, deviceNewer, ConflictChoice.DEVICE,
        )

        addView(sectionLabel(c.path).apply { textSize = 14f })
        addView(View(host).apply { minimumHeight = dp(12) })

        if (sideBySide()) {
            val columns = LinearLayout(host).apply { orientation = LinearLayout.HORIZONTAL }
            listOf(pc, device).forEachIndexed { i, side ->
                columns.addView(sideColumn(c, side), LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f).apply {
                    if (i == 0) marginEnd = dp(6) else marginStart = dp(6)
                })
            }
            addView(columns)
        } else {
            listOf(pc, device).forEach { addView(sideRow(c, it)) }
        }

        if (!c.pcDeleted && !c.deviceDeleted) {
            addView(button("Both", fill = true) { resolve(c, ConflictChoice.BOTH) }.apply {
                (layoutParams as LinearLayout.LayoutParams).topMargin = dp(4)
            })
        }
        alpha = if (busy) 0.5f else 1f
    }

    private class Side(
        val key: String, val label: String, val keepText: String, val deleted: Boolean,
        val size: Long?, val modified: OffsetDateTime?, val newer: Boolean, val choice: ConflictChoice,
    )

    /** Two columns need roughly 150dp each after the screen and card padding; narrower windows stack instead. */
    private fun sideBySide() = host.resources.configuration.screenWidthDp - 64 >= 2 * 150 + 12

    /** Wide layout: thumbnail, details beneath it, then the keep button. */
    private fun sideColumn(c: RemoteConflict, side: Side): View = LinearLayout(host).apply {
        orientation = LinearLayout.VERTICAL
        addView(thumbnail(c, side, FrameLayout.LayoutParams(MATCH_PARENT, dp(120))), LinearLayout.LayoutParams(MATCH_PARENT, dp(120)))
        addView(sideLabel(side).apply { setPadding(0, dp(8), 0, 0) })
        addView(sideDetails(side))
        addView(button(side.keepText, fill = true) { resolve(c, side.choice) }.apply {
            (layoutParams as LinearLayout.LayoutParams).topMargin = dp(10)
        })
    }

    /** Narrow layout: the keep button sits to the right of the thumbnail, under the details. */
    private fun sideRow(c: RemoteConflict, side: Side): View = LinearLayout(host).apply {
        orientation = LinearLayout.HORIZONTAL
        setPadding(0, 0, 0, dp(12))
        val box = dp(88)
        addView(thumbnail(c, side, FrameLayout.LayoutParams(box, box)), LinearLayout.LayoutParams(box, box).apply { marginEnd = dp(12) })
        addView(LinearLayout(host).apply {
            orientation = LinearLayout.VERTICAL
            addView(sideLabel(side))
            addView(sideDetails(side))
            addView(button(side.keepText, fill = true) { resolve(c, side.choice) }.apply {
                (layoutParams as LinearLayout.LayoutParams).topMargin = dp(8)
            })
        }, LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f))
    }

    /** A file icon until (and unless) the PC can render an image for this side. */
    private fun thumbnail(c: RemoteConflict, side: Side, size: FrameLayout.LayoutParams): View {
        val icon = TextView(host).apply {
            text = if (side.deleted) "\uD83D\uDDD1" else "\uD83D\uDCC4"
            textSize = 30f
            gravity = Gravity.CENTER
            background = Ui.rounded(Ui.SURFACE, dp(6))
        }
        val image = ImageView(host).apply {
            scaleType = ImageView.ScaleType.CENTER_CROP
            visibility = View.GONE
            clipToOutline = true
            background = Ui.rounded(Ui.SURFACE, dp(6))
        }
        val frame = FrameLayout(host).apply {
            addView(icon, FrameLayout.LayoutParams(size.width, size.height))
            addView(image, FrameLayout.LayoutParams(size.width, size.height))
        }

        val config = PcConfig.load(host)
        if (!side.deleted && config != null) {
            thumbs.execute {
                val bitmap = try {
                    PcClient(config).conflictThumbnail(c.id, side.key)?.let { BitmapFactory.decodeByteArray(it, 0, it.size) }
                } catch (e: Exception) {
                    null
                }
                if (bitmap != null) host.ui {
                    image.setImageBitmap(bitmap)
                    image.visibility = View.VISIBLE
                    icon.visibility = View.GONE
                }
            }
        }
        return frame
    }

    private fun sideLabel(side: Side) = sectionLabel(if (side.newer) "${side.label} (newer)" else side.label).apply { textSize = 14f }

    private fun sideDetails(side: Side): View {
        val detail = if (side.deleted) "Deleted" else buildString {
            if (side.size != null) append(Formatter.formatShortFileSize(host, side.size))
            if (side.modified != null) {
                if (isNotEmpty()) append("\n")
                append(DateUtils.getRelativeTimeSpanString(side.modified.toInstant().toEpochMilli(), System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS))
            }
        }
        return caption(detail)
    }
}
