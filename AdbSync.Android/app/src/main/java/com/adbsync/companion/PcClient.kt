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
    val pendingConflicts: Int = 0,
) {
    val isPushing get() = phase == "Push" || devices.any { it.second == "Push" }

    fun statusText(): String = when {
        running && devices.isNotEmpty() -> devices.joinToString("\n") { (device, phase) -> "${Protocol.phaseLabel(phase)} · $device" }
        running -> Protocol.phaseLabel(phase)
        else -> lastResult ?: "Never run"
    }

    companion object {
        /** Parses a v1 JobStatus. [deviceNames] maps device ids to the names the rest of the app works with. */
        fun parse(o: JSONObject, deviceNames: Map<String, String>): RemoteJob {
            val devices = o.optJSONArray("devices")
            val lastRun = o.optJSONObject("lastRun")
            val status = lastRun?.optStringOrNull("status")
            val checkpoint = o.optJSONObject("checkpoint")
            val stale = o.optJSONArray("staleDevices")?.let { array ->
                (0 until array.length()).map {
                    val d = array.getJSONObject(it)
                    (deviceNames[d.getString("deviceId")] ?: "A device") to d.getString("reason")
                }
            }.orEmpty()
            return RemoteJob(
                name = o.getString("name"),
                enabled = o.optBoolean("enabled"),
                running = o.optString("state", "idle") != "idle",
                phase = o.optStringOrNull("phase")?.capitalized(),
                devices = (0 until (devices?.length() ?: 0)).map {
                    val d = devices!!.getJSONObject(it)
                    (deviceNames[d.getString("deviceId")] ?: "A device") to d.getString("phase").capitalized()
                },
                lastResult = status?.let { outcomeLabel(it.capitalized()) },
                lastFailed = status == "failed",
                lastResultAt = lastRun?.optStringOrNull("finishedAt")?.let { OffsetDateTime.parse(it) },
                nextRunAt = o.optStringOrNull("nextRunAt")?.let { OffsetDateTime.parse(it) },
                needsAttention = status == "failed" || stale.isNotEmpty() ||
                    o.optJSONObject("availability")?.optString("projectFolder", "ok") != "ok",
                hasCheckpoint = checkpoint != null,
                checkpointSummary = checkpoint?.optStringOrNull("summary"),
                appPackage = o.optStringOrNull("appPackage"),
                staleDevices = stale,
                pendingConflicts = o.optInt("pendingConflicts"),
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

    fun outcomeText(): String = outcomeLabel(outcome)

    companion object {
        fun parse(o: JSONObject): RemoteRun {
            val queued = o.getString("queuedAt")
            val started = o.optStringOrNull("startedAt") ?: queued
            return RemoteRun(
                runId = o.getString("id"),
                startedAt = OffsetDateTime.parse(started),
                completedAt = OffsetDateTime.parse(o.optStringOrNull("finishedAt") ?: started),
                outcome = o.getString("status").capitalized(),
                errorMessage = o.optStringOrNull("message"),
                filesCopied = o.optInt("filesCopied"),
                filesDeleted = o.optInt("filesDeleted"),
                errorCount = o.optInt("errorCount"),
                bytesCopied = o.optLong("bytesCopied"),
            )
        }
    }
}

fun outcomeLabel(outcome: String): String = when (outcome) {
    "Completed" -> "Success"
    "CompletedNoChanges" -> "No changes"
    "Skipped" -> "Skipped"
    "SkippedAppRunning" -> "Skipped (app running)"
    "Failed" -> "Error"
    "DryRunCompleted" -> "Dry run"
    "Cancelled" -> "Stopped"
    "Queued" -> "Queued"
    "Running" -> "Running"
    else -> outcome
}

data class RemoteStep(
    val device: String,
    val phase: String,
    val status: String,
    val startedAt: OffsetDateTime?,
    val endedAt: OffsetDateTime?,
    val filesCopied: Int,
    val filesDeleted: Int,
    val bytesCopied: Long,
    val errors: Int,
    val conflicts: Int,
) {
    companion object {
        fun parse(o: JSONObject) = RemoteStep(
            device = o.getString("deviceName"),
            phase = o.getString("phase").capitalized(),
            status = o.getString("status").capitalized(),
            startedAt = o.optStringOrNull("startedAt")?.let { OffsetDateTime.parse(it) },
            endedAt = o.optStringOrNull("endedAt")?.let { OffsetDateTime.parse(it) },
            filesCopied = o.optInt("filesCopied"),
            filesDeleted = o.optInt("filesDeleted"),
            bytesCopied = o.optLong("bytesCopied"),
            errors = o.optInt("errors"),
            conflicts = o.optInt("conflicts"),
        )
    }
}

data class RemoteConflict(
    val id: String,
    val job: String,
    val device: String,
    val path: String,
    val deviceDeleted: Boolean,
    val deviceSize: Long?,
    val deviceModifiedAt: OffsetDateTime?,
    val pcDeleted: Boolean,
    val pcSize: Long?,
    val pcModifiedAt: OffsetDateTime?,
) {
    companion object {
        fun parse(o: JSONObject, jobNames: Map<String, String>, deviceNames: Map<String, String>): RemoteConflict {
            val master = o.getJSONObject("master")
            val device = o.getJSONObject("device")
            return RemoteConflict(
                id = o.getString("id"),
                job = jobNames[o.getString("jobId")] ?: "Unknown job",
                device = deviceNames[o.getString("deviceId")] ?: "Unknown device",
                path = o.getString("path"),
                deviceDeleted = device.optBoolean("deleted"),
                deviceSize = if (device.isNull("size")) null else device.optLong("size"),
                deviceModifiedAt = device.optStringOrNull("modifiedAt")?.let { OffsetDateTime.parse(it) },
                pcDeleted = master.optBoolean("deleted"),
                pcSize = if (master.isNull("size")) null else master.optLong("size"),
                pcModifiedAt = master.optStringOrNull("modifiedAt")?.let { OffsetDateTime.parse(it) },
            )
        }
    }
}

/** [PC] keeps the server's master copy, [DEVICE] keeps the version from the device the conflict was found on. */
enum class ConflictChoice(val wire: String) { PC("master"), DEVICE("device"), BOTH("both"), NEWEST("newest") }

data class CloseAndSyncResult(val result: String, val failed: Boolean, val stillOutOfSync: Boolean, val reason: String?)

private fun JSONObject.optStringOrNull(key: String): String? = if (!has(key) || isNull(key)) null else optString(key)

private fun String.capitalized() = replaceFirstChar { it.uppercaseChar() }

class HttpStatusException(val code: Int) : Exception("HTTP $code")

/** Job and device ids by name and back. The app works with names; the v1 API works with ids. */
private class Directory(val jobIds: Map<String, String>, val deviceIds: Map<String, String>, val fetchedAt: Long) {
    val jobNames = jobIds.entries.associate { (name, id) -> id to name }
    val deviceNames = deviceIds.entries.associate { (name, id) -> id to name }
}

/** Blocking calls; run them off the main thread. */
class PcClient(private val config: PcConfig) {

    fun run(job: String): Int = postJson("/api/v1/jobs/${jobId(job)}/runs", JSONObject().put("mode", "normal")).first

    fun stop(job: String): Int = postJson("/api/v1/jobs/${jobId(job)}/stop", null).first

    fun dryRun(job: String): Int = postJson("/api/v1/jobs/${jobId(job)}/runs", JSONObject().put("mode", "dryRun")).first

    fun discardCheckpoint(job: String): Int = send("DELETE", "/api/v1/jobs/${jobId(job)}/checkpoint", null).first

    fun setEnabled(job: String, enabled: Boolean): Int =
        send("PATCH", "/api/v1/jobs/${jobId(job)}", JSONObject().put("enabled", enabled), "application/merge-patch+json").first

    fun status(): List<RemoteJob> = parseStatus(getText("/api/v1/status", connectTimeoutMs = 2_500, readTimeoutMs = 4_000))

    private fun parseStatus(json: String): List<RemoteJob> {
        val jobs = JSONObject(json).getJSONArray("jobs")
        val names = directory().deviceNames
        return (0 until jobs.length()).map { RemoteJob.parse(jobs.getJSONObject(it), names) }
    }

    /** Closes the linked app on [device], syncs, and waits for the run to end. Throws [HttpStatusException] if the server refuses. */
    fun closeAndSync(job: String, device: String): CloseAndSyncResult {
        val (code, text) = postJson(
            "/api/v1/jobs/${jobId(job)}/app/close-and-sync", JSONObject().put("deviceId", deviceId(device)),
        )
        if (code !in 200..299) throw HttpStatusException(code)
        val operationId = JSONObject(text).getString("id")

        val deadline = System.currentTimeMillis() + 25 * 60_000L
        while (System.currentTimeMillis() < deadline) {
            val operation = JSONObject(getText("/api/v1/operations/$operationId"))
            when (operation.getString("status")) {
                "succeeded" -> {
                    val result = operation.optJSONObject("result")
                    val outcome = result?.optString("result").orEmpty()
                    return CloseAndSyncResult(
                        outcome, outcome.equals("failed", ignoreCase = true), result?.optBoolean("stillOutOfSync") == true,
                        result?.optStringOrNull("reason"),
                    )
                }
                "failed" -> return CloseAndSyncResult("failed", true, false, operation.optStringOrNull("message"))
            }
            Thread.sleep(2_000)
        }
        return CloseAndSyncResult("failed", true, false, "Timed out waiting for the sync")
    }

    fun conflicts(): List<RemoteConflict> {
        val array = JSONArray(getText("/api/v1/conflicts"))
        val dir = directory()
        return (0 until array.length()).map { RemoteConflict.parse(array.getJSONObject(it), dir.jobNames, dir.deviceNames) }
    }

    /** A small JPEG of one side of a conflict ("pc" or "device"), or null when it isn't an image (or that side was deleted). */
    fun conflictThumbnail(id: String, side: String): ByteArray? {
        val wireSide = if (side == "pc") "master" else side
        val conn = open("/api/v1/conflicts/${encode(id)}/thumbnail?side=$wireSide", readTimeoutMs = 15_000)
        try {
            if (conn.responseCode == 404) return null
            check(conn)
            return conn.inputStream.use { it.readBytes() }
        } finally {
            conn.disconnect()
        }
    }

    /** Returns the HTTP status and the server's message, if any. A successful resolve also queues the job. */
    fun resolveConflict(id: String, choice: ConflictChoice): Pair<Int, String> =
        postJson("/api/v1/conflicts/${encode(id)}/resolve", JSONObject().put("choice", choice.wire), readTimeoutMs = 30_000)

    /** Returns the HTTP status and the server's JSON reply, normalised to `resolved` and `failed` counts. */
    fun resolveAllConflicts(choice: ConflictChoice): Pair<Int, String> {
        val (code, text) = postJson("/api/v1/conflicts/resolve", JSONObject().put("choice", choice.wire), readTimeoutMs = 120_000)
        if (code !in 200..299) return code to text
        val o = JSONObject(text)
        return code to JSONObject().put("resolved", o.optInt("resolved")).put("failed", o.optJSONArray("failed")?.length() ?: 0).toString()
    }

    fun reportEndpoint(device: String, port: Int): Int =
        postJson("/api/v1/devices/${deviceId(device)}/endpoint", JSONObject().put("port", port)).first

    /** Returns the HTTP status and the server's error text, if any. */
    fun pair(device: String, port: Int, code: String): Pair<Int, String> =
        postJson("/api/v1/devices/${deviceId(device)}/pair", JSONObject().put("port", port).put("code", code), readTimeoutMs = 45_000)

    /** The devices the server knows, by name, for choosing which one this phone is. */
    fun deviceList(): List<RemoteDevice> {
        val array = JSONArray(getText("/api/v1/devices"))
        return (0 until array.length()).map {
            val d = array.getJSONObject(it)
            RemoteDevice(d.getString("name"), d.optStringOrNull("host"))
        }
    }

    private fun postJson(path: String, body: JSONObject?, readTimeoutMs: Int = 10_000): Pair<Int, String> =
        send("POST", path, body, readTimeoutMs = readTimeoutMs)

    private fun send(
        method: String, path: String, body: JSONObject?, contentType: String = "application/json", readTimeoutMs: Int = 10_000,
    ): Pair<Int, String> {
        val bytes = body?.toString()?.toByteArray(Charsets.UTF_8)
        val conn = open(path, readTimeoutMs)
        try {
            conn.requestMethod = method
            if (bytes != null) {
                conn.doOutput = true
                conn.setRequestProperty("Content-Type", contentType)
                conn.setFixedLengthStreamingMode(bytes.size)
                conn.outputStream.use { it.write(bytes) }
            } else if (method == "POST") {
                conn.doOutput = true
                conn.setFixedLengthStreamingMode(0)
                conn.outputStream.close()
            }
            val code = conn.responseCode
            val stream = if (code in 200..299) conn.inputStream else conn.errorStream
            val text = stream?.bufferedReader()?.use { it.readText() }.orEmpty().trim()
            return code to if (code in 200..299) text else problemText(text)
        } finally {
            conn.disconnect()
        }
    }

    /** The title and detail of a problem+json body, or the text as it came. */
    private fun problemText(text: String): String = try {
        val o = JSONObject(text)
        listOfNotNull(o.optStringOrNull("title"), o.optStringOrNull("detail")).distinct().joinToString(" ").ifBlank { text }
    } catch (_: Exception) {
        text
    }

    fun log(job: String): String {
        val run = runIdFor(job) ?: throw HttpStatusException(404)
        return getText("/api/v1/runs/$run/log")
    }

    fun runLog(job: String, runId: String): String = getText("/api/v1/runs/$runId/log")

    fun runSteps(job: String, runId: String): List<RemoteStep> = parseSteps(getText("/api/v1/runs/$runId"))

    /** The in-progress run's steps; empty when the job isn't running. */
    fun liveSteps(job: String): List<RemoteStep> {
        val active = statusFor(job)?.optStringOrNull("activeRunId") ?: return emptyList()
        return parseSteps(getText("/api/v1/runs/$active"))
    }

    private fun parseSteps(runDetailJson: String): List<RemoteStep> {
        val array = JSONObject(runDetailJson).optJSONArray("steps") ?: return emptyList()
        return (0 until array.length()).map { RemoteStep.parse(array.getJSONObject(it)) }
    }

    fun history(job: String): List<RemoteRun> {
        val items = JSONObject(getText("/api/v1/runs?jobId=${jobId(job)}&limit=50")).getJSONArray("items")
        return (0 until items.length()).map { RemoteRun.parse(items.getJSONObject(it)) }
            .filter { it.outcome != "Queued" && it.outcome != "Running" }
    }

    private fun statusFor(job: String): JSONObject? {
        val jobs = JSONObject(getText("/api/v1/status")).getJSONArray("jobs")
        return (0 until jobs.length()).map { jobs.getJSONObject(it) }.firstOrNull { it.getString("name") == job }
    }

    /** The run to show the log of: the active one, else the latest. */
    private fun runIdFor(job: String): String? {
        val status = statusFor(job) ?: return null
        return status.optStringOrNull("activeRunId") ?: status.optJSONObject("lastRun")?.optStringOrNull("runId")
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

    private fun jobId(name: String) = encode(directory().jobIds[name] ?: directory(refresh = true).jobIds[name] ?: throw HttpStatusException(404))

    private fun deviceId(name: String) = encode(directory().deviceIds[name] ?: directory(refresh = true).deviceIds[name] ?: throw HttpStatusException(404))

    private fun directory(refresh: Boolean = false): Directory {
        val cached = directories[config.url]
        if (!refresh && cached != null && System.currentTimeMillis() - cached.fetchedAt < DIRECTORY_TTL_MS) return cached
        val jobs = JSONArray(getText("/api/v1/jobs"))
        val devices = JSONArray(getText("/api/v1/devices"))
        val fresh = Directory(
            (0 until jobs.length()).associate { jobs.getJSONObject(it).let { j -> j.getString("name") to j.getString("id") } },
            (0 until devices.length()).associate { devices.getJSONObject(it).let { d -> d.getString("name") to d.getString("id") } },
            System.currentTimeMillis(),
        )
        synchronized(directories) { directories[config.url] = fresh }
        return fresh
    }

    interface FeedListener {
        fun onConnected()
        fun onSnapshot(pc: String, jobs: List<RemoteJob>)
        fun onJob(job: RemoteJob)
    }

    /** Reads the event stream until it ends or [shouldContinue] turns false. Throws on connection failure. */
    fun streamEvents(listener: FeedListener, onOpen: (HttpURLConnection) -> Unit, shouldContinue: () -> Boolean) {
        val conn = open("/api/v1/events", readTimeoutMs = 60_000)
        onOpen(conn)
        try {
            conn.setRequestProperty("Accept", "text/event-stream")
            check(conn)
            listener.onConnected()
            val snapshot = JSONObject(getText("/api/v1/status"))
            listener.onSnapshot(snapshot.optJSONObject("server")?.optString("name") ?: config.pcName, parseStatus(snapshot.toString()))
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
        val envelope = JSONObject(data)
        when (event) {
            "job.status" -> listener.onJob(RemoteJob.parse(envelope.getJSONObject("data"), directory().deviceNames))
            "job.changed", "job.deleted", "device.changed", "device.deleted", "resync" -> {
                directory(refresh = true)
                listener.onSnapshot(config.pcName, parseStatus(getText("/api/v1/status")))
            }
        }
    }

    private fun open(path: String, readTimeoutMs: Int, connectTimeoutMs: Int = 5_000): HttpURLConnection =
        openPinned(config.url, path, config.certSha256, readTimeoutMs, connectTimeoutMs).apply {
            setRequestProperty("Authorization", "Bearer ${config.token}")
        }

    private fun check(conn: HttpURLConnection) {
        val code = conn.responseCode
        if (code !in 200..299) throw HttpStatusException(code)
    }

    companion object {
        private const val DIRECTORY_TTL_MS = 30_000L
        private val directories = mutableMapOf<String, Directory>()
        private val factories = mutableMapOf<String, SSLSocketFactory>()

        private fun encode(s: String) = URLEncoder.encode(s, "UTF-8").replace("+", "%20")

        /** A connection to [path] under [url] that only trusts the certificate with [certSha256]. */
        fun openPinned(url: String, path: String, certSha256: String, readTimeoutMs: Int, connectTimeoutMs: Int = 5_000): HttpsURLConnection =
            (URL(url.trimEnd('/') + path).openConnection() as HttpsURLConnection).apply {
                sslSocketFactory = pinnedSocketFactory(certSha256)
                // The server is reached by a LAN address its self-signed cert can't name; trust comes from the
                // exact-certificate pin in PinnedTrustManager instead of hostname matching.
                hostnameVerifier = HostnameVerifier { _, _ -> true }
                connectTimeout = connectTimeoutMs
                readTimeout = readTimeoutMs
            }

        @Synchronized
        fun pinnedSocketFactory(sha256: String): SSLSocketFactory = factories.getOrPut(sha256.lowercase()) {
            SSLContext.getInstance("TLS").apply {
                init(null, arrayOf<TrustManager>(PinnedTrustManager(sha256)), null)
            }.socketFactory
        }
    }
}

data class RemoteDevice(val name: String, val host: String?)

class CertificatePinMismatchException : CertificateException("Server certificate doesn't match the pinned fingerprint")

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
