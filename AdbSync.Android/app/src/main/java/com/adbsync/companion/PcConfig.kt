package com.adbsync.companion

import android.content.Context
import android.content.SharedPreferences

data class PcConfig(val url: String, val token: String, val certSha256: String, val pcName: String) {
    companion object {
        private const val PREFS = "pc"
        private const val KEY_URL = "url"
        private const val KEY_TOKEN = "token"
        private const val KEY_FP = "fp"
        private const val KEY_PC = "pc"

        fun prefs(context: Context): SharedPreferences =
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

        fun load(context: Context): PcConfig? {
            val p = prefs(context)
            val url = p.getString(KEY_URL, null) ?: return null
            val token = p.getString(KEY_TOKEN, null) ?: return null
            val fp = p.getString(KEY_FP, null) ?: return null
            if (!url.startsWith("https://")) return null
            return PcConfig(url, token, fp, p.getString(KEY_PC, null) ?: "PC")
        }

        /** Returns true if anything changed. */
        fun save(context: Context, config: PcConfig): Boolean {
            if (load(context) == config) return false
            prefs(context).edit()
                .putString(KEY_URL, config.url)
                .putString(KEY_TOKEN, config.token)
                .putString(KEY_FP, config.certSha256.lowercase())
                .putString(KEY_PC, config.pcName)
                .apply()
            return true
        }
    }
}
