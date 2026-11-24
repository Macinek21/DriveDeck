package io.github.macinek21.drivedeck.svc

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothSocket
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.SystemClock
import androidx.core.content.ContextCompat
import io.github.macinek21.drivedeck.BuildConfig
import io.github.macinek21.drivedeck.core.ElmParser
import io.github.macinek21.drivedeck.core.ElmSimulator
import io.github.macinek21.drivedeck.core.ObdException
import io.github.macinek21.drivedeck.core.ObdTransport
import java.io.IOException
import java.util.UUID
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

/** Classic Bluetooth (SPP) link to an ELM327 that the user paired in Android's own Bluetooth settings. */
class BluetoothTransport private constructor(private val socket: BluetoothSocket) : ObdTransport {
    private val rx = LinkedBlockingQueue<Int>(ElmParser.MAX_CHARS)
    @Volatile private var closed = false
    private val reader = Thread({
        val buf = ByteArray(512)
        try {
            while (!closed) {
                val n = socket.inputStream.read(buf)
                if (n < 0) break
                for (i in 0 until n) rx.offer(buf[i].toInt() and 0xFF) // a flooding adapter only fills the bounded queue
            }
        } catch (_: IOException) {}
        closed = true
    }, "elm-rx").also { it.isDaemon = true; it.start() }

    override fun send(command: String, timeoutMs: Long): String {
        require(command.all { it in ' '..'~' }) { "Invalid command" }
        rx.clear()
        socket.outputStream.write((command + "\r").toByteArray(Charsets.US_ASCII))
        val sb = StringBuilder()
        val end = SystemClock.elapsedRealtime() + timeoutMs
        while (sb.length < ElmParser.MAX_CHARS) {
            val left = end - SystemClock.elapsedRealtime()
            if (left <= 0) throw IOException("Adapter stopped answering")
            val b = rx.poll(minOf(left, 200), TimeUnit.MILLISECONDS)
            if (b == null) { if (closed) throw IOException("Bluetooth connection lost"); continue }
            if (b == '>'.code) return sb.toString()
            if (b != 0) sb.append(if (b < 0x80) b.toChar() else '?')
        }
        throw IOException("Adapter sent too much data")
    }

    override fun close() {
        closed = true
        runCatching { socket.close() }
        reader.interrupt()
    }

    companion object {
        private val SPP: UUID = UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")
        const val SIMULATED = "00:00:00:00:00:00"

        fun permitted(c: Context) = Build.VERSION.SDK_INT < 31 ||
            ContextCompat.checkSelfPermission(c, Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED

        private fun adapter(c: Context) = c.getSystemService(BluetoothManager::class.java)?.adapter

        /** Paired devices as (address, name); debug builds also offer a simulated car. */
        @SuppressLint("MissingPermission")
        fun paired(c: Context): List<Pair<String, String>> {
            val real = if (!permitted(c)) emptyList() else try {
                adapter(c)?.bondedDevices.orEmpty().map { it.address to (it.name ?: it.address) }.sortedBy { it.second }
            } catch (_: SecurityException) { emptyList() }
            return if (BuildConfig.DEBUG) real + (SIMULATED to "Simulated car (debug)") else real
        }

        @SuppressLint("MissingPermission")
        fun connect(c: Context, address: String): ObdTransport {
            if (BuildConfig.DEBUG && address == SIMULATED) return ElmSimulator(moving = true)
            if (!permitted(c)) throw ObdException("Bluetooth permission is missing")
            val a = adapter(c) ?: throw ObdException("This phone has no Bluetooth")
            if (!a.isEnabled) throw ObdException("Bluetooth is turned off")
            val dev = a.getRemoteDevice(address)
            var last: IOException? = null
            // Some clones only accept the second, insecure variant.
            for (secure in listOf(true, false)) {
                val s = if (secure) dev.createRfcommSocketToServiceRecord(SPP) else dev.createInsecureRfcommSocketToServiceRecord(SPP)
                try {
                    s.connect()
                    return BluetoothTransport(s)
                } catch (e: IOException) {
                    last = e
                    runCatching { s.close() }
                }
            }
            AppLog.i("Bluetooth connect failed: ${last?.message}")
            throw ObdException("Adapter not reachable. Is it plugged in and the ignition on?")
        }
    }
}
