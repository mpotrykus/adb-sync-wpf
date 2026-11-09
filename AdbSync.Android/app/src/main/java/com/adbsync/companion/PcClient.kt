package com.adbsync.companion

import org.json.JSONArray
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.time.OffsetDateTime
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
    val lastResultAt: OffsetDateTime?,
    val nextRunAt: OffsetDateTime?,
    val needsAttention: Boolean,
    val hasCheckpoint: Boolean,
    val checkpointSummary: String?,
    val appPackage: String?,
    val staleDevices: List<Pair<String, String>>,
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
                lastResultAt = o.optStringOrNull("lastResultAt")?.let { OffsetDateTime.parse(it) },
                nextRunAt = o.optStringOrNull("nextRunAt")?.let { OffsetDateTime.parse(it) },
                needsAttention = o.optBoolean("needsAttention"),
                hasCheckpoint = o.optBoolean("hasCheckpoint"),
                checkpointSummary = o.optStringOrNull("checkpointSummary"),
                appPackage = o.optStringOrNull("appPackage"),
                staleDevices = o.optJSONArray("staleDevices")?.let { stale ->
                    (0 until stale.length()).map {
                        val d = stale.getJSONObject(it)
                        d.getString("device") to d.getString("reason")
                    }
                }.orEmpty(),
            )
        }
    }
}

data class RemoteRun(
    val runId: String,
    val startedAt: OffsetDateTime,
    val completedAt: OffsetDateTime,
    val outcome: String,
    val errorMessage: String?,
    val filesCopied: Int,
    val filesDeleted: Int,
    val errorCount: Int,
    val bytesCopied: Long,
) {
    val failed get() = outcome == "Failed"
    val skipped get() = outcome == "Skipped" || outcome == "SkippedAppRunning" || outcome == "Cancelled"

    fun outcomeText(): String = when (outcome) {
        "Completed" -> "Success"
        "CompletedNoChanges" -> "No changes"
        "Skipped" -> "Skipped"
        "SkippedAppRunning" -> "Skipped (app running)"
        "Failed" -> "Error"
        "DryRunCompleted" -> "Dry run"
        "Cancelled" -> "Stopped"
        else -> outcome
    }

    companion object {
        fun parse(o: JSONObject) = RemoteRun(
            runId = o.getString("runId"),
            startedAt = OffsetDateTime.parse(o.getString("startedAt")),
            completedAt = OffsetDateTime.parse(o.getString("completedAt")),
            outcome = o.getString("outcome"),
            errorMessage = o.optStringOrNull("errorMessage"),
            filesCopied = o.optInt("filesCopied"),
            filesDeleted = o.optInt("filesDeleted"),
            errorCount = o.optInt("errorCount"),
            bytesCopied = o.optLong("bytesCopied"),
        )
    }
}

data class CloseAndSyncResult(val result: String, val failed: Boolean, val stillOutOfSync: Boolean, val reason: String?)

private fun JSONObject.optStringOrNull(key: String): String? = if (isNull(key)) null else optString(key)

class HttpStatusException(val code: Int) : Exception("HTTP $code")

/** Blocking calls; run them off the main thread. */
class PcClient(private val config: PcConfig) {

    fun run(job: String) = post("/api/jobs/${encode(job)}/run")

    fun stop(job: String) = post("/api/jobs/${encode(job)}/stop")

    fun dryRun(job: String) = post("/api/jobs/${encode(job)}/dryrun")

    fun discardCheckpoint(job: String) = post("/api/jobs/${encode(job)}/checkpoint/discard")

    fun setEnabled(job: String, enabled: Boolean): Int =
        postJson("/api/jobs/${encode(job)}/enabled", JSONObject().put("enabled", enabled)).first

    fun status(): List<RemoteJob> {
        val jobs = JSONObject(getText("/api/status", connectTimeoutMs = 2_500, readTimeoutMs = 4_000)).getJSONArray("jobs")
        return (0 until jobs.length()).map { RemoteJob.parse(jobs.getJSONObject(it)) }
    }

    /** Closes the linked app on [device], syncs, and waits for the run to end. Throws [HttpStatusException] if the PC refuses. */
    fun closeAndSync(job: String, device: String): CloseAndSyncResult {
        val (code, text) = postJson(
            "/api/jobs/${encode(job)}/close-and-sync", JSONObject().put("device", device), readTimeoutMs = 25 * 60_000,
        )
        if (code !in 200..299) throw HttpStatusException(code)
        val o = JSONObject(text)
        return CloseAndSyncResult(o.getString("result"), o.optBoolean("failed"), o.optBoolean("stillOutOfSync"), o.optStringOrNull("reason"))
    }

    fun reportEndpoint(device: String, port: Int): Int =
        postJson("/api/devices/${encode(device)}/endpoint", JSONObject().put("port", port)).first

    /** Returns the HTTP status and the PC's error text, if any. */
    fun pair(device: String, port: Int, code: String): Pair<Int, String> =
        postJson("/api/devices/${encode(device)}/pair", JSONObject().put("port", port).put("code", code), readTimeoutMs = 45_000)

    private fun postJson(path: String, body: JSONObject, readTimeoutMs: Int = 10_000): Pair<Int, String> {
        val bytes = body.toString().toByteArray(Charsets.UTF_8)
        val conn = open(path, readTimeoutMs)
        try {
            conn.requestMethod = "POST"
            conn.doOutput = true
            conn.setRequestProperty("Content-Type", "application/json")
            conn.setFixedLengthStreamingMode(bytes.size)
            conn.outputStream.use { it.write(bytes) }
            val code = conn.responseCode
            val stream = if (code in 200..299) conn.inputStream else conn.errorStream
            val text = stream?.bufferedReader()?.use { it.readText() }.orEmpty().trim().removeSurrounding("\"")
            return code to text
        } finally {
            conn.disconnect()
        }
    }

    fun log(job: String): String = getText("/api/jobs/${encode(job)}/log")

    fun runLog(job: String, runId: String): String = getText("/api/jobs/${encode(job)}/history/$runId/log")

    fun history(job: String): List<RemoteRun> {
        val array = JSONArray(getText("/api/jobs/${encode(job)}/history"))
        return (0 until array.length()).map { RemoteRun.parse(array.getJSONObject(it)) }
    }

    private fun getText(path: String, connectTimeoutMs: Int = 5_000, readTimeoutMs: Int = 10_000): String {
        val conn = open(path, readTimeoutMs, connectTimeoutMs)
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

    private fun open(path: String, readTimeoutMs: Int, connectTimeoutMs: Int = 5_000): HttpURLConnection =
        (URL(config.url.trimEnd('/') + path).openConnection() as HttpsURLConnection).apply {
            sslSocketFactory = pinnedSocketFactory(config.certSha256)
            // The PC is reached by a DHCP-assigned IP that its self-signed cert can't name; trust comes from
            // the exact-certificate pin in PinnedTrustManager instead of hostname matching.
            hostnameVerifier = HostnameVerifier { _, _ -> true }
            connectTimeout = connectTimeoutMs
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
