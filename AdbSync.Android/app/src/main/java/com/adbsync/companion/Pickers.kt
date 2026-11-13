package com.adbsync.companion

import android.app.Activity
import android.app.AlertDialog
import android.text.Editable
import android.text.TextWatcher
import android.view.Gravity
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.LinearLayout
import android.widget.ScrollView
import com.adbsync.companion.Ui.body
import com.adbsync.companion.Ui.caption
import com.adbsync.companion.Ui.dp
import com.adbsync.companion.Ui.input
import com.adbsync.companion.Ui.styled

/** List pickers backed by a device: folders to sync and installed apps. */
object Pickers {
    private fun row(activity: Activity, label: String, detail: String?, onClick: () -> Unit) = LinearLayout(activity).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(activity.dp(12), activity.dp(11), activity.dp(12), activity.dp(11))
        isClickable = true
        background = android.graphics.drawable.RippleDrawable(
            android.content.res.ColorStateList.valueOf(0x33FFFFFF), null, Ui.rounded(0xFFFFFFFF.toInt(), activity.dp(6)),
        )
        addView(activity.body(label, Ui.TEXT_PRIMARY, 14f))
        if (detail != null) addView(activity.caption(detail))
        setOnClickListener { onClick() }
    }

    fun folder(activity: Activity, deviceId: String, deviceName: String, startPath: String, onPick: (String) -> Unit) {
        val list = LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(activity.dp(8), activity.dp(4), activity.dp(8), activity.dp(8))
        }
        val dialog = AlertDialog.Builder(activity)
            .setTitle("Folder on $deviceName")
            .setView(ScrollView(activity).apply { addView(list) })
            .setPositiveButton("Use this folder", null)
            .setNegativeButton("Cancel", null)
            .create()
            .styled()
        var path = startPath.ifBlank { "/storage/emulated/0" }
        var attempt = 0

        fun load(target: String) {
            list.removeAllViews()
            list.addView(activity.caption("Loading $target…").apply { setPadding(activity.dp(8), activity.dp(8), 0, 0) })
            Backend.async(activity, { it.browse(deviceId, target) }) { result ->
                list.removeAllViews()
                result.onSuccess { listing ->
                    path = listing.path
                    list.addView(activity.body(listing.path, Ui.ACCENT_FROM, 13f).apply {
                        typeface = android.graphics.Typeface.MONOSPACE
                        setPadding(activity.dp(12), activity.dp(4), activity.dp(12), activity.dp(8))
                    })
                    listing.parent?.let { parent -> list.addView(row(activity, "..", "Up one level") { load(parent) }) }
                    val folders = listing.entries.filter { it.isDirectory }
                    folders.forEach { entry ->
                        val child = if (listing.path == "/") "/${entry.name}" else "${listing.path}/${entry.name}"
                        list.addView(row(activity, entry.name + "/", null) { load(child) })
                    }
                    if (folders.isEmpty()) list.addView(activity.caption("No sub-folders here.").apply { setPadding(activity.dp(12), activity.dp(4), 0, 0) })
                }.onFailure {
                    if (attempt++ == 0 && target != "/storage/emulated/0") {
                        load("/storage/emulated/0")
                    } else {
                        list.addView(activity.body(errorText(it), Ui.DANGER, 13f).apply { setPadding(activity.dp(12), activity.dp(8), activity.dp(12), 0) })
                    }
                }
            }
        }
        dialog.show()
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
            onPick(path)
            dialog.dismiss()
        }
        load(path)
    }

    fun app(activity: Activity, deviceId: String, deviceName: String, onPick: (String) -> Unit) {
        val list = LinearLayout(activity).apply { orientation = LinearLayout.VERTICAL }
        val filter = activity.input("Filter apps")
        val holder = LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(activity.dp(16), activity.dp(4), activity.dp(16), activity.dp(8))
            addView(filter)
            addView(ScrollView(activity).apply { addView(list) }, LinearLayout.LayoutParams(MATCH_PARENT, activity.dp(360)).apply { topMargin = activity.dp(8) })
        }
        val dialog = AlertDialog.Builder(activity)
            .setTitle("Apps on $deviceName")
            .setView(holder)
            .setNegativeButton("Cancel", null)
            .create()
            .styled()
        var packages: List<String> = emptyList()

        fun render() {
            list.removeAllViews()
            val needle = filter.text.toString().trim().lowercase()
            packages.filter { needle.isEmpty() || it.lowercase().contains(needle) }.take(300).forEach { pkg ->
                list.addView(row(activity, pkg, null) {
                    onPick(pkg)
                    dialog.dismiss()
                })
            }
        }
        filter.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
            override fun afterTextChanged(s: Editable?) = render()
        })
        list.addView(activity.caption("Loading the phone's apps…").apply { setPadding(activity.dp(8), activity.dp(8), 0, 0) })
        dialog.show()
        Backend.async(activity, { it.packages(deviceId) }) { result ->
            result.onSuccess {
                packages = it.sorted()
                render()
            }.onFailure {
                list.removeAllViews()
                list.addView(activity.body(errorText(it), Ui.DANGER, 13f).apply { setPadding(activity.dp(8), activity.dp(8), 0, 0) })
            }
        }
    }
}
