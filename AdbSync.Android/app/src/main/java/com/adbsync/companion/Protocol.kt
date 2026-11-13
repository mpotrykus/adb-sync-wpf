package com.adbsync.companion

import android.content.Intent

object Protocol {
    const val ACTION = "com.adbsync.companion.EVENT"

    const val EXTRA_EVENT = "event"
    const val EXTRA_JOB = "job"
    const val EXTRA_PHASE = "phase"
    const val EXTRA_DEVICE = "device"
    const val EXTRA_MESSAGE = "message"
    const val EXTRA_PUSHED = "pushed"
    const val EXTRA_URL = "url"
    const val EXTRA_TOKEN = "token"
    const val EXTRA_FP = "fp"
    const val EXTRA_PC = "pc"

    const val EVENT_CONFIG = "config"

    const val EVENT_PHASE = "phase"
    const val EVENT_QUEUED = "queued"
    const val EVENT_HEARTBEAT = "heartbeat"
    const val EVENT_COMPLETED = "completed"
    const val EVENT_FAILED = "failed"
    const val EVENT_CANCELLED = "cancelled"
    const val EVENT_SKIPPED = "skipped"

    private val terminalEvents = setOf(EVENT_COMPLETED, EVENT_FAILED, EVENT_CANCELLED, EVENT_SKIPPED)

    fun isTerminal(event: String?) = event in terminalEvents

    fun phaseLabel(phase: String?): String = when (phase) {
        "PreConnect" -> "Connecting"
        "Pull" -> "Pulling"
        "Push" -> "Pushing"
        "Merge" -> "Merging"
        "Snapshot" -> "Saving rollback point"
        "WaitingForAppClose" -> "Waiting for app to close"
        "WaitingForDevice" -> "Waiting for device"
        null, "" -> "Running"
        else -> phase
    }
}

data class SyncEvent(
    val event: String,
    val job: String,
    val phase: String?,
    val device: String?,
    val message: String?,
    val pushed: Boolean,
) {
    fun writeTo(intent: Intent): Intent = intent
        .putExtra(Protocol.EXTRA_EVENT, event)
        .putExtra(Protocol.EXTRA_JOB, job)
        .putExtra(Protocol.EXTRA_PHASE, phase)
        .putExtra(Protocol.EXTRA_DEVICE, device)
        .putExtra(Protocol.EXTRA_MESSAGE, message)
        .putExtra(Protocol.EXTRA_PUSHED, pushed)

    companion object {
        fun from(intent: Intent): SyncEvent? {
            val event = intent.getStringExtra(Protocol.EXTRA_EVENT) ?: return null
            val job = intent.getStringExtra(Protocol.EXTRA_JOB) ?: return null
            return SyncEvent(
                event = event,
                job = job,
                phase = intent.getStringExtra(Protocol.EXTRA_PHASE),
                device = intent.getStringExtra(Protocol.EXTRA_DEVICE),
                message = intent.getStringExtra(Protocol.EXTRA_MESSAGE),
                pushed = intent.getBooleanExtra(Protocol.EXTRA_PUSHED, false),
            )
        }
    }
}
