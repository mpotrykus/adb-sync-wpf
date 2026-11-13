package com.adbsync.companion

import org.json.JSONArray
import org.json.JSONObject
import java.time.OffsetDateTime

data class DeviceInfo(
    val id: String,
    val name: String,
    val transport: String,
    val host: String?,
    val serial: String?,
    val staticPort: Int?,
    val usedByJobs: Int,
    val state: String,
    val endpoint: String?,
) {
    val address get() = host ?: serial ?: "no address"
    val isUsb get() = transport == "usb"
}

data class ConnectionTest(val connected: Boolean, val endpoint: String?, val detail: String?)

data class DiscoveredDevice(val source: String, val label: String, val serial: String?, val endpoint: String?, val matchedDeviceId: String?)

data class RunListItem(val run: RemoteRun, val jobName: String, val mode: String, val trigger: String) {
    val live get() = run.outcome == "Queued" || run.outcome == "Running"
}

data class RunsPage(val items: List<RunListItem>, val nextCursor: String?)

data class RunFile(val device: String?, val phase: String?, val path: String, val action: String, val size: Long?, val detail: String?)

data class RunFiles(val retained: Boolean, val items: List<RunFile>)

data class FolderEntry(val name: String, val isDirectory: Boolean, val size: Long?)

data class FolderListing(val path: String, val parent: String?, val entries: List<FolderEntry>)

data class Snapshot(val id: String, val rollbackPoint: Boolean, val createdAt: OffsetDateTime, val devices: List<String>)

data class OperationOutcome(val succeeded: Boolean, val message: String?, val result: JSONObject?)

fun PcClient.jobNames(): Map<String, String> = directory().jobIds.entries.associate { (name, id) -> id to name }

fun PcClient.jobIdOf(name: String): String =
    directory().jobIds[name] ?: directory(refresh = true).jobIds[name] ?: throw HttpStatusException(404)

fun PcClient.refreshDirectory() {
    directory(refresh = true)
}

private fun JSONArray.objects(): List<JSONObject> = (0 until length()).map { getJSONObject(it) }

private fun JSONObject.stringList(key: String): List<String> =
    optJSONArray(key)?.let { array -> (0 until array.length()).map { array.getString(it) } }.orEmpty()

private fun checked(result: Pair<Int, String>): String {
    if (result.first !in 200..299) throw ApiException(result.first, result.second)
    return result.second
}

/** A failed call, carrying the server's explanation when it gave one. */
class ApiException(val code: Int, message: String) : Exception(message.ifBlank { "HTTP $code" })

fun PcClient.runsPage(jobIds: Set<String>, cursor: String?, limit: Int = 30): RunsPage {
    val query = buildString {
        append("/api/v1/runs?limit=").append(limit)
        jobIds.forEach { append("&jobId=").append(PcClient.encode(it)) }
        if (cursor != null) append("&cursor=").append(PcClient.encode(cursor))
    }
    val page = JSONObject(getText(query))
    val items = page.getJSONArray("items").objects().map {
        RunListItem(RemoteRun.parse(it), it.optString("jobName"), it.optString("mode", "normal"), it.optString("trigger"))
    }
    return RunsPage(items, page.optStringOrNull("nextCursor"))
}

fun PcClient.runFiles(runId: String): RunFiles {
    val names = directory().deviceNames
    val page = JSONObject(getText("/api/v1/runs/$runId/files", readTimeoutMs = 20_000))
    return RunFiles(
        page.optBoolean("retained"),
        page.getJSONArray("items").objects().map {
            RunFile(
                names[it.optStringOrNull("deviceId")], it.optStringOrNull("phase"), it.getString("path"), it.getString("action"),
                if (it.isNull("size")) null else it.optLong("size"), it.optStringOrNull("detail"),
            )
        },
    )
}

