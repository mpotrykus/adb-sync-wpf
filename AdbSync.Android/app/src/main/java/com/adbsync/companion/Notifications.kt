package com.adbsync.companion

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent

object Notifications {
    const val CHANNEL_SYNC = "sync"
    const val CHANNEL_RESULT = "result"
    const val ID_ONGOING = 1

    private const val ID_RESULT_BASE = 1000

    fun ensureChannels(context: Context) {
        val manager = context.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_SYNC, "Sync in progress", NotificationManager.IMPORTANCE_LOW)
        )
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_RESULT, "Sync results", NotificationManager.IMPORTANCE_DEFAULT)
        )
    }

    fun ongoing(context: Context, jobs: Map<String, JobState>): Notification {
        val title = when (jobs.size) {
            0 -> "AdbSync idle"
            1 -> "Syncing ${jobs.keys.first()}"
            else -> "Syncing ${jobs.size} jobs"
        }
        val lines = jobs.map { (name, state) ->
            val label = state.label()
            if (jobs.size == 1) label else "$name: $label"
        }
        val text = lines.joinToString(" · ")

        val builder = Notification.Builder(context, CHANNEL_SYNC)
            .setSmallIcon(android.R.drawable.stat_notify_sync)
            .setColor(Ui.ACCENT)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(Notification.BigTextStyle().bigText(lines.joinToString("\n")))
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setContentIntent(openApp(context))
            .setForegroundServiceBehavior(Notification.FOREGROUND_SERVICE_IMMEDIATE)
        if (jobs.isNotEmpty()) builder.setProgress(0, 0, true)
        return builder.build()
    }

    fun postResult(context: Context, event: SyncEvent) {
        Notifications.ensureChannels(context)
        val (title, icon) = when (event.event) {
            Protocol.EVENT_COMPLETED -> "Synced ${event.job}" to android.R.drawable.stat_sys_upload_done
            Protocol.EVENT_FAILED -> "Sync failed: ${event.job}" to android.R.drawable.stat_notify_error
            else -> return
        }
        val text = when (event.event) {
            Protocol.EVENT_COMPLETED -> if (event.pushed) "Changes pushed to device" else "No changes to push"
            else -> event.message.orEmpty()
        }
        val notification = Notification.Builder(context, CHANNEL_RESULT)
            .setSmallIcon(icon)
            .setColor(Ui.ACCENT)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(Notification.BigTextStyle().bigText(text))
            .setAutoCancel(true)
            .setContentIntent(openApp(context))
            .build()
        val id = ID_RESULT_BASE + (event.job.hashCode() and 0xFFFF)
        context.getSystemService(NotificationManager::class.java).notify(id, notification)
    }

    private fun openApp(context: Context): PendingIntent = PendingIntent.getActivity(
        context,
        0,
        Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
        PendingIntent.FLAG_IMMUTABLE,
    )
}
