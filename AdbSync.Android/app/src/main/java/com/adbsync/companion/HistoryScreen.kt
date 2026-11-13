package com.adbsync.companion

import android.view.Gravity
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.HorizontalScrollView
import android.widget.LinearLayout
import com.adbsync.companion.Ui.body
import com.adbsync.companion.Ui.button
import com.adbsync.companion.Ui.caption
import com.adbsync.companion.Ui.card
import com.adbsync.companion.Ui.chip
import com.adbsync.companion.Ui.dp
import com.adbsync.companion.Ui.page
import com.adbsync.companion.Ui.sectionLabel
import com.adbsync.companion.Ui.statTile
import com.adbsync.companion.Ui.statStrip
import com.adbsync.companion.Ui.statusLine
import com.adbsync.companion.Ui.title
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

/** Every job's runs in one list, newest first, filterable by job. */
class HistoryScreen(host: MainActivity) : Screen(host) {
    private val filters = LinearLayout(host).apply { orientation = LinearLayout.HORIZONTAL }
    private val stats = LinearLayout(host).apply { orientation = LinearLayout.VERTICAL }
    private val list = LinearLayout(host).apply { orientation = LinearLayout.VERTICAL }

    override val view = host.page(
        pageHead("Run history", iconButton(R.drawable.ic_refresh, "Refresh") { reload() }),
        HorizontalScrollView(host).apply {
            isHorizontalScrollBarEnabled = false
            addView(filters)
            setPadding(0, 0, 0, host.dp(4))
        },
        stats,
        list,
    )

    private var jobs: Map<String, String> = emptyMap()
    private var selected = linkedSetOf<String>()
    private var runs = mutableListOf<RunListItem>()
    private var next: String? = null
    private var loaded = false
    private var stale = true
    private var loadingMore = false
    private var generation = 0

    init {
        list.addView(loader())
    }

    override fun onShow() {
        if (stale) reload()
    }

    /** Jumps to this tab showing only [jobName]'s runs. */
    fun focusJob(jobName: String) {
        async({ it.jobIdOf(jobName) }) { result ->
            result.onSuccess {
                selected = linkedSetOf(it)
                stale = true
                host.showTab(1)
            }.onFailure { toast(errorText(it)) }
        }
    }

    /** A job started or finished; the list is out of date. */
    fun jobsChanged() {
        stale = true
        if (visible) reload()
    }

    private fun reload() {
        stale = false
        val token = ++generation
        val ids = selected.toSet()
        async({ client ->
            val names = client.jobNames()
            names to client.runsPage(ids, null)
        }) { result ->
            if (token != generation) return@async
            result.onSuccess { (names, page) ->
                jobs = names
                runs = page.items.toMutableList()
                next = page.nextCursor
                loaded = true
                render()
            }.onFailure {
                if (!loaded) {
                    list.removeAllViews()
                    list.addView(card().apply { addView(caption(errorText(it))) })
                } else {
                    toast(errorText(it))
                }
            }
        }
    }

    private fun loadMore() {
        val cursor = next ?: return
        if (loadingMore) return
        loadingMore = true
        render()
        val token = generation
        async({ it.runsPage(selected.toSet(), cursor) }) { result ->
            loadingMore = false
            if (token != generation) return@async
            if (result.isFailure) render()
            result.onSuccess { page ->
                val known = runs.map { it.run.runId }.toSet()
                runs.addAll(page.items.filter { it.run.runId !in known })
                next = page.nextCursor
                render()
            }.onFailure { toast(errorText(it)) }
        }
    }

    private fun render() {
        filters.removeAllViews()
        filters.addView(host.chip("All jobs", if (selected.isEmpty()) Ui.ACCENT_FROM else Ui.TEXT_SECONDARY) {
            selected.clear()
            reload()
            render()
        })
        jobs.entries.sortedBy { it.value.lowercase() }.forEach { (id, name) ->
            filters.addView(host.chip(name, if (id in selected) Ui.ACCENT_FROM else Ui.TEXT_SECONDARY) {
                if (!selected.add(id)) selected.remove(id)
                reload()
                render()
            })
        }

        stats.removeAllViews()
        list.removeAllViews()
        if (runs.isEmpty()) {
            list.addView(host.card().apply { addView(host.caption("No runs yet.")) })
            return
        }
        val done = runs.filter { !it.live }
        stats.addView(host.statStrip(listOf(
            host.statTile("${runs.size}", "Runs", Ui.ACCENT_FROM),
            host.statTile("${done.count { it.run.failed }}", "Failed", Ui.DANGER),
            host.statTile("${done.sumOf { it.run.filesCopied }}", "Copied", Ui.SUCCESS),
            host.statTile(Fmt.bytes(host, done.sumOf { it.run.bytesCopied }), "Transferred", 0xFF38BDF8.toInt()),
        )).apply { (layoutParams as LinearLayout.LayoutParams).topMargin = host.dp(8) })

        runs.forEach { list.addView(runCard(it)) }
        if (next != null) {
            list.addView(if (loadingMore) loader(32) else host.button("Load more", fill = true) { loadMore() })
        }
    }

    private fun runCard(item: RunListItem): View {
        val run = item.run
        val color = when {
            item.live -> Ui.ACCENT_FROM
            run.failed -> Ui.DANGER
            run.skipped -> Ui.WARNING
            else -> Ui.SUCCESS
        }
        val started = run.startedAt.atZoneSameInstant(ZoneId.systemDefault()).format(STARTED)
        return host.card(color).apply {
            isClickable = true
            setOnClickListener {
                val config = PcConfig.load(host) ?: return@setOnClickListener
                RunDetailDialog(host, config, item.jobName, "${item.jobName} · $started", if (item.live) null else run.runId).show()
            }
            addView(LinearLayout(host).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                addView(host.sectionLabel(item.jobName).apply { textSize = 16f }, LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f))
                addView(host.caption(Fmt.ago(host, run.startedAt)))
            })
            addView(host.statusLine(
                (if (item.live) run.outcome else run.outcomeText()).let { label ->
                    if (item.mode != "normal" && !label.equals(Fmt.words(item.mode), ignoreCase = true)) "$label · ${Fmt.words(item.mode)}" else label
                },
                color, Ui.TEXT_PRIMARY,
            ).apply { setPadding(0, host.dp(6), 0, 0) })
            val details = buildList {
                if (run.filesCopied > 0) add("${run.filesCopied} copied")
                if (run.filesDeleted > 0) add("${run.filesDeleted} deleted")
                if (run.errorCount > 0) add("${run.errorCount} errors")
                if (run.bytesCopied > 0) add(Fmt.bytes(host, run.bytesCopied))
                if (!item.live) add(Fmt.duration(run.startedAt, run.completedAt))
                if (item.trigger.isNotEmpty()) add(Fmt.words(item.trigger))
            }
            addView(host.caption(details.joinToString(" · ")).apply { setPadding(host.dp(15), host.dp(2), 0, 0) })
            run.errorMessage?.takeIf { run.failed || run.skipped }?.let {
                addView(host.body(it, if (run.failed) Ui.DANGER else Ui.WARNING, 12f).apply { setPadding(host.dp(15), host.dp(4), 0, 0) })
            }
        }
    }

    private companion object {
        val STARTED: DateTimeFormatter = DateTimeFormatter.ofLocalizedDateTime(FormatStyle.MEDIUM, FormatStyle.SHORT)
    }
}