fun PcClient.devices(): List<DeviceInfo> = JSONArray(getText("/api/v1/devices", readTimeoutMs = 15_000)).objects().map {
    val connection = it.optJSONObject("connection")
    DeviceInfo(
        it.getString("id"), it.getString("name"), it.optString("transport", "wifi"), it.optStringOrNull("host"),
        it.optStringOrNull("serial"), if (it.isNull("staticPort")) null else it.optInt("staticPort"),
        it.optJSONArray("usedByJobIds")?.length() ?: 0, connection?.optString("state", "unknown") ?: "unknown",
        connection?.optStringOrNull("endpoint"),
    )
}

fun PcClient.testDevice(id: String): ConnectionTest {
    val o = JSONObject(checked(postJson("/api/v1/devices/$id/test", null, readTimeoutMs = 45_000)))
    return ConnectionTest(o.optBoolean("connected"), o.optStringOrNull("endpoint"), o.optStringOrNull("detail"))
}

/** Creates the device when [id] is null, otherwise updates it. Throws [ApiException] with the server's reason. */
fun PcClient.saveDevice(id: String?, name: String, transport: String, host: String?, serial: String?, staticPort: Int?) {
    val body = JSONObject()
        .put("name", name).put("transport", transport)
        .put("host", if (transport == "wifi") host else JSONObject.NULL)
        .put("serial", if (transport == "usb") serial else JSONObject.NULL)
        .put("staticPort", if (transport == "wifi" && staticPort != null) staticPort else JSONObject.NULL)
    if (id == null) {
        checked(postJson("/api/v1/devices", body))
    } else {
        checked(send("PATCH", "/api/v1/devices/$id", body, "application/merge-patch+json"))
    }
    refreshDirectory()
}

fun PcClient.deleteDevice(id: String) {
    checked(send("DELETE", "/api/v1/devices/$id", null))
    refreshDirectory()
}

fun PcClient.pairDevice(id: String, host: String?, port: Int, code: String) {
    val body = JSONObject().put("port", port).put("code", code)
    if (!host.isNullOrBlank()) body.put("host", host)
    checked(postJson("/api/v1/devices/$id/pair", body, readTimeoutMs = 60_000))
}

fun PcClient.discoveredDevices(): List<DiscoveredDevice> =
    JSONArray(getText("/api/v1/devices/discovered", readTimeoutMs = 20_000)).objects().map {
        DiscoveredDevice(
            it.optString("source"), it.optString("label"), it.optStringOrNull("serial"), it.optStringOrNull("endpoint"),
            it.optStringOrNull("matchedDeviceId"),
        )
    }

fun PcClient.browse(deviceId: String, path: String): FolderListing {
    val o = JSONObject(getText("/api/v1/devices/$deviceId/browse?path=${PcClient.encode(path)}", readTimeoutMs = 60_000))
    return FolderListing(
        o.getString("path"), o.optStringOrNull("parent"),
        o.getJSONArray("entries").objects().map {
            FolderEntry(it.getString("name"), it.optBoolean("isDirectory"), if (it.isNull("size")) null else it.optLong("size"))
        },
    )
}

fun PcClient.packages(deviceId: String): List<String> =
    JSONArray(getText("/api/v1/devices/$deviceId/packages", readTimeoutMs = 60_000)).let { array ->
        (0 until array.length()).map { array.getString(it) }
    }

fun PcClient.jobConfig(jobId: String): JSONObject = JSONObject(getText("/api/v1/jobs/$jobId"))

fun PcClient.createJob(body: JSONObject) {
    checked(postJson("/api/v1/jobs", body))
    refreshDirectory()
}

fun PcClient.patchJob(jobId: String, body: JSONObject) {
    checked(send("PATCH", "/api/v1/jobs/$jobId", body, "application/merge-patch+json"))
    refreshDirectory()
}

fun PcClient.deleteJob(jobId: String) {
    checked(send("DELETE", "/api/v1/jobs/$jobId", null))
    refreshDirectory()
}

fun PcClient.runAll(): Int = postJson("/api/v1/jobs/run-all", null).first

fun PcClient.schedulerPaused(): Boolean = JSONObject(getText("/api/v1/scheduler")).optBoolean("paused")

