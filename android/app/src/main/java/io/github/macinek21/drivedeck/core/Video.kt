package io.github.macinek21.drivedeck.core

import kotlin.math.abs
import kotlin.math.roundToInt

/**
 * One recording mode. [minFps] < [fps]: the camera may slow down in the dark. [highSpeed]: official high-speed session.
 * [legacy]: reached through the manufacturer's older camera interface, which does not promise it to other apps.
 */
data class VideoMode(val res: String, val width: Int, val height: Int, val fps: Int, val minFps: Int = fps, val highSpeed: Boolean = false, val legacy: Boolean = false) {
    /** Recorded by the app's own engine, without the burned-in stamp. */
    val special get() = highSpeed || legacy
    val id get() = "$res@$fps"
}

object Video {
    val RES = listOf("480p", "720p", "1080p", "1440p", "2160p")
    val FPS = listOf(15, 20, 24, 25, 30, 48, 50, 60, 120, 240)
    const val MAX_MBIT = 100

    private val SIZES = mapOf("720p" to (1280 to 720), "1080p" to (1920 to 1080), "1440p" to (2560 to 1440), "2160p" to (3840 to 2160))
    private val FEATURE = Regex("""(\d{4})[xX](\d{3,4})(?:[_@ ]?(\d{2,3})[fF][pP][sS])?""")

    /**
     * Modes of a Samsung phone that are only reachable through its older camera interface, from what the phone says about itself:
     * the interface's parameters (the fast mode switch must exist), the camera feature file if it is readable, and what the
     * video chip can encode. Nothing is tried out here.
     */
    fun legacyModes(params: String?, features: String?, official: List<VideoMode>, canEncode: (Int, Int, Int) -> Boolean): List<VideoMode> {
        if (params == null || !Regex("(^|;)fast-fps-mode=").containsMatchIn(params)) return emptyList()
        val found = LinkedHashSet<Pair<String, Int>>(listOf("1080p" to 60, "1440p" to 60, "2160p" to 30))
        features?.lineSequence()?.filter { it.contains("CAMCORDER_RESOLUTION", true) }?.forEach { l ->
            for (m in FEATURE.findAll(l)) {
                val res = SIZES.entries.firstOrNull { it.value == m.groupValues[1].toInt() to m.groupValues[2].toInt() }?.key ?: continue
                found += res to (m.groupValues[3].toIntOrNull() ?: 30)
            }
        }
        return found.filter { (res, fps) -> fps in FPS && fps <= 60 && official.none { it.res == res && it.fps == fps } }.mapNotNull { (res, fps) ->
            val (w, h) = SIZES.getValue(res)
            if (canEncode(w, h, fps)) VideoMode(res, w, h, fps, legacy = true) else null
        }
    }

    /** First guess how much to ask of an encoder that assumes 30 fps, so that the file gets the bitrate the user set. */
    fun startScale(m: VideoMode) = if (m.fps <= 30) 1.0 else if (m.legacy) 30.0 / m.fps else 0.4

    /** Corrects the guess from a recorded clip. Only downwards: a quiet scene may well need less than the limit. */
    fun correctScale(scale: Double, wantedBits: Int, actualBits: Double) =
        if (actualBits > wantedBits * 1.15) (scale * wantedBits / actualBits).coerceIn(0.08, 1.0) else scale

    fun label(m: VideoMode) = when (m.res) { "2160p" -> "4K"; "1440p" -> "1440p (2K)"; else -> m.res } + "  ${m.width}×${m.height}"

    /** The wanted mode, or the closest one the camera has: same resolution first, then the nearest smaller one. */
    fun pick(modes: List<VideoMode>, res: String, fps: Int): VideoMode? {
        if (modes.isEmpty()) return null
        val want = RES.indexOf(res)
        val best = modes.map { RES.indexOf(it.res) }.distinct().minByOrNull { if (it <= want) want - it else 100 + it }!!
        val same = modes.filter { RES.indexOf(it.res) == best }
        return same.firstOrNull { it.fps == fps } ?: same.filter { it.fps <= fps && !it.special }.maxByOrNull { it.fps } ?: same.minByOrNull { abs(it.fps - fps) }
    }

    /** Sensible bitrate in Mbit/s; frames above 30 per second need less each, H.265 about 30 % less overall. */
    fun recommended(width: Int, height: Int, fps: Int, hevc: Boolean): Int {
        val mbit = width.toDouble() * height * (minOf(fps, 30) * 0.16 + maxOf(0, fps - 30) * 0.08) / 1e6
        return (mbit * if (hevc) 0.7 else 1.0).roundToInt().coerceIn(2, MAX_MBIT)
    }
}
