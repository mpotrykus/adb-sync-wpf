package com.adbsync.companion

import android.view.View
import android.widget.LinearLayout
import com.adbsync.companion.Ui.ButtonKind
import com.adbsync.companion.Ui.field
import com.adbsync.companion.Ui.input
import com.adbsync.companion.Ui.picker
import com.adbsync.companion.Ui.toggle
import org.json.JSONObject

/** The server's global settings, as a form that can sit inside another page. */
class SettingsScreen(host: MainActivity) : Screen(host) {
    private val content = LinearLayout(host).apply { orientation = LinearLayout.VERTICAL }
    override val view: View = content

    private var current: JSONObject? = null
    private var loadedOnce = false
    private var errorView: android.widget.TextView? = null
    private val collectors = ArrayList<(JSONObject) -> Unit>()

    init {
        content.addView(card().apply { addView(caption("Loading…")) })
    }

    override fun onShow() {
        if (!loadedOnce) load()
    }

    fun load() {
        async({ it.settings() }) { result ->
            result.onSuccess {
                current = it
                loadedOnce = true
                build(it)
            }.onFailure {
                content.removeAllViews()
                content.addView(card().apply { addView(caption(errorText(it))) })
            }
        }
    }

    private fun build(s: JSONObject) {
        content.removeAllViews()
        collectors.clear()
        val notifications = s.optJSONObject("notifications") ?: JSONObject()
        val quiet = notifications.optJSONObject("quietHours") ?: JSONObject()

        fun text(label: String, key: String, help: String? = null): View {
            val box = host.input(label, s.optString(key))
            collectors += { it.put(key, box.text.toString().trim()) }
            return host.field(label, box, help)
        }

        fun number(label: String, key: String, help: String? = null): View {
            val box = host.input(label, s.optInt(key).toString(), numeric = true)
            collectors += { it.put(key, box.text.toString().trim().toIntOrNull() ?: s.optInt(key)) }
            return host.field(label, box, help)
        }

        fun optionalNumber(label: String, key: String, help: String? = null): View {
            val box = host.input("None", if (s.isNull(key) || !s.has(key)) "" else s.optInt(key).toString(), numeric = true)
            collectors += {
                val value = box.text.toString().trim().toIntOrNull()
                it.put(key, value ?: JSONObject.NULL)
            }
            return host.field(label, box, help)
        }

        fun flag(label: String, key: String, parent: JSONObject = s, apply: (JSONObject, Boolean) -> Unit): View {
            val toggle = host.toggle(label).apply { isChecked = parent.optBoolean(key) }
            collectors += { apply(it, toggle.isChecked) }
            return toggle
        }

        content.addView(sectionLabel("General").padded())
        content.addView(card().apply {
            addView(host.field("Projects directory", host.input("", s.optString("projectsDirectory"), mono = true).apply {
                isEnabled = false
                alpha = 0.7f
            }, "Where each job's master copy lives on the server. Change it from the web UI."))
            addView(text("Time zone", "timeZone", "IANA name, e.g. America/Chicago. Daily schedules and quiet hours use it."))
            addView(flag("Run missed schedules after a restart", "runMissedSchedules") { o, v -> o.put("runMissedSchedules", v) })
            addView(flag("Send sync progress to the companion apps", "companionBroadcastsEnabled") { o, v -> o.put("companionBroadcastsEnabled", v) })
        })

        content.addView(sectionLabel("Syncing").padded())
        content.addView(card().apply {
            var policy = s.optString("conflictPolicy", "newestWins")
            collectors += { it.put("conflictPolicy", policy) }
            addView(host.field("When a file changed on two sides", host.picker(
                listOf("newestWins" to "Newest wins", "ask" to "Ask me"), policy,
            ) { policy = it }))
            addView(flag("Keep a copy of the losing version", "backupConflictLosers") { o, v -> o.put("backupConflictLosers", v) })
            addView(number("Jobs at once", "maxConcurrentJobs"))
            addView(number("Jobs per device", "maxConcurrentPerDevice"))
            addView(number("Push safety minimum (%)", "pushSafetyMinimumPercent", "A push that would delete more than this share of a phone's files is held back."))
            addView(number("Retry attempts", "retryMaxAttempts"))
            addView(number("Retry backoff (seconds)", "retryBackoffSeconds"))
            addView(optionalNumber("Bandwidth limit (KB/s)", "bandwidthThrottleKBps", "Leave empty for no limit."))
            addView(flag("Incremental pull (only transfer changed files)", "incrementalPull") { o, v -> o.put("incrementalPull", v) })
            addView(number("Full pull at least every (days)", "fullPullIntervalDays", "0 always pulls everything."))
        })

        content.addView(sectionLabel("Retention").padded())
        content.addView(card().apply {
            addView(number("Logs (days)", "logRetentionDays"))
            addView(number("Conflict copies (days)", "conflictRetentionDays"))
            addView(number("Checkpoints (days)", "checkpointRetentionDays"))
            addView(number("Rollback points kept", "rollbackPointsToKeep"))
            addView(number("Runs kept per job", "maxRunHistoryEntries"))
            addView(number("Runs with file detail", "fileDetailRunsKept"))
        })

        content.addView(sectionLabel("Notifications").padded())
        content.addView(card().apply {
            val info = host.toggle("Show info notifications").apply { isChecked = notifications.optBoolean("showInfo") }
            val errors = host.toggle("Show error notifications").apply { isChecked = notifications.optBoolean("showErrors") }
            val quietOn = host.toggle("Quiet hours").apply { isChecked = quiet.optBoolean("enabled") }
            val start = host.input("22:00", quiet.optString("start", "22:00"))
            val end = host.input("07:00", quiet.optString("end", "07:00"))
            collectors += {
                it.put("notifications", JSONObject()
                    .put("showInfo", info.isChecked)
                    .put("showErrors", errors.isChecked)
                    .put("quietHours", JSONObject()
                        .put("enabled", quietOn.isChecked)
                        .put("start", start.text.toString().trim())
                        .put("end", end.text.toString().trim())))
            }
            addView(info)
            addView(errors)
            addView(quietOn)
            addView(host.field("Quiet from (HH:mm)", start))
            addView(host.field("Quiet until (HH:mm)", end))
        })

        val error = body("", Ui.DANGER, 13f).apply { visibility = View.GONE }
        errorView = error
        content.addView(error)
        content.addView(button("Save server settings", ButtonKind.Primary, fill = true) { save(error) })
    }

    private fun View.padded(): View = apply { setPadding(0, dp(8), 0, dp(8)) }

    fun saveNow() {
        errorView?.let { save(it) }
    }

    private fun save(error: android.widget.TextView) {
        val patch = JSONObject()
        collectors.forEach { it(patch) }
        error.visibility = View.GONE
        async({ it.patchSettings(patch) }) { result ->
            result.onSuccess {
                toast("Settings saved")
                load()
            }.onFailure {
                error.text = errorText(it)
                error.visibility = View.VISIBLE
            }
        }
    }
}