fun PcClient.setSchedulerPaused(paused: Boolean) {
    checked(send("PATCH", "/api/v1/scheduler", JSONObject().put("paused", paused), "application/merge-patch+json"))
}

fun PcClient.openApp(jobId: String, deviceId: String) {
    checked(postJson("/api/v1/jobs/$jobId/app/open", JSONObject().put("deviceId", deviceId), readTimeoutMs = 30_000))
}

fun PcClient.snapshots(jobId: String): List<Snapshot> =
    JSONArray(getText("/api/v1/jobs/$jobId/snapshots", readTimeoutMs = 20_000)).objects().map {
        val names = directory().deviceNames
        Snapshot(
            it.getString("id"), it.optString("kind") == "rollbackPoint", OffsetDateTime.parse(it.getString("createdAt")),
            it.stringList("deviceIds").map { id -> names[id] ?: "A device" },
        )
    }

/** Starts the operation and returns its id; follow it with [awaitOperation]. */
fun PcClient.createSnapshot(jobId: String): String =
    JSONObject(checked(postJson("/api/v1/jobs/$jobId/snapshots", null))).getString("id")

fun PcClient.restoreSnapshot(jobId: String, snapshotId: String): String =
    JSONObject(checked(postJson("/api/v1/jobs/$jobId/snapshots/${PcClient.encode(snapshotId)}/restore", null))).getString("id")

fun PcClient.awaitOperation(operationId: String, timeoutMs: Long = 30 * 60_000L, onProgress: (String) -> Unit = {}): OperationOutcome {
    val deadline = System.currentTimeMillis() + timeoutMs
    while (System.currentTimeMillis() < deadline) {
        val o = JSONObject(getText("/api/v1/operations/$operationId"))
        o.optJSONObject("progress")?.optStringOrNull("message")?.let(onProgress)
        when (o.optString("status")) {
            "succeeded" -> return OperationOutcome(true, null, o.optJSONObject("result"))
            "failed" -> return OperationOutcome(false, o.optStringOrNull("message") ?: "It failed.", null)
        }
        Thread.sleep(1_500)
    }
    return OperationOutcome(false, "Timed out waiting for the server.", null)
}

fun PcClient.settings(): JSONObject = JSONObject(getText("/api/v1/settings"))

fun PcClient.patchSettings(patch: JSONObject) {
    checked(send("PATCH", "/api/v1/settings", patch, "application/merge-patch+json"))
}

data class JobMeta(val id: String, val name: String, val schedule: String, val devices: List<Pair<String, String>>)

private fun scheduleText(s: JSONObject?): String {
    if (s == null) return "-"
    return when (val kind = s.optString("kind")) {
        "onChange" -> "When a phone changes"
        "interval" -> {
            val minutes = s.optInt("intervalSeconds") / 60
            if (minutes >= 60 && minutes % 60 == 0) "Every ${minutes / 60} h" else "Every $minutes min"
        }
        "dailyAt" -> "Daily at " + (s.optJSONArray("dailyTimes")?.let { a -> (0 until a.length()).joinToString(", ") { a.getString(it) } } ?: "")
        "manual" -> "Manual only"
        else -> kind
    }
}

fun PcClient.jobMetas(): List<JobMeta> {
    val names = directory().deviceNames
    return JSONArray(getText("/api/v1/jobs")).objects().map { job ->
        JobMeta(
            job.getString("id"), job.getString("name"), scheduleText(job.optJSONObject("schedule")),
            (job.optJSONArray("devices")?.objects().orEmpty()).map {
                val id = it.getString("deviceId")
                id to (names[id] ?: "A device")
            },
        )
    }
}

/** Starts closing the linked app on the device and syncing; the server carries on without us. */
fun PcClient.closeAppAndSync(jobId: String, deviceId: String) {
    checked(postJson("/api/v1/jobs/$jobId/app/close-and-sync", JSONObject().put("deviceId", deviceId), readTimeoutMs = 30_000))
}
