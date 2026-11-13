package com.adbsync.companion

import android.app.AlertDialog
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import com.adbsync.companion.Ui.ButtonKind
import com.adbsync.companion.Ui.field
import com.adbsync.companion.Ui.input
import com.adbsync.companion.Ui.page
import com.adbsync.companion.Ui.picker
import com.adbsync.companion.Ui.styled

/** The server's devices: connection state, test, pair, add, edit and delete. */
class DevicesScreen(host: MainActivity) : Screen(host) {
    private val list = LinearLayout(host).apply { orientation = LinearLayout.VERTICAL }
    override val view = host.page(
        pageHead(
            "Devices",
            iconButton(R.drawable.ic_refresh, "Refresh") { load() },
            iconButton(R.drawable.ic_add, "Add device", filled = true) { showEditor(null) },
        ),
        list,
    )

    private var devices: List<DeviceInfo>? = null
    private val tests = HashMap<String, ConnectionTest>()
    private val testing = HashSet<String>()

    init {
        render()
    }

    override fun onShow() {
        load()
    }

    fun load() {
        async({ it.devices() }) { result ->
            result.onSuccess {
                devices = it
                render()
            }.onFailure {
                if (devices == null) {
                    list.removeAllViews()
                    list.addView(card().apply { addView(caption(errorText(it))) })
                } else {
                    toast(errorText(it))
                }
            }
        }
    }

