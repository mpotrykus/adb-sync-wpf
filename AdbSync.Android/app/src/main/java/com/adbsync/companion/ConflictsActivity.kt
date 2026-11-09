package com.adbsync.companion

import android.app.Activity
import android.app.AlertDialog
import android.graphics.BitmapFactory
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.format.DateUtils
import android.text.format.Formatter
import android.view.Gravity
import android.view.View
import android.view.WindowInsets
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.view.WindowInsetsController
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import com.adbsync.companion.Ui.ButtonKind
import com.adbsync.companion.Ui.body
import com.adbsync.companion.Ui.button
import com.adbsync.companion.Ui.caption
import com.adbsync.companion.Ui.card
import com.adbsync.companion.Ui.dp
import com.adbsync.companion.Ui.indeterminateBar
import com.adbsync.companion.Ui.sectionLabel
import com.adbsync.companion.Ui.styled
import com.adbsync.companion.Ui.subSectionLabel
import com.adbsync.companion.Ui.title
import org.json.JSONObject
import java.time.OffsetDateTime
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

/** Lists files that changed on a device and on the PC at once, so they can be settled from the phone. */
class ConflictsActivity : Activity() {
    private val handler = Handler(Looper.getMainLooper())
    private val io = Executors.newSingleThreadExecutor()
    private val thumbs: ExecutorService = Executors.newFixedThreadPool(2)

    private lateinit var content: LinearLayout
    private var conflicts: List<RemoteConflict> = emptyList()
    private var loaded = false
    private var busy = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        actionBar?.hide()
        window.setBackgroundDrawable(Ui.windowBackground())
        window.isNavigationBarContrastEnforced = false
        window.insetsController?.setSystemBarsAppearance(
            0, WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS or WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS,
        )

