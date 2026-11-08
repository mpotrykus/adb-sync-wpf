package com.adbsync.companion

import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

data class RemoteJob(
    val name: String,
    val enabled: Boolean,
    val running: Boolean,
    val phase: String?,
    val devices: List<Pair<String, String>>,
    val lastResult: String?,
    val lastFailed: Boolean,
) {
    val isPushing get() = phase == "Push" || devices.any { it.second == "Push" }

    fun statusText(): String = when {
        running && devices.isNotEmpty() -> devices.joinToString("\n") { (device, phase) -> "${Protocol.phaseLabel(phase)} · $device" }
        running -> Protocol.phaseLabel(phase)
        else -> lastResult ?: "Never run"
    }

    companion object {
        fun parse(o: JSONObject): RemoteJob {
            val devices = o.optJSONArray("devices")
            return RemoteJob(
                name = o.getString("name"),
                enabled = o.optBoolean("enabled"),
                running = o.optBoolean("running"),
                phase = o.optStringOrNull("phase"),
                devices = (0 until (devices?.length() ?: 0)).map {
                    val d = devices!!.getJSONObject(it)
                    d.getString("device") to d.getString("phase")
                },
                lastResult = o.optStringOrNull("lastResult"),
                lastFailed = o.optBoolean("lastFailed"),
            )
        }
    }
}

private fun JSONObject.optStringOrNull(key: String): String? = if (isNull(key)) null else optString(key)

class HttpStatusException(val code: Int) : Exception("HTTP $code")

/** Blocking calls; run them off the main thread. */
class PcClient(private val config: PcConfig) {

    fun run(job: String) = post("/api/jobs/${encode(job)}/run")

    fun stop(job: String) = post("/api/jobs/${encode(job)}/stop")

    fun log(job: String): String {
        val conn = open("/api/jobs/${encode(job)}/log", readTimeoutMs = 10_000)
        try {
            check(conn)
            return conn.inputStream.bufferedReader().use { it.readText() }
        } finally {
            conn.disconnect()
        }
    }

    interface FeedListener {
        fun onConnected()
        fun onSnapshot(pc: String, jobs: List<RemoteJob>)
        fun onJob(job: RemoteJob)
    }

    /** Reads the SSE stream until it ends or [shouldContinue] turns false. Throws on connection failure. */
    fun streamEvents(listener: FeedListener, onOpen: (HttpURLConnection) -> Unit, shouldContinue: () -> Boolean) {
        val conn = open("/api/events", readTimeoutMs = 60_000)
        onOpen(conn)
        try {
            check(conn)
            listener.onConnected()
            BufferedReader(InputStreamReader(conn.inputStream, Charsets.UTF_8)).use { reader ->
                var event = "message"
                val data = StringBuilder()
                while (shouldContinue()) {
                    val line = reader.readLine() ?: break
                    when {
                        line.isEmpty() -> {
                            if (data.isNotEmpty()) dispatch(event, data.toString(), listener)
                            event = "message"
                            data.setLength(0)
                        }
                        line.startsWith(":") -> Unit
                        line.startsWith("event:") -> event = line.substring(6).trim()
                        line.startsWith("data:") -> {
                            if (data.isNotEmpty()) data.append('\n')
                            data.append(line.substring(5).trimStart())
                        }
                    }
                }
            }
        } finally {
            conn.disconnect()
        }
    }

    private fun dispatch(event: String, data: String, listener: FeedListener) {
        val o = JSONObject(data)
        when (event) {
            "snapshot" -> {
                val jobs = o.getJSONArray("jobs")
                listener.onSnapshot(o.optString("pc", config.pcName), (0 until jobs.length()).map { RemoteJob.parse(jobs.getJSONObject(it)) })
            }
            "job" -> listener.onJob(RemoteJob.parse(o))
        }
    }

    private fun post(path: String): Int {
        val conn = open(path, readTimeoutMs = 10_000)
        try {
            conn.requestMethod = "POST"
            conn.doOutput = true
            conn.setFixedLengthStreamingMode(0)
            conn.outputStream.close()
            return conn.responseCode
        } finally {
            conn.disconnect()
        }
    }

    private fun open(path: String, readTimeoutMs: Int): HttpURLConnection =
        (URL(config.url.trimEnd('/') + path).openConnection() as HttpsURLConnection).apply {
            sslSocketFactory = pinnedSocketFactory(config.certSha256)
            // The PC is reached by a DHCP-assigned IP that its self-signed cert can't name; trust comes from
            // the exact-certificate pin in PinnedTrustManager instead of hostname matching.
            hostnameVerifier = HostnameVerifier { _, _ -> true }
            connectTimeout = 5_000
            readTimeout = readTimeoutMs
            setRequestProperty("Authorization", "Bearer ${config.token}")
        }

    private fun check(conn: HttpURLConnection) {
        val code = conn.responseCode
        if (code !in 200..299) throw HttpStatusException(code)
    }

    private fun encode(s: String) = URLEncoder.encode(s, "UTF-8").replace("+", "%20")

    private companion object {
        private val factories = mutableMapOf<String, SSLSocketFactory>()

        @Synchronized
        fun pinnedSocketFactory(sha256: String): SSLSocketFactory = factories.getOrPut(sha256) {
            SSLContext.getInstance("TLS").apply {
                init(null, arrayOf<TrustManager>(PinnedTrustManager(sha256)), null)
            }.socketFactory
        }
    }
}

class CertificatePinMismatchException : CertificateException("PC certificate doesn't match the pinned fingerprint")

private class PinnedTrustManager(private val expectedSha256: String) : X509TrustManager {
    override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String) {
        val leaf = chain.firstOrNull() ?: throw CertificateException("Empty certificate chain")
        val actual = MessageDigest.getInstance("SHA-256").digest(leaf.encoded).joinToString("") { "%02x".format(it) }
        if (!MessageDigest.isEqual(actual.toByteArray(), expectedSha256.lowercase().toByteArray())) {
            throw CertificatePinMismatchException()
        }
        leaf.checkValidity()
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String) =
        throw CertificateException("Client certificates aren't used")

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}
