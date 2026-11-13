package com.adbsync.companion

import android.net.Uri
import android.os.Build
import org.json.JSONObject

/** What a server's pairing QR code or link carries: where it is, the one-time code, and its certificate fingerprint. */
data class PairingLink(val url: String, val code: String, val certSha256: String) {
    companion object {
        /** Parses `adbsync://pair?url=…&code=…&fp=…`, or null when [text] isn't one. */
        fun parse(text: String): PairingLink? {
            val uri = runCatching { Uri.parse(text.trim()) }.getOrNull() ?: return null
            if (uri.scheme != "adbsync" || uri.host != "pair") return null
            val url = uri.getQueryParameter("url")?.takeIf { it.startsWith("https://") } ?: return null
            val code = uri.getQueryParameter("code")?.takeIf { it.isNotBlank() } ?: return null
            val fp = uri.getQueryParameter("fp")?.takeIf { it.length == 64 } ?: return null
            return PairingLink(url, code, fp.lowercase())
        }
    }
}

class EnrollmentException(message: String) : Exception(message)

object Enrollment {
    /** Blocking. Exchanges the one-time code for a token, trusting only the certificate in [link]. */
    fun pair(link: PairingLink, clientVersion: String?, clientName: String = Build.MODEL ?: "Android phone"): PcConfig {
        val body = JSONObject()
            .put("code", link.code)
            .put("clientName", clientName)
            .put("clientKind", "android")
            .put("clientVersion", clientVersion)
            .toString()
            .toByteArray(Charsets.UTF_8)

        val conn = PcClient.openPinned(link.url, "/api/v1/auth/pair", link.certSha256, readTimeoutMs = 15_000)
        val token: String
        try {
            conn.requestMethod = "POST"
            conn.doOutput = true
            conn.setRequestProperty("Content-Type", "application/json")
            conn.setFixedLengthStreamingMode(body.size)
            conn.outputStream.use { it.write(body) }
            val code = conn.responseCode
            val text = (if (code in 200..299) conn.inputStream else conn.errorStream)?.bufferedReader()?.use { it.readText() }.orEmpty()
            if (code !in 200..299) {
                throw EnrollmentException(
                    when (code) {
                        401 -> "That pairing code is wrong or has expired. Make a new one and try again."
                        429 -> "Too many attempts. Wait a minute and try again."
                        else -> "The server refused to pair (HTTP $code)."
                    },
                )
            }
            token = JSONObject(text).getString("token")
        } catch (e: CertificatePinMismatchException) {
            throw EnrollmentException("The server's certificate doesn't match the pairing link.")
        } finally {
            conn.disconnect()
        }

        val provisional = PcConfig(link.url, token, link.certSha256, "server", null)
        val serverName = runCatching {
            val c = PcClient.openPinned(link.url, "/api/v1/status", link.certSha256, readTimeoutMs = 10_000)
            c.setRequestProperty("Authorization", "Bearer $token")
            try {
                JSONObject(c.inputStream.bufferedReader().use { it.readText() }).optJSONObject("server")?.optString("name")
            } finally {
                c.disconnect()
            }
        }.getOrNull()
        return provisional.copy(pcName = serverName?.takeIf { it.isNotBlank() } ?: "server")
    }
}
