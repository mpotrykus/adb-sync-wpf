package com.adbsync.companion

import android.app.Activity
import android.graphics.Typeface
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.adbsync.companion.Ui.ButtonKind
import com.adbsync.companion.Ui.body
import com.adbsync.companion.Ui.button
import com.adbsync.companion.Ui.caption
import com.adbsync.companion.Ui.card
import com.adbsync.companion.Ui.divider
import com.adbsync.companion.Ui.dp
import com.adbsync.companion.Ui.field
import com.adbsync.companion.Ui.frostedBar
import com.adbsync.companion.Ui.input
import com.adbsync.companion.Ui.picker
import com.adbsync.companion.Ui.sectionLabel
import com.adbsync.companion.Ui.spacer
import com.adbsync.companion.Ui.toggle
import org.json.JSONArray
import org.json.JSONObject

/** Creates a job, or edits the one named by [EXTRA_JOB_ID]. */
class JobEditActivity : Activity() {
    private class Binding(val device: DeviceInfo, val toggle: android.widget.Switch, val path: EditText, val row: LinearLayout)

    private lateinit var content: LinearLayout
    private lateinit var saveButton: TextView
    private lateinit var errorView: TextView

    private var jobId: String? = null
    private var job: JSONObject? = null
    private var devices: List<DeviceInfo> = emptyList()
    private val bindings = ArrayList<Binding>()

