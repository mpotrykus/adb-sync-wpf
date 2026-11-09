package com.adbsync.companion

import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.ComponentName
import android.content.Context
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.os.PersistableBundle
import android.provider.Settings
import android.util.Log

/**
 * Background work that needs more time than a broadcast receiver gets: re-enabling wireless debugging and
 * reporting its port to the PC (phase 3), and finishing a pairing the user started from the notification (phase 4).
 */
class CompanionJobService : JobService() {

    override fun onStartJob(params: JobParameters): Boolean {
        Thread {
            try {
                when (params.jobId) {
                    JOB_ENABLE, JOB_PERIODIC -> enableAndReport(this)
                    JOB_PAIR -> pair(params.extras.getString(EXTRA_CODE).orEmpty())
                    JOB_WATCH -> onWirelessDebuggingChanged()
                }
            } catch (e: Exception) {
                Log.w(TAG, "Job ${params.jobId} failed", e)
            } finally {
                jobFinished(params, false)
            }
        }.start()
        return true
    }

    override fun onStopJob(params: JobParameters) = false

    private fun onWirelessDebuggingChanged() {
        armWatch(this)
        when {
            WirelessDebugging.isEnabled(this) -> {
                Log.i(TAG, "Wireless debugging turned on; reporting its port")
                scheduleEnable(this)
            }
            WirelessDebugging.ownWifiAddress(this) == null -> {
                Log.i(TAG, "Wireless debugging turned off after leaving Wi-Fi; will re-enable when Wi-Fi returns")
                scheduleEnable(this)
            }
            else -> Log.i(TAG, "Wireless debugging turned off while on Wi-Fi (by the user); leaving it off")
        }
    }

    private fun pair(code: String) {
        val config = PcConfig.load(this)
        val device = config?.deviceName
        if (config == null || device == null) {
            Pairing.notifyResult(this, false, "This phone hasn't been set up with AdbSync yet. Connect it over USB once.")
            return
        }

        val port = WirelessDebugging.findOwnPort(this, WirelessDebugging.PAIRING_SERVICE, 10_000)
        if (port == null) {
            Pairing.notifyResult(this, false, "Couldn't find the pairing service. Keep the \"Pair device with pairing code\" dialog open and try again.")
            return
        }

        val (status, message) = try {
            PcClient(config).pair(device, port, code)
        } catch (e: Exception) {
            0 to "Couldn't reach ${config.pcName}: ${e.javaClass.simpleName}"
        }
        if (status in 200..299) {
            Pairing.notifyResult(this, true, "Paired with ${config.pcName}")
            enableAndReport(this)
        } else {
            Pairing.notifyResult(this, false, message.ifBlank { "Pairing failed (HTTP $status)" })
        }
    }

    companion object {
        private const val TAG = "AdbSyncJobs"
        private const val JOB_ENABLE = 1
        private const val JOB_PAIR = 2
        private const val JOB_WATCH = 3
        private const val JOB_PERIODIC = 4
        private const val EXTRA_CODE = "code"
        private const val ENABLE_SETTLE_MS = 2_500L

        /** Runs as soon as the phone is on Wi-Fi (immediately if it already is). */
        fun scheduleEnable(context: Context) {
            val wifi = NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_WIFI).build()
            schedule(context, JobInfo.Builder(JOB_ENABLE, ComponentName(context, CompanionJobService::class.java))
                .setRequiredNetwork(wifi)
                .build())
        }

        /**
         * Two triggers, because Android versions differ:
         * - Up to Android 16 the system flips adb_wifi_enabled to 0 when the phone leaves Wi-Fi, and never back on.
         *   A content trigger on that setting catches the flip so the app can re-enable on rejoin. Content-trigger
         *   jobs are one-shot, so the job re-arms itself each time it runs.
         * - Android 17 keeps the setting at 1 and restarts adbd's Wi-Fi listener itself (on a new port), so
         *   nothing changes that a job could trigger on. A periodic Wi-Fi job reports the current port as a
         *   fallback for when the PC's own mDNS discovery misses it.
         */
        fun armWatch(context: Context) {
            val uri = Settings.Global.getUriFor("adb_wifi_enabled")
            schedule(context, JobInfo.Builder(JOB_WATCH, ComponentName(context, CompanionJobService::class.java))
                .addTriggerContentUri(JobInfo.TriggerContentUri(uri, 0))
                .setTriggerContentUpdateDelay(500)
                .setTriggerContentMaxDelay(2_000)
                .build())

            val scheduler = context.getSystemService(JobScheduler::class.java)
            if (scheduler.getPendingJob(JOB_PERIODIC) == null) {
                val wifi = NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_WIFI).build()
                schedule(context, JobInfo.Builder(JOB_PERIODIC, ComponentName(context, CompanionJobService::class.java))
                    .setPeriodic(15 * 60 * 1000L)
                    .setRequiredNetwork(wifi)
                    .setPersisted(true)
                    .build())
            }
        }

        fun schedulePair(context: Context, code: String) {
            schedule(context, JobInfo.Builder(JOB_PAIR, ComponentName(context, CompanionJobService::class.java))
                .setOverrideDeadline(0)
                .setExtras(PersistableBundle().apply { putString(EXTRA_CODE, code) })
                .build())
        }

        private fun schedule(context: Context, job: JobInfo) {
            context.getSystemService(JobScheduler::class.java).schedule(job)
        }

        /** Blocking. Turns wireless debugging back on (if allowed and on Wi-Fi) and tells the PC its port. */
        fun enableAndReport(context: Context) {
            if (!WirelessDebugging.autoEnable(context)) return
            if (WirelessDebugging.ownWifiAddress(context) == null) {
                Log.i(TAG, "Not on Wi-Fi; nothing to do")
                return
            }

            if (!WirelessDebugging.isEnabled(context)) {
                if (!WirelessDebugging.canWrite(context)) return
                WirelessDebugging.enable(context)
                Thread.sleep(ENABLE_SETTLE_MS)
                if (!WirelessDebugging.isEnabled(context)) {
                    Log.w(TAG, "Wireless debugging turned itself back off; this network probably isn't trusted")
                    Pairing.notifyUntrustedNetwork(context)
                    return
                }
                Log.i(TAG, "Enabled wireless debugging")
            }

            val config = PcConfig.load(context)
            val device = config?.deviceName
            if (config == null || device == null) {
                Log.i(TAG, "Wireless debugging is on, but the PC hasn't set this phone up yet; can't report the port")
                return
            }
            val port = WirelessDebugging.findOwnPort(context, WirelessDebugging.CONNECT_SERVICE, 15_000) ?: run {
                Log.w(TAG, "Wireless debugging is on but its connect service wasn't found")
                return
            }
            val status = PcClient(config).reportEndpoint(device, port)
            Log.i(TAG, "Reported port $port to ${config.pcName}: HTTP $status")
        }
    }
}
