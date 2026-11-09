package com.adbsync.companion

import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager
import android.util.Log

data class JobState(val event: String, val phase: String?, val device: String?) {
    fun label(): String {
        val base = if (event == Protocol.EVENT_QUEUED) "Queued" else Protocol.phaseLabel(phase)
        return if (device.isNullOrEmpty()) base else "$base · $device"
    }
}

/**
 * Holds a partial wake lock and a low-latency Wi-Fi lock while the PC reports active jobs, so Doze and
 * Wi-Fi power save don't drop adb's sockets mid-sync. Stops itself when no jobs remain, or when the PC
 * goes silent (no event or heartbeat) for [WATCHDOG_MS].
 */
class SyncService : Service() {
    private val handler = Handler(Looper.getMainLooper())
    private val jobs = linkedMapOf<String, JobState>()
    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null

    private val stopWhenIdle = Runnable { if (jobs.isEmpty()) stopSelf() }
    private val watchdog = Runnable {
        Log.w(TAG, "No events from PC for ${WATCHDOG_MS / 60000} min; releasing locks")
        jobs.clear()
        stopSelf()
    }

    override fun onCreate() {
        super.onCreate()
        isRunning = true
        Notifications.ensureChannels(this)
        acquireLocks()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startForeground(
            Notifications.ID_ONGOING,
            Notifications.ongoing(this, jobs),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC,
        )

        intent?.let(SyncEvent::from)?.let(::apply)

        handler.removeCallbacks(watchdog)
        handler.postDelayed(watchdog, WATCHDOG_MS)
        handler.removeCallbacks(stopWhenIdle)
        if (jobs.isEmpty()) handler.postDelayed(stopWhenIdle, IDLE_GRACE_MS)

        updateNotification()
        return START_NOT_STICKY
    }

    private fun apply(event: SyncEvent) {
        when {
            Protocol.isTerminal(event.event) -> {
                jobs.remove(event.job)
                Notifications.postResult(this, event)
            }
            event.event == Protocol.EVENT_HEARTBEAT -> {
                if (event.job !in jobs) jobs[event.job] = JobState(Protocol.EVENT_PHASE, event.phase, event.device)
            }
            else -> jobs[event.job] = JobState(event.event, event.phase, event.device)
        }
    }

    private fun updateNotification() {
        getSystemService(NotificationManager::class.java)
            .notify(Notifications.ID_ONGOING, Notifications.ongoing(this, jobs))
    }

    private fun acquireLocks() {
        wakeLock = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "AdbSync:sync")
            .apply {
                setReferenceCounted(false)
                acquire(MAX_LOCK_MS)
            }
        @Suppress("DEPRECATION")
        wifiLock = getSystemService(WifiManager::class.java)
            .createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "AdbSync:sync")
            .apply {
                setReferenceCounted(false)
                acquire()
            }
    }

    private fun releaseLocks() {
        wakeLock?.takeIf { it.isHeld }?.release()
        wifiLock?.takeIf { it.isHeld }?.release()
        wakeLock = null
        wifiLock = null
    }

    override fun onTimeout(startId: Int, fgsType: Int) {
        Log.w(TAG, "dataSync foreground service time limit reached")
        jobs.clear()
        stopSelf()
    }

    override fun onDestroy() {
        handler.removeCallbacksAndMessages(null)
        releaseLocks()
        isRunning = false
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        private const val TAG = "AdbSyncService"
        private const val WATCHDOG_MS = 15 * 60 * 1000L
        private const val IDLE_GRACE_MS = 10 * 1000L
        private const val MAX_LOCK_MS = 6 * 60 * 60 * 1000L

        @Volatile
        var isRunning = false
            private set
    }
}
