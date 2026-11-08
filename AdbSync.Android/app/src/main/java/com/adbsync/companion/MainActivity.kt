package com.adbsync.companion

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.provider.Settings
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

class MainActivity : Activity() {
    private val handler = Handler(Looper.getMainLooper())
    private lateinit var notificationStatus: TextView
    private lateinit var batteryStatus: TextView
    private lateinit var adbTimeoutStatus: TextView
    private lateinit var serviceStatus: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val pad = (16 * resources.displayMetrics.density).toInt()
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(pad, pad, pad, pad)
        }

        column.addView(heading("AdbSync Companion"))

        notificationStatus = body()
        column.addView(notificationStatus)
        column.addView(button("Allow notifications") {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), REQUEST_NOTIFICATIONS)
        })

        batteryStatus = body()
        column.addView(batteryStatus)
        column.addView(button("Exempt from battery optimization") { requestBatteryExemption() })

        adbTimeoutStatus = body()
        column.addView(adbTimeoutStatus)
        column.addView(button("Open developer options") {
            startActivity(Intent(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS))
        })

        serviceStatus = body()
        column.addView(serviceStatus)
        column.addView(button("Simulate a 10s sync") { simulateSync() })

        setContentView(ScrollView(this).apply { addView(column) })
    }

    override fun onResume() {
        super.onResume()
        refresh()
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        refresh()
    }

    private fun refresh() {
        val notificationsOk = checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
        notificationStatus.text = if (notificationsOk) "✓ Notifications allowed" else "✗ Notifications blocked: progress won't be visible"

        val batteryOk = getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(packageName)
        batteryStatus.text = if (batteryOk) {
            "✓ Exempt from battery optimization"
        } else {
            "✗ Battery optimized: Android may refuse to start the sync service from the background"
        }

        adbTimeoutStatus.text = when (readAdbAuthTimeoutDisabled()) {
            true -> "✓ adb authorization timeout disabled"
            false -> "! adb authorization timeout is on: the PC's key is revoked after 7 days unused. " +
                "Turn on \"Disable adb authorization timeout\" in developer options."
            null -> "? Couldn't read the adb authorization timeout setting. Check \"Disable adb authorization timeout\" " +
                "in developer options."
        }

        serviceStatus.text = if (SyncService.isRunning) "Sync service running (locks held)" else "Sync service idle"
    }

    private fun readAdbAuthTimeoutDisabled(): Boolean? = try {
        Settings.Global.getLong(contentResolver, "adb_allowed_connection_time", -1L)
            .takeIf { it >= 0 }
            ?.let { it == 0L }
    } catch (_: SecurityException) {
        null
    }

    @SuppressLint("BatteryLife")
    private fun requestBatteryExemption() {
        startActivity(
            Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName"))
        )
    }

    private fun simulateSync() {
        val job = "Simulated job"
        fun send(event: String, phase: String? = null, pushed: Boolean = false) {
            startForegroundService(
                SyncEvent(event, job, phase, "this device", null, pushed)
                    .writeTo(Intent(this, SyncService::class.java))
            )
            handler.postDelayed(::refresh, 300)
        }
        send(Protocol.EVENT_PHASE, "Pull")
        handler.postDelayed({ send(Protocol.EVENT_PHASE, "Push") }, 5_000)
        handler.postDelayed({ send(Protocol.EVENT_COMPLETED, pushed = true) }, 10_000)
        handler.postDelayed(::refresh, 21_000)
    }

    private fun heading(text: String) = TextView(this).apply {
        this.text = text
        textSize = 22f
        setPadding(0, 0, 0, pad())
    }

    private fun body() = TextView(this).apply {
        textSize = 15f
        setPadding(0, pad(), 0, pad() / 2)
    }

    private fun button(text: String, onClick: () -> Unit) = Button(this).apply {
        this.text = text
        layoutParams = LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT)
        setOnClickListener { onClick() }
    }

    private fun pad() = (12 * resources.displayMetrics.density).toInt()

    private companion object {
        const val REQUEST_NOTIFICATIONS = 1
    }
}