    private fun render() {
        list.removeAllViews()
        val items = devices
        if (items == null) {
            list.addView(loader())
            return
        }
        if (items.isEmpty()) {
            list.addView(card().apply { addView(caption("No devices yet. Tap Add to register a phone.")) })
            return
        }
        val me = PcConfig.load(host)?.deviceName
        val columns = ((host.resources.configuration.screenWidthDp - 32) / 340).coerceAtLeast(1)
        items.chunked(columns).forEach { row ->
            list.addView(LinearLayout(host).apply {
                orientation = LinearLayout.HORIZONTAL
                isBaselineAligned = false
                for (i in 0 until columns) {
                    val cell = row.getOrNull(i)?.let { deviceCard(it, it.name == me) } ?: View(host)
                    addView(cell, LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f).apply { if (i < columns - 1) marginEnd = dp(12) })
                }
            })
        }
    }

    private fun stateColor(state: String) = when (state) {
        "connected" -> Ui.SUCCESS
        "unreachable", "unauthorized" -> Ui.DANGER
        else -> Ui.TEXT_SECONDARY
    }

    private fun deviceCard(device: DeviceInfo, isThisPhone: Boolean): View {
        val test = tests[device.id]
        val state = when {
            test == null -> device.state
            test.connected -> "connected"
            else -> "unreachable"
        }
        val color = stateColor(state)
        return card(color).apply {
            addView(LinearLayout(host).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                addView(LinearLayout(host).apply {
                    orientation = LinearLayout.VERTICAL
                    addView(sectionLabel(device.name).apply { textSize = 17f })
                    addView(caption("${if (device.isUsb) "USB" else "Wi-Fi"} · ${device.address}"))
                }, LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f))
                addView(chip(Fmt.words(state), color))
            })
            if (isThisPhone) addView(caption("This phone").apply { setPadding(0, dp(6), 0, 0) })
            val endpoint = test?.endpoint ?: device.endpoint
            addView(caption(
                "Used by ${device.usedByJobs} job${if (device.usedByJobs == 1) "" else "s"}" +
                    (endpoint?.let { " · adb at $it" } ?: ""),
            ).apply { setPadding(0, dp(8), 0, 0) })
            if (test != null && !test.connected) {
                addView(body(
                    "Not reachable${test.detail?.let { ": $it" } ?: "."} If this server hasn't been paired with the phone yet, use Pair.",
                    Ui.WARNING, 12f,
                ).apply { setPadding(0, dp(8), 0, 0) })
            }
            addView(LinearLayout(host).apply {
                orientation = LinearLayout.HORIZONTAL
                setPadding(0, dp(12), 0, 0)
                if (!device.isUsb) addView(button("Pair", ButtonKind.Primary) { showPair(device) })
                addView(button(if (device.id in testing) "Testing…" else "Test") { test(device) })
                addView(button("Edit") { showEditor(device) })
            })
        }
    }

    private fun test(device: DeviceInfo) {
        if (!testing.add(device.id)) return
        render()
        async({ it.testDevice(device.id) }) { result ->
            testing.remove(device.id)
            result.onSuccess {
                tests[device.id] = it
                toast(if (it.connected) "${device.name} is connected" else "${device.name} isn't reachable")
            }.onFailure { toast(errorText(it)) }
            render()
        }
    }

    private fun showEditor(existing: DeviceInfo?) {
        val form = LinearLayout(host).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(4), dp(20), dp(8))
        }
        val name = host.input("Name", existing?.name.orEmpty())
        var transport = existing?.transport ?: "wifi"
        val hostField = host.input("192.168.0.40", existing?.host.orEmpty(), mono = true)
        val portField = host.input("Optional", existing?.staticPort?.toString().orEmpty(), numeric = true)
        val serialField = host.input("adb serial", existing?.serial.orEmpty(), mono = true)
        val wifiBlock = LinearLayout(host).apply {
            orientation = LinearLayout.VERTICAL
            addView(host.field("IP address or hostname", hostField))
            addView(host.field("Fixed adb port", portField, "Only if the phone's wireless debugging port never changes."))
        }
        val usbBlock = LinearLayout(host).apply {
            orientation = LinearLayout.VERTICAL
            addView(host.field("adb serial", serialField, "Shown by adb devices."))
        }
        fun sync() {
            wifiBlock.visibility = if (transport == "wifi") View.VISIBLE else View.GONE
            usbBlock.visibility = if (transport == "usb") View.VISIBLE else View.GONE
        }
        form.addView(host.field("Name", name, if (existing != null && existing.usedByJobs > 0) "Remove it from its jobs before renaming." else null))
        form.addView(host.field("Connection", host.picker(listOf("wifi" to "Wi-Fi", "usb" to "USB"), transport) {
            transport = it
            sync()
        }))
        form.addView(wifiBlock)
        form.addView(usbBlock)
        sync()
        val error = body("", Ui.DANGER, 13f).apply { visibility = View.GONE; setPadding(0, dp(8), 0, 0) }
        form.addView(error)

        val dialog = AlertDialog.Builder(host)
            .setTitle(if (existing == null) "New device" else "Edit ${existing.name}")
            .setView(ScrollView(host).apply { addView(form) })
            .setPositiveButton("Save", null)
            .setNegativeButton("Cancel", null)
            .apply { if (existing != null) setNeutralButton("Delete", null) }
            .create()
            .styled()
        dialog.show()
        dialog.getButton(AlertDialog.BUTTON_NEUTRAL)?.setTextColor(Ui.DANGER)
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
            val port = portField.text.toString().trim().toIntOrNull()
            it.isEnabled = false
            error.visibility = View.GONE
            async({ client ->
                client.saveDevice(
                    existing?.id, name.text.toString().trim(), transport,
                    hostField.text.toString().trim(), serialField.text.toString().trim(), port,
                )
            }) { result ->
                it.isEnabled = true
                result.onSuccess {
                    dialog.dismiss()
                    load()
                }.onFailure {
                    error.text = errorText(it)
                    error.visibility = View.VISIBLE
                }
            }
        }
        dialog.getButton(AlertDialog.BUTTON_NEUTRAL)?.setOnClickListener {
            confirm("Delete ${existing!!.name}?", "It disappears from the server. Its project folders and files stay where they are.", "Delete", danger = true) {
                async({ it.deleteDevice(existing.id) }) { result ->
                    result.onSuccess {
                        dialog.dismiss()
                        load()
                    }.onFailure { toast(errorText(it)) }
                }
            }
        }
    }

    private fun showPair(device: DeviceInfo) {
        val form = LinearLayout(host).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(4), dp(20), dp(8))
        }
        form.addView(caption(
            "On ${device.name}: Settings → Developer options → Wireless debugging, tap \"Pair device with pairing code\" and keep " +
                "that screen open. Type the code below; the port fills in by itself when the server sees the phone.",
        ).apply { setPadding(0, 0, 0, dp(8)) })
        val hostField = host.input("Phone IP address", device.host.orEmpty(), mono = true)
        val portField = host.input("Pairing port", "", numeric = true)
        val codeField = host.input("6-digit pairing code", "", numeric = true)
        form.addView(host.field("Phone IP address", hostField))
        form.addView(host.field("Pairing port", portField))
        form.addView(host.field("Pairing code", codeField))
        val status = body("Looking for the phone's pairing service…", Ui.TEXT_SECONDARY, 13f).apply { setPadding(0, dp(8), 0, 0) }
        form.addView(status)

        val handler = Handler(Looper.getMainLooper())
        var open = true
        var finding = false
        fun find() {
            if (!open || finding || portField.text.isNotEmpty()) return
            finding = true
            async({ it.discoveredDevices() }) { result ->
                finding = false
                if (!open) return@async
                result.onSuccess { found ->
                    val pairing = found.filter { it.source == "mdnsPairing" && it.endpoint != null }
                    val match = pairing.firstOrNull { it.matchedDeviceId == device.id }
                        ?: pairing.firstOrNull { hostField.text.isNotEmpty() && it.endpoint!!.startsWith(hostField.text.toString() + ":") }
                        ?: pairing.singleOrNull { it.matchedDeviceId == null }
                    val endpoint = match?.endpoint
                    val colon = endpoint?.lastIndexOf(':') ?: -1
                    if (endpoint != null && colon > 0) {
                        hostField.setText(endpoint.substring(0, colon))
                        portField.setText(endpoint.substring(colon + 1))
                        status.text = "Found the phone's pairing service."
                    }
                }
                if (open && portField.text.isEmpty()) handler.postDelayed({ find() }, 2_000)
            }
        }

        val dialog = AlertDialog.Builder(host)
            .setTitle("Pair ${device.name}")
            .setView(ScrollView(host).apply { addView(form) })
            .setPositiveButton("Pair", null)
            .setNegativeButton("Cancel", null)
            .create()
            .styled()
        dialog.setOnDismissListener {
            open = false
            handler.removeCallbacksAndMessages(null)
        }
        dialog.show()
        find()
        dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener { button ->
            val port = portField.text.toString().trim().toIntOrNull()
            val code = codeField.text.toString().trim()
            if (port == null || port !in 1..65535 || code.length != 6) {
                status.text = "Enter the pairing port and the 6-digit code shown on the phone."
                status.setTextColor(Ui.WARNING)
                return@setOnClickListener
            }
            button.isEnabled = false
            status.text = "Pairing…"
            status.setTextColor(Ui.TEXT_SECONDARY)
            async({ client ->
                client.pairDevice(device.id, hostField.text.toString().trim(), port, code)
                client.testDevice(device.id)
            }) { result ->
                button.isEnabled = true
                result.onSuccess {
                    tests[device.id] = it
                    toast(if (it.connected) "${device.name} paired and connected" else "${device.name} paired, but the connection test failed")
                    dialog.dismiss()
                    load()
                }.onFailure {
                    status.text = errorText(it)
                    status.setTextColor(Ui.DANGER)
                }
            }
        }
    }
}
