package com.adbsync.companion

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Build
import android.provider.Settings
import android.util.Log
import java.net.Inet4Address
import java.net.InetAddress
import java.util.concurrent.CountDownLatch
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

object WirelessDebugging {
    const val CONNECT_SERVICE = "_adb-tls-connect._tcp"
    const val PAIRING_SERVICE = "_adb-tls-pairing._tcp"

    private const val TAG = "AdbSyncWifiDebug"
    private const val SETTING = "adb_wifi_enabled"
    private const val PREFS = "settings"
    private const val KEY_AUTO_ENABLE = "auto_enable_wifi_debugging"

    fun canWrite(context: Context) =
        context.checkSelfPermission(Manifest.permission.WRITE_SECURE_SETTINGS) == PackageManager.PERMISSION_GRANTED

    fun isEnabled(context: Context) = Settings.Global.getInt(context.contentResolver, SETTING, 0) == 1

    fun enable(context: Context): Boolean = try {
        Settings.Global.putInt(context.contentResolver, SETTING, 1)
    } catch (e: SecurityException) {
        Log.w(TAG, "Not allowed to enable wireless debugging", e)
        false
    }

    fun autoEnable(context: Context) =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_AUTO_ENABLE, true)

    fun setAutoEnable(context: Context, value: Boolean) =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean(KEY_AUTO_ENABLE, value).apply()

    @Suppress("DEPRECATION")
    fun ownWifiAddress(context: Context): InetAddress? {
        val cm = context.getSystemService(ConnectivityManager::class.java)
        for (network in cm.allNetworks) {
            val caps = cm.getNetworkCapabilities(network) ?: continue
            if (!caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) continue
            val address = cm.getLinkProperties(network)?.linkAddresses
                ?.map { it.address }
                ?.firstOrNull { it is Inet4Address }
            if (address != null) return address
        }
        return null
    }

    /** Finds the port this phone's own adbd advertises for [serviceType], matching on our Wi-Fi address. Blocking. */
    fun findOwnPort(context: Context, serviceType: String, timeoutMs: Long): Int? {
        val own = ownWifiAddress(context) ?: return null
        val nsd = context.getSystemService(NsdManager::class.java)
        val found = LinkedBlockingQueue<NsdServiceInfo>()
        val listener = object : NsdManager.DiscoveryListener {
            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                found.add(serviceInfo)
            }
            override fun onServiceLost(serviceInfo: NsdServiceInfo) = Unit
            override fun onDiscoveryStarted(serviceType: String) = Unit
            override fun onDiscoveryStopped(serviceType: String) = Unit
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                Log.w(TAG, "Discovery of $serviceType failed: $errorCode")
            }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) = Unit
        }

        nsd.discoverServices(serviceType, NsdManager.PROTOCOL_DNS_SD, listener)
        val deadline = System.currentTimeMillis() + timeoutMs
        try {
            while (true) {
                val remaining = deadline - System.currentTimeMillis()
                if (remaining <= 0) return null
                val candidate = found.poll(remaining, TimeUnit.MILLISECONDS) ?: return null
                val resolved = resolve(nsd, candidate, remaining.coerceAtMost(5_000)) ?: continue
                if (own in addressesOf(resolved)) return resolved.port
            }
        } finally {
            try {
                nsd.stopServiceDiscovery(listener)
            } catch (_: IllegalArgumentException) {
            }
        }
    }

    @Suppress("DEPRECATION")
    private fun resolve(nsd: NsdManager, service: NsdServiceInfo, timeoutMs: Long): NsdServiceInfo? {
        var result: NsdServiceInfo? = null
        val latch = CountDownLatch(1)
        nsd.resolveService(service, object : NsdManager.ResolveListener {
            override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                result = serviceInfo
                latch.countDown()
            }
            override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                latch.countDown()
            }
        })
        latch.await(timeoutMs, TimeUnit.MILLISECONDS)
        return result
    }

    @Suppress("DEPRECATION")
    private fun addressesOf(info: NsdServiceInfo): List<InetAddress> =
        if (Build.VERSION.SDK_INT >= 34) info.hostAddresses else listOfNotNull(info.host)
}
