package com.adbsync.companion

import android.app.AlertDialog
import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Canvas
import android.graphics.ColorFilter
import android.graphics.Paint
import android.graphics.PixelFormat
import android.graphics.RadialGradient
import android.graphics.Shader
import android.graphics.Typeface
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.text.TextUtils
import android.view.Gravity
import android.view.View
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.Switch
import android.widget.TextView

/** Palette and components mirroring AdbSync.Web/wwwroot/css/app.css (the glass theme) so the phone matches the web UI. */
object Ui {
    const val BACKGROUND = 0xFF0D0A18.toInt()
    const val SURFACE = 0x12FFFFFF
    const val SURFACE_BORDER = 0x1AFFFFFF
    const val SURFACE_ALT = 0xF0271F38.toInt()
    const val SURFACE_ALT_BORDER = 0x40FFFFFF
    const val DIVIDER = 0x17FFFFFF

    const val ACCENT = 0xFF6C63FF.toInt()
    const val ACCENT_FROM = 0xFF7C5CFF.toInt()
    const val ACCENT_TO = 0xFF4C6FFF.toInt()
    const val DANGER = 0xFFFF6B6B.toInt()
    const val SUCCESS = 0xFF5BD98A.toInt()
    const val WARNING = 0xFFFFB454.toInt()

    const val TEXT_PRIMARY = 0xFFF5F3FA.toInt()
    const val TEXT_SECONDARY = 0xFF9C97B3.toInt()

    private fun Context.plex(weight: Int): Typeface = resources.getFont(
        when {
            weight >= 600 -> R.font.ibm_plex_sans_semibold
            weight >= 500 -> R.font.ibm_plex_sans_medium
            else -> R.font.ibm_plex_sans_regular
        },
    )

    enum class ButtonKind { Neutral, Success, Danger, Primary }

    fun Context.dp(value: Int) = (value * resources.displayMetrics.density).toInt()

    /** The web UI's body.glass backdrop: near-black violet with four soft colored glows. */
    fun windowBackground(): Drawable = object : Drawable() {
        private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
        private val glows = listOf(
            Triple(0.12f to 0.08f, 0.60f, 0x33 shl 24 or 0x7C5CFF),
            Triple(0.88f to 0.18f, 0.50f, 0x24 shl 24 or 0x4C6FFF),
            Triple(0.70f to 0.95f, 0.55f, 0x1F shl 24 or 0xA855F7),
            Triple(0.05f to 0.85f, 0.45f, 0x12 shl 24 or 0x38BDF8),
        )

        override fun draw(canvas: Canvas) {
            canvas.drawColor(BACKGROUND)
            val w = bounds.width().toFloat()
            val h = bounds.height().toFloat()
            for ((center, radius, color) in glows) {
                val r = radius * maxOf(w, h)
                paint.shader = RadialGradient(center.first * w, center.second * h, r, color, color and 0x00FFFFFF, Shader.TileMode.CLAMP)
                canvas.drawRect(bounds, paint)
            }
        }

        override fun setAlpha(alpha: Int) {}
        override fun setColorFilter(colorFilter: ColorFilter?) {}
        @Suppress("OVERRIDE_DEPRECATION")
        override fun getOpacity() = PixelFormat.OPAQUE
    }

