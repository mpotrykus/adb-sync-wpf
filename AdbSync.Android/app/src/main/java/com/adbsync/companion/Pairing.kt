package com.adbsync.companion

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.RemoteInput
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.graphics.drawable.Icon
import android.provider.Settings

object Pairing {
    private const val CHANNEL = "pairing"
    private const val ID = 2
    private const val ID_UNTRUSTED = 3
    const val KEY_CODE = "code"

    fun start(context: Context) {
        ensureChannel(context)
        val reply = PendingIntent.getBroadcast(
            context, 0, Intent(context, PairingReceiver::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE,
        )
        val action = Notification.Action.Builder(
            Icon.createWithResource(context, android.R.drawable.ic_menu_send), "Enter code", reply,
        ).addRemoteInput(RemoteInput.Builder(KEY_CODE).setLabel("6-digit pairing code").build()).build()

        val notification = Notification.Builder(context, CHANNEL)
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentTitle("Pair with AdbSync")
            .setContentText("Tap \"Pair device with pairing code\", then enter the code here.")
            .setStyle(Notification.BigTextStyle().bigText(
                "In Wireless debugging, tap \"Pair device with pairing code\". Keep that dialog open, pull down " +
                    "this notification and enter the 6-digit code."))
            .addAction(action)
            .setOngoing(true)
            .build()
        context.getSystemService(NotificationManager::class.java).notify(ID, notification)
        context.startActivity(wirelessDebuggingSettings())
    }

    fun notifyProgress(context: Context, text: String) = post(context, ID, "Pairing with AdbSync", text, ongoing = true)

    fun notifyResult(context: Context, success: Boolean, text: String) =
        post(context, ID, if (success) "Paired" else "Pairing failed", text, ongoing = false)

    fun notifyUntrustedNetwork(context: Context) = post(
        context, ID_UNTRUSTED, "Wireless debugging was refused on this network",
        "Turn on Wireless debugging once by hand on this Wi-Fi and choose \"Always allow on this network\".",
        ongoing = false,
    )

    private fun post(context: Context, id: Int, title: String, text: String, ongoing: Boolean) {
        ensureChannel(context)
        val notification = Notification.Builder(context, CHANNEL)
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(Notification.BigTextStyle().bigText(text))
            .setOngoing(ongoing)
            .setAutoCancel(!ongoing)
            .build()
        context.getSystemService(NotificationManager::class.java).notify(id, notification)
    }

    private fun ensureChannel(context: Context) {
        context.getSystemService(NotificationManager::class.java).createNotificationChannel(
            NotificationChannel(CHANNEL, "Pairing", NotificationManager.IMPORTANCE_HIGH)
        )
    }

    private fun wirelessDebuggingSettings() = Intent(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS)
        // Settings scrolls to and highlights the matching preference key; ignored where unsupported.
        .putExtra(":settings:fragment_args_key", "toggle_adb_wireless")
        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
}

class PairingReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val code = RemoteInput.getResultsFromIntent(intent)?.getCharSequence(Pairing.KEY_CODE)?.toString()?.trim().orEmpty()
        if (code.length != 6 || !code.all { it.isDigit() }) {
            Pairing.notifyResult(context, false, "\"$code\" isn't a 6-digit code. Start pairing again from the app.")
            return
        }
        Pairing.notifyProgress(context, "Sending the code to the PC…")
        CompanionJobService.schedulePair(context, code)
    }
}
