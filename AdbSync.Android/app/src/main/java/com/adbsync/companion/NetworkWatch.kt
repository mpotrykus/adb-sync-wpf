package com.adbsync.companion

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log

/** Content-trigger jobs aren't persisted across reboots, so the watch is re-armed on boot and on app update. */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED && intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        Log.i("AdbSyncJobs", "Re-arming after ${intent.action}")
        CompanionJobService.armWatch(context)
        CompanionJobService.scheduleEnable(context)
    }
}
