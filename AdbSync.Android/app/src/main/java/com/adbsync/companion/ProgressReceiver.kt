package com.adbsync.companion

import android.app.ForegroundServiceStartNotAllowedException
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log

class ProgressReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.getStringExtra(Protocol.EXTRA_EVENT) == Protocol.EVENT_CONFIG) {
            val url = intent.getStringExtra(Protocol.EXTRA_URL)
            val token = intent.getStringExtra(Protocol.EXTRA_TOKEN)
            val fp = intent.getStringExtra(Protocol.EXTRA_FP)
            if (url != null && token != null && fp != null) {
                val pc = intent.getStringExtra(Protocol.EXTRA_PC) ?: "PC"
                if (PcConfig.save(context, PcConfig(url, token, fp, pc))) Log.i(TAG, "PC server set to $url ($pc)")
            }
            return
        }

        val event = SyncEvent.from(intent) ?: run {
            Log.w(TAG, "Ignoring broadcast without event/job extras")
            return
        }

        if (Protocol.isTerminal(event.event) && !SyncService.isRunning) {
            Notifications.postResult(context, event)
            return
        }

        val serviceIntent = event.writeTo(Intent(context, SyncService::class.java))
        try {
            context.startForegroundService(serviceIntent)
        } catch (e: ForegroundServiceStartNotAllowedException) {
            Log.w(TAG, "Foreground service start blocked; is the app exempt from battery optimization?", e)
            if (Protocol.isTerminal(event.event)) Notifications.postResult(context, event)
        }
    }

    private companion object {
        const val TAG = "AdbSyncReceiver"
    }
}
