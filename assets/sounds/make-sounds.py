"""The four Wormhole Send sounds, synthesized from sine partials (no samples),
so both apps can ship the same files:

    sent.wav          files delivered: two rising glassy notes (G6, C7)
    received.wav      files received: a warm rising arpeggio (C6 E6 G6 C7)
    failed.wav        a send failed or a receive broke off: two falling
                      glassy notes (E5, B4)
    pair-request.wav  another device asks to pair: A6 twice

44.1 kHz, 16-bit mono, peaks at -1 dBFS; the apps play them at 60 % volume.
After changing this file, run it again (python make-sounds.py) and commit the
.wav files with it.
"""
import math
import os
import struct
import wave

RATE = 44100


def note(freq, partials, length, attack=0.003, drop=0.0, drop_tau=0.05):
    """One struck note. `partials`: (frequency ratio, amplitude, decay in
    seconds). `drop`: the pitch falls by this fraction at the start."""
    out = []
    phases = [0.0] * len(partials)
    for i in range(int(length * RATE)):
        t = i / RATE
        f = freq * (1 - drop * (1 - math.exp(-t / drop_tau)))
        envelope = min(1.0, t / attack) if attack > 0 else 1.0
        value = 0.0
        for k, (ratio, amplitude, tau) in enumerate(partials):
            phases[k] += 2 * math.pi * f * ratio / RATE
            value += amplitude * math.exp(-t / tau) * math.sin(phases[k])
        out.append(value * envelope)
    return out


def mix(parts, length):
    """`parts`: (start in seconds, gain, samples)."""
    out = [0.0] * int(length * RATE)
    for start, gain, samples in parts:
        offset = int(start * RATE)
        for i, value in enumerate(samples):
            if offset + i < len(out):
                out[offset + i] += gain * value
    return out


def save(path, samples, peak=0.89):
    """Normalized to `peak`, with a 30 ms fade at the end."""
    top = max(abs(v) for v in samples) or 1
    fade = int(0.03 * RATE)
    n = len(samples)
    frames = bytearray()
    for i, value in enumerate(samples):
        gain = min(1.0, (n - i) / fade)
        frames += struct.pack('<h', int(max(-1, min(1, value / top * peak * gain)) * 32767))
    with wave.open(path, 'wb') as file:
        file.setnchannels(1)
        file.setsampwidth(2)
        file.setframerate(RATE)
        file.writeframes(bytes(frames))


# A struck glass: inharmonic partials that fade fast.
GLASS = [(1, 1.0, 0.45), (2.32, 0.30, 0.16), (4.25, 0.14, 0.07), (6.63, 0.06, 0.035)]
GLASS_LOW = [(1, 1.0, 0.3), (2.32, 0.32, 0.13), (4.25, 0.14, 0.06), (6.63, 0.05, 0.03)]
GLASS_LOW_LONG = [(1, 1.0, 0.5), (2.32, 0.32, 0.18), (4.25, 0.14, 0.08), (6.63, 0.05, 0.04)]
# Warm: harmonic and soft.
WARM = [(1, 1.0, 0.5), (2, 0.22, 0.22), (3, 0.07, 0.1)]
WARM_LONG = [(1, 1.0, 0.75), (2, 0.22, 0.3), (3, 0.07, 0.12)]
# Clear: nearly a sine.
CLEAR = [(1, 1.0, 0.55), (2, 0.1, 0.18), (3, 0.04, 0.09)]

SOUNDS = {
    'sent': lambda: mix([
        (0, 0.8, note(1568.0, GLASS, 1.2)),
        (0.085, 1.0, note(2093.0, GLASS, 1.1)),
    ], 1.25),
    'received': lambda: mix([
        (0, 0.55, note(1046.5, WARM, 0.6)),
        (0.065, 0.65, note(1318.5, WARM, 0.6)),
        (0.13, 0.75, note(1568.0, WARM, 0.7)),
        (0.195, 1.0, note(2093.0, WARM_LONG, 1.2)),
    ], 1.45),
    'failed': lambda: mix([
        (0, 0.85, note(659.3, GLASS_LOW, 0.45, attack=0.002)),
        (0.14, 1.0, note(493.9, GLASS_LOW_LONG, 0.8, attack=0.002, drop=0.02)),
    ], 0.95),
    'pair-request': lambda: mix([
        (0, 1.0, note(1760.0, CLEAR, 0.6)),
        (0.15, 0.8, note(1760.0, CLEAR, 1.0)),
    ], 1.15),
}

if __name__ == '__main__':
    here = os.path.dirname(os.path.abspath(__file__))
    for name, make in SOUNDS.items():
        samples = make()
        save(os.path.join(here, f'{name}.wav'), samples)
        print(f'{name}.wav  {len(samples) / RATE:.2f} s')