        content = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(20), dp(16), dp(24))
        }
        setContentView(ScrollView(this).apply {
            addView(content)
            isVerticalScrollBarEnabled = false
            setOnApplyWindowInsetsListener { view, insets ->
                val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout())
                view.setPadding(bars.left, bars.top, bars.right, bars.bottom)
                insets
            }
        })
        render()
    }

    override fun onResume() {
        super.onResume()
        load()
    }

    override fun onDestroy() {
        io.shutdownNow()
        thumbs.shutdownNow()
        super.onDestroy()
    }

    private fun load() {
        val config = PcConfig.load(this) ?: return
        io.execute {
            val result = try {
                PcClient(config).conflicts()
            } catch (e: Exception) {
                ui { Toast.makeText(this, "Couldn't reach the PC", Toast.LENGTH_SHORT).show() }
                return@execute
            }
            ui {
                conflicts = result
                loaded = true
                render()
            }
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

    /** Runs a PC call off the main thread, then reloads the list. The call returns an error message, or null on success. */
    private fun perform(call: (PcClient) -> String?) {
        val config = PcConfig.load(this) ?: return
        if (busy) return
        busy = true
        render()
        io.execute {
            val error = try {
                call(PcClient(config))
            } catch (e: Exception) {
                "Couldn't reach the PC: ${e.javaClass.simpleName}"
            }
            val refreshed = try {
                PcClient(config).conflicts()
            } catch (e: Exception) {
                null
            }
            ui {
                busy = false
                if (refreshed != null) conflicts = refreshed
                error?.let { Toast.makeText(this, it, Toast.LENGTH_LONG).show() }
                render()
            }
        }
    }

    private fun render() {
        content.removeAllViews()
        content.addView(title("Resolve conflicts"))
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

        content.addView(card().apply {
            addView(sectionLabel("All ${conflicts.size} files"))
            addView(View(this@ConflictsActivity).apply { minimumHeight = dp(10) })
            val row = LinearLayout(this@ConflictsActivity).apply { orientation = LinearLayout.HORIZONTAL }
            row.addView(button("Keep newest", ButtonKind.Primary) { confirmAll(ConflictChoice.NEWEST, "Keep the newest version of every file?") })
            row.addView(button("Keep PC's") { confirmAll(ConflictChoice.PC, "Keep the PC's version of every file?") })
            addView(row)
            if (busy) addView(indeterminateBar())
        })

        for ((job, items) in conflicts.groupBy { it.job }) {
            content.addView(subSectionLabel(job))
            items.forEach { content.addView(conflictCard(it)) }
        }
    }

    private fun confirmAll(choice: ConflictChoice, message: String) {
        AlertDialog.Builder(this)
            .setMessage(message)
            .setPositiveButton("Yes") { _, _ -> resolveAll(choice) }
            .setNegativeButton("Cancel", null)
            .create().styled().show()
    }

    private fun conflictCard(c: RemoteConflict): View = card().apply {
        val isThisPhone = c.device == PcConfig.load(this@ConflictsActivity)?.deviceName
        val deviceLabel = if (isThisPhone) "This phone" else c.device
        val deviceNewer = c.deviceModifiedAt != null && (c.pcModifiedAt == null || c.deviceModifiedAt >= c.pcModifiedAt)
        val pc = Side("pc", "PC", "Keep PC's", c.pcDeleted, c.pcSize, c.pcModifiedAt, !deviceNewer, ConflictChoice.PC)
        val device = Side(
            "device", deviceLabel, if (isThisPhone) "Keep phone's" else "Keep ${c.device}'s",
            c.deviceDeleted, c.deviceSize, c.deviceModifiedAt, deviceNewer, ConflictChoice.DEVICE,
        )

        addView(sectionLabel(c.path).apply { textSize = 14f })
        addView(View(this@ConflictsActivity).apply { minimumHeight = dp(12) })

        if (sideBySide()) {
            val columns = LinearLayout(this@ConflictsActivity).apply { orientation = LinearLayout.HORIZONTAL }
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
    private fun sideBySide() = resources.configuration.screenWidthDp - 64 >= 2 * 150 + 12

    /** Wide layout: thumbnail, details beneath it, then the keep button. */
    private fun sideColumn(c: RemoteConflict, side: Side): View = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        addView(thumbnail(c, side, FrameLayout.LayoutParams(MATCH_PARENT, dp(120))), LinearLayout.LayoutParams(MATCH_PARENT, dp(120)))
        addView(sideLabel(side).apply { setPadding(0, dp(8), 0, 0) })
        addView(sideDetails(side))
        addView(button(side.keepText, fill = true) { resolve(c, side.choice) }.apply {
            (layoutParams as LinearLayout.LayoutParams).topMargin = dp(10)
        })
    }

    /** Narrow layout: the keep button sits to the right of the thumbnail, under the details. */
    private fun sideRow(c: RemoteConflict, side: Side): View = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        setPadding(0, 0, 0, dp(12))
        val box = dp(88)
        addView(thumbnail(c, side, FrameLayout.LayoutParams(box, box)), LinearLayout.LayoutParams(box, box).apply { marginEnd = dp(12) })
        addView(LinearLayout(this@ConflictsActivity).apply {
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
        val icon = TextView(this).apply {
            text = if (side.deleted) "\uD83D\uDDD1" else "\uD83D\uDCC4"
            textSize = 30f
            gravity = Gravity.CENTER
            background = Ui.rounded(Ui.SURFACE, dp(6))
        }
        val image = ImageView(this).apply {
            scaleType = ImageView.ScaleType.CENTER_CROP
            visibility = View.GONE
            clipToOutline = true
            background = Ui.rounded(Ui.SURFACE, dp(6))
        }
        val frame = FrameLayout(this).apply {
            addView(icon, FrameLayout.LayoutParams(size.width, size.height))
            addView(image, FrameLayout.LayoutParams(size.width, size.height))
        }

        val config = PcConfig.load(this)
        if (!side.deleted && config != null) {
            thumbs.execute {
                val bitmap = try {
                    PcClient(config).conflictThumbnail(c.id, side.key)?.let { BitmapFactory.decodeByteArray(it, 0, it.size) }
                } catch (e: Exception) {
                    null
                }
                if (bitmap != null) ui {
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
            if (side.size != null) append(Formatter.formatShortFileSize(this@ConflictsActivity, side.size))
            if (side.modified != null) {
                if (isNotEmpty()) append("\n")
                append(DateUtils.getRelativeTimeSpanString(side.modified.toInstant().toEpochMilli(), System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS))
            }
        }
        return caption(detail)
    }

    private fun ui(block: () -> Unit) {
        handler.post { if (!isDestroyed) block() }
    }
}