    fun Context.card(): LinearLayout = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        background = glass(SURFACE, SURFACE_BORDER, dp(8), dp(1))
        setPadding(dp(16), dp(14), dp(16), dp(14))
        layoutParams = LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply { bottomMargin = dp(12) }
    }

    fun Context.title(text: String) = TextView(this).apply {
        this.text = text
        textSize = 22f
        typeface = plex(600)
        setTextColor(TEXT_PRIMARY)
    }

    /** SectionLabel: card heading. */
    fun Context.sectionLabel(text: String) = TextView(this).apply {
        this.text = text
        textSize = 15f
        typeface = plex(600)
        setTextColor(TEXT_PRIMARY)
    }

    /** SubSectionLabel: uppercase group header in secondary text. */
    fun Context.subSectionLabel(text: String) = TextView(this).apply {
        this.text = text.uppercase()
        textSize = 11f
        typeface = plex(600)
        letterSpacing = 0.06f
        setTextColor(TEXT_SECONDARY)
        setPadding(dp(4), dp(20), 0, dp(8))
    }

    fun Context.body(text: CharSequence = "", color: Int = TEXT_PRIMARY, size: Float = 14f) = TextView(this).apply {
        this.text = text
        textSize = size
        setTextColor(color)
        setLineSpacing(0f, 1.1f)
    }

    fun Context.caption(text: CharSequence = "") = body(text, TEXT_SECONDARY, 12f)

    fun Context.dot(color: Int) = View(this).apply {
        background = GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(color)
        }
        layoutParams = LinearLayout.LayoutParams(dp(7), dp(7)).apply {
            marginEnd = dp(8)
            gravity = Gravity.CENTER_VERTICAL
        }
    }

    /** A dot followed by text, e.g. the dashboard's STATUS column. */
    fun Context.statusLine(text: CharSequence, color: Int, textColor: Int = color) = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
        addView(dot(color))
        addView(body(text, textColor, 13f).apply {
            maxLines = 3
            ellipsize = TextUtils.TruncateAt.END
        })
    }

    /** RowButton / RowSuccessButton / RowDangerButton / PrimaryButton from the PC theme. */
    fun Context.button(text: String, kind: ButtonKind = ButtonKind.Neutral, fill: Boolean = false, onClick: () -> Unit) =
        TextView(this).apply {
            this.text = text
            textSize = 13f
            typeface = plex(600)
            gravity = Gravity.CENTER
            minHeight = dp(36)
            setPadding(dp(14), dp(8), dp(14), dp(8))
            isClickable = true
            isFocusable = true
            val radius = dp(if (kind == ButtonKind.Primary) 6 else 5)
            val (content, textColor) = when (kind) {
                ButtonKind.Neutral -> stroked(0x14FFFFFF, 0x26FFFFFF, radius) to TEXT_PRIMARY
                ButtonKind.Success -> stroked(0x1F5BD98A, 0x335BD98A, radius) to SUCCESS
                ButtonKind.Danger -> stroked(0x33FF6B6B, 0x66FF6B6B, radius) to DANGER
                ButtonKind.Primary -> GradientDrawable(
                    GradientDrawable.Orientation.TL_BR, intArrayOf(ACCENT_FROM, ACCENT_TO),
                ).apply { cornerRadius = radius.toFloat() } to 0xFFFFFFFF.toInt()
            }
            setTextColor(textColor)
            background = RippleDrawable(ColorStateList.valueOf(0x33FFFFFF), content, rounded(0xFFFFFFFF.toInt(), radius))
            layoutParams = LinearLayout.LayoutParams(if (fill) MATCH_PARENT else WRAP_CONTENT, WRAP_CONTENT).apply {
                if (!fill) marginEnd = dp(8)
            }
            setOnClickListener { onClick() }
        }

    /** ToggleSwitch: accent track when on, faint white track when off, white thumb. */
    fun Context.toggle(text: String) = Switch(this).apply {
        this.text = text
        textSize = 14f
        typeface = plex(400)
        setTextColor(TEXT_PRIMARY)
        thumbTintList = ColorStateList.valueOf(0xFFFFFFFF.toInt())
        trackTintList = ColorStateList(
            arrayOf(intArrayOf(android.R.attr.state_checked), intArrayOf()),
            intArrayOf(ACCENT, 0x40FFFFFF),
        )
        setPadding(0, dp(4), 0, dp(4))
    }

    fun Context.indeterminateBar() = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal).apply {
        isIndeterminate = true
        indeterminateTintList = ColorStateList.valueOf(ACCENT)
        layoutParams = LinearLayout.LayoutParams(MATCH_PARENT, dp(4)).apply { topMargin = dp(10) }
    }

    fun Context.divider() = View(this).apply {
        setBackgroundColor(DIVIDER)
        layoutParams = LinearLayout.LayoutParams(MATCH_PARENT, 1).apply {
            topMargin = dp(12)
            bottomMargin = dp(12)
        }
    }

    /** Dialogs use the PC's Surface.Alt popup color. */
    fun AlertDialog.styled(): AlertDialog = apply {
        setOnShowListener {
            window?.setBackgroundDrawable(glass(SURFACE_ALT, SURFACE_ALT_BORDER, context.dp(8), context.dp(1)))
            getButton(AlertDialog.BUTTON_POSITIVE)?.setTextColor(ACCENT)
            getButton(AlertDialog.BUTTON_NEGATIVE)?.setTextColor(TEXT_SECONDARY)
        }
    }

    fun rounded(color: Int, radius: Int): Drawable = GradientDrawable().apply {
        setColor(color)
        cornerRadius = radius.toFloat()
    }

    fun glass(fill: Int, stroke: Int, radius: Int, strokeWidth: Int): Drawable = GradientDrawable().apply {
        setColor(fill)
        setStroke(strokeWidth, stroke)
        cornerRadius = radius.toFloat()
    }

    private fun stroked(fill: Int, stroke: Int, radius: Int) = glass(fill, stroke, radius, 2)

    /** The app bar: brand mark and name on the left, [action] (e.g. the web UI link) on the right. */
    fun Context.header(action: View?): LinearLayout = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
        addView(View(context).apply {
            background = GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(ACCENT_FROM, ACCENT_TO))
                .apply { cornerRadius = dp(5).toFloat() }
            layoutParams = LinearLayout.LayoutParams(dp(14), dp(14)).apply { marginEnd = dp(10) }
        })
        addView(title("AdbSync"), LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f))
        if (action != null) addView(action)
    }

    /** Colors a last-result string the way the dashboard's LAST RESULT column does. */
    fun resultColor(result: String?, failed: Boolean): Int = when {
        failed || result?.startsWith("Error") == true -> DANGER
        result == null -> TEXT_SECONDARY
        result.startsWith("Success") || result.startsWith("No changes") -> SUCCESS
        result.startsWith("Stopped") || result.startsWith("Skipped") -> WARNING
        else -> TEXT_SECONDARY
    }
}
