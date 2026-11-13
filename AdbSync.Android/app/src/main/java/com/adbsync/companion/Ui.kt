package com.adbsync.companion

import android.app.AlertDialog
import android.content.Context
import android.content.res.ColorStateList
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Outline
import android.graphics.drawable.LayerDrawable
import android.view.ViewOutlineProvider
import android.widget.FrameLayout
import android.widget.HorizontalScrollView
import android.widget.ImageView
import android.graphics.ColorFilter
import android.graphics.Paint
import android.graphics.PixelFormat
import android.graphics.RadialGradient
import android.graphics.Shader
import android.graphics.Typeface
import android.graphics.drawable.ColorDrawable
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

    /** The web UI's body.glass backdrop: the same arcs image at 55% over #09061a, cropped to fill. */
    fun windowBackground(context: Context): Drawable = object : Drawable() {
        private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply { alpha = 140 }
        private val image = android.graphics.BitmapFactory.decodeResource(context.resources, R.drawable.bg_arcs)

        override fun draw(canvas: Canvas) {
            canvas.drawColor(0xFF09061A.toInt())
            val w = bounds.width().toFloat()
            val h = bounds.height().toFloat()
            val scale = maxOf(w / image.width, h / image.height) * 1.08f
            canvas.save()
            canvas.translate((w - image.width * scale) / 2, (h - image.height * scale) / 2)
            canvas.scale(scale, scale)
            canvas.drawBitmap(image, 0f, 0f, paint)
            canvas.restore()
        }

        override fun setAlpha(alpha: Int) {}
        override fun setColorFilter(colorFilter: ColorFilter?) {}
        @Suppress("OVERRIDE_DEPRECATION")
        override fun getOpacity() = PixelFormat.OPAQUE
    }

    /** Frosted glass bar fill: a faint white gradient with a hairline on the edge that faces the content. */
    fun frostedBar(context: Context, edgeOnTop: Boolean, edgeOnEnd: Boolean = false): Drawable {
        val line = maxOf(1, context.resources.displayMetrics.density.toInt())
        val frost = GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM, intArrayOf(0x24FFFFFF, 0x12FFFFFF))
        val edge = GradientDrawable().apply { setColor(0x33FFFFFF) }
        return LayerDrawable(arrayOf(frost, edge)).apply {
            if (edgeOnEnd) {
                setLayerGravity(1, Gravity.END or Gravity.FILL_VERTICAL)
                setLayerWidth(1, line)
            } else {
                setLayerGravity(1, (if (edgeOnTop) Gravity.TOP else Gravity.BOTTOM) or Gravity.FILL_HORIZONTAL)
                setLayerHeight(1, line)
            }
        }
    }

    /** A glass card. With [accent] it gets the web job card's colored left edge; [glow] adds a tinted border (running jobs). */
    fun Context.card(accent: Int? = null, glow: Boolean = false): LinearLayout = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        val border = if (glow && accent != null) (accent and 0x00FFFFFF) or (0x99 shl 24) else SURFACE_BORDER
        val base = glass(SURFACE, border, dp(10), dp(1))
        val edge = dp(10).toFloat()
        background = if (accent == null) base else LayerDrawable(arrayOf(base, GradientDrawable().apply {
            setColor(accent)
            cornerRadii = floatArrayOf(edge, edge, 0f, 0f, 0f, 0f, edge, edge)
        })).apply {
            setLayerGravity(1, Gravity.START or Gravity.FILL_VERTICAL)
            setLayerWidth(1, dp(3))
        }
        setPadding(dp(if (accent == null) 16 else 18), dp(14), dp(16), dp(14))
        layoutParams = LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply { bottomMargin = dp(12) }
    }

    fun Context.title(text: String) = TextView(this).apply {
        this.text = text
        textSize = 26f
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
        typeface = plex(400)
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

    /** The app bar: translucent black, brand mark and name on the left, [action] (e.g. the web UI link) on the right. */
    fun Context.header(action: View?): LinearLayout = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
        background = frostedBar(context, edgeOnTop = false)
        setPadding(dp(16), dp(10), dp(16), dp(10))
        addView(View(context).apply {
            background = GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(ACCENT_FROM, ACCENT_TO))
                .apply { cornerRadius = dp(5).toFloat() }
            layoutParams = LinearLayout.LayoutParams(dp(14), dp(14)).apply { marginEnd = dp(10) }
        })
        addView(TextView(context).apply {
            text = "AdbSync"
            textSize = 18f
            typeface = plex(600)
            setTextColor(TEXT_PRIMARY)
        }, LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f))
        if (action != null) addView(action)
    }

    /** A pill like the web's MudChip: tinted fill and border. */
    fun Context.chip(text: String, color: Int, onClick: (() -> Unit)? = null) = TextView(this).apply {
        this.text = text
        textSize = 12f
        typeface = plex(500)
        setTextColor(color)
        setPadding(dp(10), dp(4), dp(10), dp(4))
        background = glass((color and 0x00FFFFFF) or (0x1F shl 24), (color and 0x00FFFFFF) or (0x59 shl 24), dp(14), dp(1))
        layoutParams = LinearLayout.LayoutParams(WRAP_CONTENT, WRAP_CONTENT).apply { marginEnd = dp(8) }
        if (onClick != null) setOnClickListener { onClick() }
    }

    fun Context.syncChip(text: String, color: Int) = chip(text, color).apply {
        setPadding(dp(8), dp(4), dp(10), dp(4))
        compoundDrawablePadding = dp(6)
        val mark = SyncMarkDrawable().apply { setBounds(0, 0, dp(18), dp(18)) }
        setCompoundDrawablesRelative(mark, null, null, null)
        mark.start()
    }

    /** One tile of the stat strip: big value over an uppercase label with a dot in the tile's color. */
    fun Context.statTile(value: String, label: String, color: Int, onClick: (() -> Unit)? = null) = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        background = glass(0x0FFFFFFF, 0x1AFFFFFF, dp(10), dp(1))
        setPadding(dp(14), dp(10), dp(14), dp(10))
        minimumWidth = dp(112)
        addView(TextView(context).apply {
            text = value
            textSize = 26f
            typeface = plex(600)
            letterSpacing = -0.03f
            setTextColor(blend(color, 0xFFFFFFFF.toInt(), 0.45f))
        })
        addView(LinearLayout(context).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            addView(dot(color))
            addView(TextView(context).apply {
                text = label.uppercase()
                textSize = 10f
                typeface = plex(600)
                letterSpacing = 0.1f
                setTextColor(TEXT_SECONDARY)
            })
        })
        layoutParams = LinearLayout.LayoutParams(WRAP_CONTENT, WRAP_CONTENT).apply { marginEnd = dp(10) }
        if (onClick != null) setOnClickListener { onClick() }
    }

    fun Context.statStrip(tiles: List<View>) = HorizontalScrollView(this).apply {
        isHorizontalScrollBarEnabled = false
        overScrollMode = View.OVER_SCROLL_NEVER
        addView(LinearLayout(context).apply {
            orientation = LinearLayout.HORIZONTAL
            tiles.forEach { addView(it) }
        })
        layoutParams = LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT).apply { bottomMargin = dp(14) }
    }

    enum class StepState { Pending, Active, Done }

    /** The web's phase graph: bordered circles joined by lines, green when done, violet when active. */
    fun Context.phaseGraph(steps: List<Triple<Int, String, StepState>>) = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_HORIZONTAL
        setPadding(0, dp(14), 0, dp(4))
        steps.forEachIndexed { i, (icon, label, state) ->
            val (stroke, fill, tint) = when (state) {
                StepState.Done -> Triple(SUCCESS, 0x1F5BD98A, SUCCESS)
                StepState.Active -> Triple(ACCENT_FROM, 0x477C5CFF, 0xFFFFFFFF.toInt())
                StepState.Pending -> Triple(0x29FFFFFF, 0x08FFFFFF, 0xFF6F6A86.toInt())
            }
            addView(LinearLayout(context).apply {
                orientation = LinearLayout.VERTICAL
                gravity = Gravity.CENTER_HORIZONTAL
                addView(FrameLayout(context).apply {
                    background = GradientDrawable().apply {
                        shape = GradientDrawable.OVAL
                        setColor(fill)
                        setStroke(dp(2), stroke)
                    }
                    addView(ImageView(context).apply {
                        setImageResource(if (state == StepState.Done) R.drawable.ic_check else icon)
                        imageTintList = ColorStateList.valueOf(tint)
                    }, FrameLayout.LayoutParams(dp(17), dp(17), Gravity.CENTER))
                }, LinearLayout.LayoutParams(dp(34), dp(34)))
                addView(TextView(context).apply {
                    text = label
                    textSize = 10f
                    typeface = plex(500)
                    setTextColor(if (state == StepState.Pending) TEXT_SECONDARY else TEXT_PRIMARY)
                    setPadding(0, dp(3), 0, 0)
                })
            })
            if (i < steps.size - 1) {
                addView(View(context).apply {
                    background = when (state) {
                        StepState.Done -> rounded(SUCCESS, dp(2))
                        StepState.Active -> GradientDrawable(GradientDrawable.Orientation.LEFT_RIGHT, intArrayOf(ACCENT_FROM, 0x1FFFFFFF))
                        StepState.Pending -> rounded(0x1FFFFFFF, dp(2))
                    }
                }, LinearLayout.LayoutParams(dp(40), dp(2)).apply { topMargin = dp(16) })
            }
        }
    }

    /** A label/value row like the web job card's info table. */
    fun Context.infoRow(label: String, value: View) = LinearLayout(this).apply {
        orientation = LinearLayout.HORIZONTAL
        setPadding(0, dp(3), 0, dp(3))
        addView(caption(label), LinearLayout.LayoutParams(dp(78), WRAP_CONTENT))
        addView(value, LinearLayout.LayoutParams(0, WRAP_CONTENT, 1f))
    }

    /** The job's app icon (rounded), or the web's generated initial tile while it's missing. */
    fun Context.appIcon(pkg: String?, bitmap: Bitmap?, sizeDp: Int = 42): View {
        val size = dp(sizeDp)
        val radius = size * 0.24f
        val params = LinearLayout.LayoutParams(size, size).apply { marginEnd = dp(12) }
        if (bitmap != null) return ImageView(this).apply {
            setImageBitmap(bitmap)
            scaleType = ImageView.ScaleType.CENTER_CROP
            clipToOutline = true
            outlineProvider = object : ViewOutlineProvider() {
                override fun getOutline(view: View, outline: Outline) = outline.setRoundRect(0, 0, view.width, view.height, radius)
            }
            layoutParams = params
        }
        return FrameLayout(this).apply {
            layoutParams = params
            if (pkg.isNullOrEmpty()) {
                background = glass(0x14FFFFFF, 0x26FFFFFF, radius.toInt(), 1)
                addView(ImageView(context).apply {
                    setImageResource(R.drawable.ic_sync)
                    imageTintList = ColorStateList.valueOf(TEXT_SECONDARY)
                }, FrameLayout.LayoutParams(dp(22), dp(22), Gravity.CENTER))
            } else {
                var hash = 17
                for (c in pkg) hash = hash * 31 + c.code
                val hue = Math.abs(hash) % 360
                background = GradientDrawable(
                    GradientDrawable.Orientation.TL_BR,
                    intArrayOf(hsl(hue.toFloat(), 0.68f, 0.56f), hsl(((hue + 45) % 360).toFloat(), 0.70f, 0.38f)),
                ).apply { cornerRadius = radius }
                val name = pkg.split('.').filter { it.isNotEmpty() }.let { parts ->
                    parts.lastOrNull { it.lowercase() !in GENERIC_SEGMENTS } ?: parts.lastOrNull() ?: "?"
                }
                addView(TextView(context).apply {
                    text = name.take(1).uppercase()
                    textSize = sizeDp * 0.42f
                    typeface = plex(600)
                    setTextColor(0xFFFFFFFF.toInt())
                    gravity = Gravity.CENTER
                }, FrameLayout.LayoutParams(MATCH_PARENT, MATCH_PARENT))
            }
        }
    }

    private val GENERIC_SEGMENTS = setOf("app", "android", "mobile", "free", "pro", "lite", "client")

    private fun blend(from: Int, to: Int, amount: Float): Int {
        fun mix(shift: Int) = (((from shr shift) and 0xFF) * (1 - amount) + ((to shr shift) and 0xFF) * amount).toInt()
        return (0xFF shl 24) or (mix(16) shl 16) or (mix(8) shl 8) or mix(0)
    }

    private fun hsl(h: Float, s: Float, l: Float): Int {
        val c = (1 - Math.abs(2 * l - 1)) * s
        val x = c * (1 - Math.abs((h / 60f) % 2 - 1))
        val m = l - c / 2
        val (r, g, b) = when {
            h < 60 -> Triple(c, x, 0f)
            h < 120 -> Triple(x, c, 0f)
            h < 180 -> Triple(0f, c, x)
            h < 240 -> Triple(0f, x, c)
            h < 300 -> Triple(x, 0f, c)
            else -> Triple(c, 0f, x)
        }
        return (0xFF shl 24) or (((r + m) * 255).toInt() shl 16) or (((g + m) * 255).toInt() shl 8) or ((b + m) * 255).toInt()
    }

    /** Bottom navigation: icon-only tabs on a frosted glass bar, accent-colored active tab, optional alert dot. */
    class TabBar(
        context: Context,
        tabs: List<Pair<Int, String>>,
        private val rail: Boolean = false,
        private val onSelect: (Int) -> Unit,
    ) : LinearLayout(context) {
        private val icons = ArrayList<ImageView>()
        private val badges = ArrayList<View>()

        init {
            orientation = if (rail) VERTICAL else HORIZONTAL
            if (rail) gravity = Gravity.CENTER_VERTICAL
            val d = resources.displayMetrics.density
            background = frostedBar(context, edgeOnTop = true, edgeOnEnd = rail)
            tabs.forEachIndexed { i, (icon, label) ->
                val tab = FrameLayout(context).apply {
                    setPadding(0, (14 * d).toInt(), 0, (14 * d).toInt())
                    isClickable = true
                    contentDescription = label
                    background = RippleDrawable(ColorStateList.valueOf(0x22FFFFFF), null, ColorDrawable(0xFFFFFFFF.toInt()))
                    setOnClickListener { select(i); onSelect(i) }
                }
                val image = ImageView(context).apply { setImageResource(icon) }
                val badge = View(context).apply {
                    background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(WARNING) }
                    visibility = GONE
                }
                tab.addView(FrameLayout(context).apply {
                    addView(image, FrameLayout.LayoutParams((26 * d).toInt(), (26 * d).toInt(), Gravity.CENTER))
                    addView(badge, FrameLayout.LayoutParams((9 * d).toInt(), (9 * d).toInt(), Gravity.TOP or Gravity.END))
                }, FrameLayout.LayoutParams((42 * d).toInt(), (26 * d).toInt(), Gravity.CENTER))
                addView(tab, if (rail) LayoutParams(MATCH_PARENT, WRAP_CONTENT) else LayoutParams(0, WRAP_CONTENT, 1f))
                icons += image; badges += badge
            }
            select(0)
        }

        fun select(index: Int) {
            icons.indices.forEach { i ->
                icons[i].imageTintList = ColorStateList.valueOf(if (i == index) ACCENT_FROM else TEXT_SECONDARY)
            }
        }

        fun setBadge(index: Int, visible: Boolean) {
            badges[index].visibility = if (visible) VISIBLE else GONE
        }
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
