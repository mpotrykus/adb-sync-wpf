package com.adbsync.companion

import android.graphics.Canvas
import android.graphics.ColorFilter
import android.graphics.Paint
import android.graphics.PixelFormat
import android.graphics.PorterDuff
import android.graphics.PorterDuffXfermode
import android.graphics.drawable.Animatable
import android.graphics.drawable.Drawable
import android.os.SystemClock
import android.view.animation.PathInterpolator
import kotlin.math.cos
import kotlin.math.min
import kotlin.math.sin

/** The orbit -> merge -> burst sync mark; same timeline as the web's SyncMark component. */
class SyncMarkDrawable(
    private val violet: Int = 0xFF7C5CFF.toInt(),
    private val cyan: Int = 0xFF38BDF8.toInt(),
) : Drawable(), Animatable {
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val screen = PorterDuffXfermode(PorterDuff.Mode.SCREEN)
    private var running = false

    private val tick = object : Runnable {
        override fun run() {
            if (!running || !isVisible) return
            invalidateSelf()
            scheduleSelf(this, SystemClock.uptimeMillis() + FRAME_MS)
        }
    }

    override fun start() {
        if (running) return
        running = true
        scheduleSelf(tick, SystemClock.uptimeMillis())
    }

    override fun stop() {
        running = false
        unscheduleSelf(tick)
    }

    override fun isRunning() = running

    override fun setVisible(visible: Boolean, restart: Boolean): Boolean {
        val changed = super.setVisible(visible, restart)
        unscheduleSelf(tick)
        if (visible && running) scheduleSelf(tick, SystemClock.uptimeMillis())
        return changed
    }

    // Wall-clock phase, so a card rebuild doesn't restart the loop.
    override fun draw(canvas: Canvas) {
        val b = bounds
        if (b.isEmpty) return
        val p = (SystemClock.uptimeMillis() % PERIOD_MS) / PERIOD_MS.toFloat()
        val (radius, scale) = pose(p)
        val theta = Math.toRadians(720.0 * p)
        val x = (radius * cos(theta)).toFloat()
        val y = (radius * sin(theta)).toFloat()

        val saved = canvas.save()
        canvas.clipRect(b)
        canvas.translate(b.exactCenterX(), b.exactCenterY())
        val u = min(b.width(), b.height()) / 80f
        canvas.scale(u, u)
        diamond(canvas, x, y, scale, violet, 0.92f, false)
        diamond(canvas, -x, -y, scale, cyan, 0.85f, true)
        ring(canvas, p)
        core(canvas, p)
        canvas.restoreToCount(saved)
    }

    private fun diamond(canvas: Canvas, x: Float, y: Float, scale: Float, color: Int, alpha: Float, blend: Boolean) {
        paint.style = Paint.Style.FILL
        paint.color = color
        paint.xfermode = if (blend) screen else null
        val saved = canvas.save()
        canvas.translate(x, y)
        canvas.rotate(45f)
        canvas.scale(scale, scale)
        paint.alpha = (255 * 0.3f).toInt()
        canvas.drawRoundRect(-17f, -17f, 17f, 17f, 10f, 10f, paint)
        paint.alpha = (255 * alpha).toInt()
        canvas.drawRoundRect(-15f, -15f, 15f, 15f, 9f, 9f, paint)
        canvas.restoreToCount(saved)
        paint.xfermode = null
    }

    private fun ring(canvas: Canvas, p: Float) {
        if (p < 0.58f || p >= 0.80f) return
        val (scale, alpha) = when {
            p < 0.61f -> segment(p, 0.58f, 0.61f, 0.3f, 0.6f, 0f, 0.95f)
            p < 0.66f -> segment(p, 0.61f, 0.66f, 0.6f, 1.7f, 0.95f, 0.75f)
            p < 0.72f -> segment(p, 0.66f, 0.72f, 1.7f, 2.2f, 0.75f, 0.35f)
            else -> segment(p, 0.72f, 0.80f, 2.2f, 2.5f, 0.35f, 0f)
        }
        paint.style = Paint.Style.STROKE
        paint.color = 0xFFFFFFFF.toInt()
        paint.alpha = (255 * alpha).toInt()
        paint.strokeWidth = 1.6f * scale
        canvas.drawCircle(0f, 0f, 14f * scale, paint)
    }

    private fun segment(p: Float, p0: Float, p1: Float, s0: Float, s1: Float, a0: Float, a1: Float): Pair<Float, Float> {
        val k = (p - p0) / (p1 - p0)
        return (s0 + (s1 - s0) * k) to (a0 + (a1 - a0) * k)
    }

    private fun core(canvas: Canvas, p: Float) {
        val lift = when {
            p < 0.40f -> 0f
            p < 0.62f -> smooth((p - 0.40f) / 0.22f)
            p < 0.82f -> 1f - smooth((p - 0.62f) / 0.20f)
            else -> 0f
        }
        paint.style = Paint.Style.FILL
        paint.color = 0xFFFFFFFF.toInt()
        paint.alpha = (255 * (0.3f + 0.7f * lift)).toInt()
        canvas.drawCircle(0f, 0f, 3.2f * (0.8f + 1.2f * lift), paint)
    }

    private fun pose(p: Float): Pair<Float, Float> = when {
        p < 0.38f -> ORBIT to 1f
        p < 0.60f -> {
            val t = SPIRAL_IN.getInterpolation((p - 0.38f) / 0.22f)
            ORBIT * (1f - t) to 1f + 0.08f * t
        }
        p < 0.65f -> 0f to 1.08f + 0.08f * EASE_OUT.getInterpolation((p - 0.60f) / 0.05f)
        p < 0.88f -> {
            val t = SPIRAL_OUT.getInterpolation((p - 0.65f) / 0.23f)
            ORBIT * t to 1.16f - 0.16f * t
        }
        else -> ORBIT to 1f
    }

    private fun smooth(t: Float) = t * t * (3f - 2f * t)

    override fun setAlpha(alpha: Int) {}
    override fun setColorFilter(colorFilter: ColorFilter?) {}
    @Deprecated("Deprecated in Java")
    override fun getOpacity() = PixelFormat.TRANSLUCENT

    private companion object {
        const val PERIOD_MS = 4800L
        const val FRAME_MS = 16L
        const val ORBIT = 13f
        val SPIRAL_IN = PathInterpolator(0.55f, 0f, 0.9f, 0.6f)
        val EASE_OUT = PathInterpolator(0f, 0f, 0.58f, 1f)
        val SPIRAL_OUT = PathInterpolator(0.1f, 0.5f, 0.3f, 1f)
    }
}
