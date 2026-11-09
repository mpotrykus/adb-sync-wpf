package com.adbsync.companion

import android.app.AlertDialog
import android.content.Context
import android.content.res.ColorStateList
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

/** Palette and components mirroring AdbSync.App/Themes/Theme.xaml so the phone matches the PC app. */
object Ui {
    const val BACKGROUND_TOP = 0xFF1A1625.toInt()
    const val BACKGROUND_BOTTOM = 0xFF0D0B12.toInt()
    const val SURFACE = 0x14FFFFFF
    const val SURFACE_ALT = 0xF0271F38.toInt()
    const val DIVIDER = 0x16FFFFFF

    const val ACCENT = 0xFF6C63FF.toInt()
    const val ACCENT_FROM = 0xFF7C5CFF.toInt()
    const val ACCENT_TO = 0xFF4C6FFF.toInt()
    const val DANGER = 0xFFFF6B6B.toInt()
    const val SUCCESS = 0xFF5BD98A.toInt()
    const val WARNING = 0xFFFFB454.toInt()

    const val TEXT_PRIMARY = 0xFFF5F3FA.toInt()
    const val TEXT_SECONDARY = 0xFF9C97B3.toInt()

    private val semiBold: Typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)

    enum class ButtonKind { Neutral, Success, Danger, Primary }

    fun Context.dp(value: Int) = (value * resources.displayMetrics.density).toInt()

    fun windowBackground() = GradientDrawable(
        GradientDrawable.Orientation.TOP_BOTTOM, intArrayOf(BACKGROUND_TOP, BACKGROUND_BOTTOM),
    )

    fun Context.card(): LinearLayout = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        background = rounded(SURFACE, dp(8))
        setPadding(dp(16), dp(14), dp(16), dp(14))
        layoutParams = LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply { bottomMargin = dp(12) }
    }

    fun Context.title(text: String) = TextView(this).apply {
        this.text = text
        textSize = 22f
        typeface = semiBold
        setTextColor(TEXT_PRIMARY)
    }

    /** SectionLabel: card heading. */
    fun Context.sectionLabel(text: String) = TextView(this).apply {
        this.text = text
        textSize = 15f
        typeface = semiBold
        setTextColor(TEXT_PRIMARY)
    }

    /** SubSectionLabel: uppercase group header in secondary text. */
    fun Context.subSectionLabel(text: String) = TextView(this).apply {
        this.text = text.uppercase()
        textSize = 11f
        typeface = semiBold
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
            typeface = semiBold
            gravity = Gravity.CENTER
            minHeight = dp(36)
            setPadding(dp(14), dp(8), dp(14), dp(8))
            isClickable = true
            isFocusable = true
            val radius = dp(if (kind == ButtonKind.Primary) 6 else 5)
            val (content, textColor) = when (kind) {
                ButtonKind.Neutral -> stroked(0x14FFFFFF, 0x20FFFFFF, radius) to TEXT_PRIMARY
                ButtonKind.Success -> stroked(0x1F5BD98A, 0x335BD98A, radius) to SUCCESS
                ButtonKind.Danger -> stroked(0x1FFF6B6B, 0x33FF6B6B, radius) to DANGER
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
            window?.setBackgroundDrawable(rounded(SURFACE_ALT, context.dp(12)))
            getButton(AlertDialog.BUTTON_POSITIVE)?.setTextColor(ACCENT)
            getButton(AlertDialog.BUTTON_NEGATIVE)?.setTextColor(TEXT_SECONDARY)
        }
    }

    fun rounded(color: Int, radius: Int): Drawable = GradientDrawable().apply {
        setColor(color)
        cornerRadius = radius.toFloat()
    }

    private fun stroked(fill: Int, stroke: Int, radius: Int) = GradientDrawable().apply {
        setColor(fill)
        setStroke(2, stroke)
        cornerRadius = radius.toFloat()
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