    private lateinit var name: EditText
    private lateinit var appPackage: EditText
    private var enabled = true
    private lateinit var enabledToggle: android.widget.Switch
    private var kind = "onChange"
    private lateinit var intervalMinutes: EditText
    private lateinit var dailyTimes: EditText
    private lateinit var debounce: EditText
    private lateinit var exclude: EditText
    private lateinit var projectDir: EditText
    private lateinit var dryRun: android.widget.Switch
    private val choices = HashMap<String, String>()
    private val numbers = LinkedHashMap<String, EditText>()
    private var busy = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        actionBar?.hide()
        window.setBackgroundDrawable(Ui.windowBackground(this))
        window.isNavigationBarContrastEnforced = false
        window.insetsController?.setSystemBarsAppearance(
            0, WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS or WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS,
        )
        jobId = intent.getStringExtra(EXTRA_JOB_ID)
        content = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }

        val bar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            background = frostedBar(context, edgeOnTop = false)
            setPadding(dp(8), dp(8), dp(16), dp(8))
            addView(button("Back") { finish() })
            addView(body(if (jobId == null) "New job" else "Edit job", Ui.TEXT_PRIMARY, 18f).apply {
                typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            }, LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f))
            saveButton = button("Save", ButtonKind.Primary) { save() }
            addView(saveButton)
        }
        val scroll = ScrollView(this).apply {
            isVerticalScrollBarEnabled = false
            addView(content.apply { setPadding(dp(16), dp(16), dp(16), dp(32)) })
        }
        setContentView(LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            addView(bar)
            addView(scroll, LinearLayout.LayoutParams(MATCH_PARENT, 0, 1f))
            setOnApplyWindowInsetsListener { view, insets ->
                val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout() or WindowInsets.Type.ime())
                view.setPadding(bars.left, bars.top, bars.right, bars.bottom)
                insets
            }
        })
        content.addView(caption("Loading…"))
        Backend.async(this, { client ->
            val configs = jobId?.let { client.jobConfig(it) }
            configs to client.devices()
        }) { result ->
            result.onSuccess { (config, deviceList) ->
                job = config
                devices = deviceList
                build()
            }.onFailure {
                content.removeAllViews()
                content.addView(body(errorText(it), Ui.DANGER))
            }
        }
    }

    private fun overrides() = job?.optJSONObject("overrides") ?: JSONObject()

    private fun overrideNumber(key: String): String = overrides().let { if (it.has(key) && !it.isNull(key)) it.optInt(key).toString() else "" }

    private fun triValue(key: String): String = overrides().let {
        if (!it.has(key) || it.isNull(key)) "" else if (it.optBoolean(key)) "yes" else "no"
    }

    private fun build() {
        content.removeAllViews()
        val schedule = job?.optJSONObject("schedule")
        kind = schedule?.optString("kind", "onChange") ?: "onChange"

        name = input("Name", job?.optString("name").orEmpty())
        appPackage = input("com.example.app", job?.optStringOrNull("appPackage").orEmpty(), mono = true)

        content.addView(sectionLabel("General").apply { setPadding(dp(4), 0, 0, dp(8)) })
        content.addView(card().apply {
            addView(field("Name", name, if (jobId == null) "Also the project folder's name." else "Renaming moves the project folder."))
            addView(field("Linked Android app (optional)", LinearLayout(context).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                addView(appPackage, LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f).apply { marginEnd = dp(8) })
                addView(button("Pick") { pickApp() }.apply { (layoutParams as LinearLayout.LayoutParams).marginEnd = 0 })
            }, "The job is skipped while this app is open on a phone."))
        })

        content.addView(sectionLabel("Devices").apply { setPadding(dp(4), dp(8), 0, dp(8)) })
        content.addView(card().apply {
            if (devices.isEmpty()) addView(caption("No devices yet. Add one from the Devices tab first."))
            val existing = job?.optJSONArray("devices")
            devices.forEachIndexed { index, device ->
                var bound: JSONObject? = null
                for (i in 0 until (existing?.length() ?: 0)) {
                    if (existing!!.getJSONObject(i).getString("deviceId") == device.id) bound = existing.getJSONObject(i)
                }
                val switch = toggle(device.name).apply { isChecked = bound != null }
                val path = input("/storage/emulated/0/…", bound?.optString("remotePath").orEmpty(), mono = true)
                val row = LinearLayout(context).apply {
                    orientation = LinearLayout.HORIZONTAL
                    gravity = Gravity.CENTER_VERTICAL
                    visibility = if (bound != null) View.VISIBLE else View.GONE
                    addView(path, LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f).apply { marginEnd = dp(8) })
                    addView(button("Browse") {
                        Pickers.folder(this@JobEditActivity, device.id, device.name, path.text.toString().trim()) { path.setText(it) }
                    }.apply { (layoutParams as LinearLayout.LayoutParams).marginEnd = 0 })
                }
                switch.setOnCheckedChangeListener { _, checked -> row.visibility = if (checked) View.VISIBLE else View.GONE }
                if (index > 0) addView(divider())
                addView(switch)
                addView(row)
                bindings += Binding(device, switch, path, row)
            }
        })

        content.addView(sectionLabel("Schedule").apply { setPadding(dp(4), dp(8), 0, dp(8)) })
        enabledToggle = toggle("Enabled").apply { isChecked = job?.optBoolean("enabled", true) ?: true }
        intervalMinutes = input("240", (((schedule?.optInt("intervalSeconds", 14400) ?: 14400) / 60).coerceAtLeast(1)).toString(), numeric = true)
        dailyTimes = input("03:00, 15:30", schedule?.optJSONArray("dailyTimes")?.let { a -> (0 until a.length()).joinToString(", ") { a.getString(it) } }.orEmpty())
        debounce = input("10", (schedule?.optInt("debounceSeconds", 10) ?: 10).toString(), numeric = true)
        exclude = input("One pattern per line", job?.optJSONArray("exclude")?.let { a -> (0 until a.length()).joinToString("\n") { a.getString(it) } }.orEmpty(), mono = true, lines = 3)
        val intervalField = field("Every (minutes)", intervalMinutes)
        val dailyField = field("Times (HH:mm, comma separated)", dailyTimes)
        val debounceField = field("Wait after the last change (seconds)", debounce)
        fun syncSchedule() {
            intervalField.visibility = if (kind == "interval") View.VISIBLE else View.GONE
            dailyField.visibility = if (kind == "dailyAt") View.VISIBLE else View.GONE
            debounceField.visibility = if (kind == "onChange") View.VISIBLE else View.GONE
        }
        content.addView(card().apply {
            addView(enabledToggle)
            addView(field("Runs", picker(
                listOf("onChange" to "When a phone changes", "interval" to "On an interval", "dailyAt" to "Daily at set times", "manual" to "Only when I start it"), kind,
            ) { kind = it; syncSchedule() }))
            addView(intervalField)
            addView(dailyField)
            addView(debounceField)
            addView(field("Exclude patterns", exclude))
            syncSchedule()
        })

        val advanced = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; visibility = View.GONE }
        projectDir = input("Empty uses the projects directory", job?.optStringOrNull("projectDirectory").orEmpty(), mono = true)
        dryRun = toggle("Always a dry run (never writes anything)").apply { isChecked = job?.optBoolean("dryRun") ?: false }
        advanced.addView(card().apply {
            addView(caption("Anything left on \"Global setting\" or empty uses the value from Settings."))
            addView(field("Project folder on the server", projectDir))
            addView(dryRun)
            addView(choice("When a file changed on both sides", "conflictPolicy",
                listOf("" to "Global setting", "newestWins" to "Newest wins", "ask" to "Ask me"), overrides().optStringOrNull("conflictPolicy").orEmpty()))
            addView(choice("Keep a copy of the losing version", "backupConflictLosers", triOptions("Yes", "No"), triValue("backupConflictLosers")))
            addView(choice("Info notifications", "showInfoNotifications", triOptions("Show", "Hide"), triValue("showInfoNotifications")))
            addView(choice("Error notifications", "showErrorNotifications", triOptions("Show", "Hide"), triValue("showErrorNotifications")))
            addView(choice("Incremental pull", "incrementalPull", triOptions("On", "Off"), triValue("incrementalPull")))
            for ((label, key) in listOf(
                "Full pull at least every (days)" to "fullPullIntervalDays",
                "Push safety minimum (%)" to "pushSafetyMinimumPercent",
                "Bandwidth limit (KB/s)" to "bandwidthThrottleKBps",
                "Retry attempts" to "retryMaxAttempts",
                "Retry backoff (seconds)" to "retryBackoffSeconds",
                "Conflict copies kept (days)" to "conflictRetentionDays",
                "Checkpoints kept (days)" to "checkpointRetentionDays",
                "Rollback points kept" to "rollbackPointsToKeep",
                "Runs kept" to "maxRunHistoryEntries",
                "Runs with file detail" to "fileDetailRunsKept",
            )) {
                val box = input("Global setting", overrideNumber(key), numeric = true)
                numbers[key] = box
                addView(field(label, box))
            }
        })
        content.addView(button("Advanced settings", fill = true) {
            advanced.visibility = if (advanced.visibility == View.VISIBLE) View.GONE else View.VISIBLE
        }.apply { (layoutParams as LinearLayout.LayoutParams).topMargin = dp(4) })
        content.addView(spacer(12))
        content.addView(advanced)

        errorView = body("", Ui.DANGER, 13f).apply { visibility = View.GONE }
        content.addView(errorView)
        if (jobId != null) {
            content.addView(button("Delete job", ButtonKind.Danger, fill = true) { confirmDelete() }.apply {
                (layoutParams as LinearLayout.LayoutParams).topMargin = dp(16)
            })
        }
    }

    private fun triOptions(yes: String, no: String) = listOf("" to "Global setting", "yes" to yes, "no" to no)

    private fun choice(label: String, key: String, options: List<Pair<String, String>>, selected: String): View {
        choices[key] = selected
        return field(label, picker(options, selected) { choices[key] = it })
    }

    private fun pickApp() {
        val device = bindings.firstOrNull { it.toggle.isChecked }?.device ?: bindings.firstOrNull()?.device
        if (device == null) {
            toastShort("Add a device first.")
            return
        }
        Pickers.app(this, device.id, device.name) { appPackage.setText(it) }
    }

    private fun tri(key: String): Any = when (choices[key]) {
        "yes" -> true
        "no" -> false
        else -> JSONObject.NULL
    }

    private fun overridesBody(): JSONObject {
        val o = JSONObject()
        for ((key, box) in numbers) o.put(key, box.text.toString().trim().toIntOrNull() ?: JSONObject.NULL)
        o.put("conflictPolicy", choices["conflictPolicy"]?.takeIf { it.isNotEmpty() } ?: JSONObject.NULL)
        for (key in listOf("backupConflictLosers", "showInfoNotifications", "showErrorNotifications", "incrementalPull")) o.put(key, tri(key))
        return o
    }

    private fun scheduleBody(): JSONObject {
        val current = job?.optJSONObject("schedule")
        val s = JSONObject().put("kind", kind)
        val interval = intervalMinutes.text.toString().trim().toIntOrNull()?.coerceAtLeast(1)?.times(60)
        s.put("intervalSeconds", if (kind == "interval") interval ?: 14400 else current?.takeIf { it.has("intervalSeconds") && !it.isNull("intervalSeconds") }?.optInt("intervalSeconds") ?: JSONObject.NULL)
        if (kind == "dailyAt") {
            s.put("dailyTimes", JSONArray(dailyTimes.text.toString().split(',', ' ', ';').map { it.trim() }.filter { it.isNotEmpty() }))
        } else {
            s.put("dailyTimes", current?.optJSONArray("dailyTimes") ?: JSONObject.NULL)
        }
        s.put("debounceSeconds", if (kind == "onChange") debounce.text.toString().trim().toIntOrNull() ?: 10 else current?.takeIf { it.has("debounceSeconds") && !it.isNull("debounceSeconds") }?.optInt("debounceSeconds") ?: JSONObject.NULL)
        s.put("rescanSeconds", current?.takeIf { it.has("rescanSeconds") && !it.isNull("rescanSeconds") }?.optInt("rescanSeconds") ?: JSONObject.NULL)
        return s
    }

    private fun save() {
        if (busy || !::name.isInitialized) return
        val body = JSONObject()
            .put("name", name.text.toString().trim())
            .put("appPackage", appPackage.text.toString().trim().ifEmpty { null } ?: JSONObject.NULL)
            .put("exclude", JSONArray(exclude.text.toString().split('\n').map { it.trim() }.filter { it.isNotEmpty() }))
            .put("devices", JSONArray(bindings.filter { it.toggle.isChecked }.map {
                JSONObject().put("deviceId", it.device.id).put("remotePath", it.path.text.toString().trim())
            }))
            .put("schedule", scheduleBody())
            .put("enabled", enabledToggle.isChecked)
            .put("projectDirectory", projectDir.text.toString().trim().ifEmpty { null } ?: JSONObject.NULL)
            .put("dryRun", dryRun.isChecked)
            .put("overrides", overridesBody())
        setBusy(true)
        errorView.visibility = View.GONE
        Backend.async(this, { client ->
            val id = jobId
            if (id == null) client.createJob(body) else client.patchJob(id, body)
        }) { result ->
            setBusy(false)
            result.onSuccess {
                setResult(RESULT_OK)
                finish()
            }.onFailure {
                errorView.text = errorText(it)
                errorView.visibility = View.VISIBLE
            }
        }
    }

    private fun setBusy(value: Boolean) {
        busy = value
        saveButton.alpha = if (value) 0.5f else 1f
        saveButton.text = if (value) "Saving…" else "Save"
    }

    private fun confirmDelete() {
        val id = jobId ?: return
        Dialogs.confirm(
            this, "Delete ${name.text}?",
            "Its saved state and history go away. The project folder and its files stay on disk.", "Delete", danger = true,
        ) {
            Backend.async(this, { it.deleteJob(id) }) { result ->
                result.onSuccess {
                    setResult(RESULT_OK)
                    finish()
                }.onFailure {
                    errorView.text = errorText(it)
                    errorView.visibility = View.VISIBLE
                }
            }
        }
    }

    companion object {
        const val EXTRA_JOB_ID = "jobId"
    }
}
