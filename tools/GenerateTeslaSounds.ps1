# GenerateTeslaSounds.ps1
# Generates 4 beefy, futuristic Tesla Rifle WAV sound effects using synthesis.
# Output: 16-bit mono PCM WAV at 44100 Hz

$sampleRate = 44100
$bitsPerSample = 16
$channels = 1
$outDir = Join-Path $PSScriptRoot "..\Content\Data\Sounds\Weapons"

function Write-WavFile {
    param(
        [string]$Path,
        [Int16[]]$Samples
    )
    $dataSize = $Samples.Length * 2  # 16-bit = 2 bytes per sample
    $fmtSize = 16
    $riffSize = 4 + (8 + $fmtSize) + (8 + $dataSize)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    # RIFF header
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("RIFF"))
    $bw.Write([int]$riffSize)
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("WAVE"))

    # fmt chunk
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("fmt "))
    $bw.Write([int]$fmtSize)
    $bw.Write([Int16]1)            # PCM format
    $bw.Write([Int16]$channels)
    $bw.Write([int]$sampleRate)
    $byteRate = $sampleRate * $channels * ($bitsPerSample / 8)
    $bw.Write([int]$byteRate)
    $blockAlign = $channels * ($bitsPerSample / 8)
    $bw.Write([Int16]$blockAlign)
    $bw.Write([Int16]$bitsPerSample)

    # data chunk
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("data"))
    $bw.Write([int]$dataSize)
    foreach ($s in $Samples) {
        $bw.Write($s)
    }

    $bw.Flush()
    [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
    $bw.Dispose()
    $ms.Dispose()
    Write-Host "Written: $Path ($($Samples.Length) samples, $([math]::Round($Samples.Length / $sampleRate, 2))s)"
}

function Clamp16 {
    param([double]$v)
    if ($v -gt 32767) { return [Int16]32767 }
    if ($v -lt -32768) { return [Int16](-32768) }
    return [Int16][math]::Round($v)
}

$pi2 = 2.0 * [math]::PI
$rng = New-Object System.Random(42)

# ============================================================
# 1. Tesla_wind_up.wav — Rising electric charge-up (~0.9s)
#    Beefy sub-bass sweep 50→400Hz + FM buzzing + noise crackle
# ============================================================
Write-Host "Generating Tesla_wind_up..."
$duration = 0.9
$numSamples = [int]($sampleRate * $duration)
$samples = New-Object Int16[] $numSamples
$phase1 = 0.0; $phase2 = 0.0; $phase3 = 0.0

for ($i = 0; $i -lt $numSamples; $i++) {
    $t = $i / $sampleRate
    $tNorm = $t / $duration  # 0→1

    # Envelope: slow fade-in, stays loud
    $env = [math]::Min(1.0, $tNorm * 2.5) * (0.6 + 0.4 * $tNorm)

    # Base frequency sweeps from 55 to 440 Hz (exponential)
    $baseFreq = 55.0 * [math]::Pow(8.0, $tNorm)

    # FM modulation — electric buzzing that intensifies
    $modDepth = 20.0 + 180.0 * $tNorm * $tNorm
    $modFreq = 60.0 + 80.0 * $tNorm
    $phase2 += $pi2 * $modFreq / $sampleRate
    $fm = $modDepth * [math]::Sin($phase2)

    # Main oscillator with FM
    $freq = $baseFreq + $fm
    $phase1 += $pi2 * $freq / $sampleRate

    # Layered harmonics for beef
    $sig = 0.45 * [math]::Sin($phase1)
    $sig += 0.25 * [math]::Sin($phase1 * 2.02)   # slightly detuned 2nd harmonic
    $sig += 0.15 * [math]::Sin($phase1 * 3.01)   # 3rd harmonic
    $sig += 0.10 * [math]::Sin($phase1 * 5.005)  # 5th harmonic for grit

    # Soft-clip distortion for heaviness
    $sig = [math]::Tanh($sig * (1.5 + 1.5 * $tNorm))

    # Rising crackle noise
    $noise = ($rng.NextDouble() * 2.0 - 1.0) * 0.12 * $tNorm * $tNorm
    $sig += $noise

    # Subtle pulsing tremolo
    $phase3 += $pi2 * (8.0 + 12.0 * $tNorm) / $sampleRate
    $tremolo = 1.0 - 0.15 * $tNorm * (0.5 + 0.5 * [math]::Sin($phase3))
    $sig *= $tremolo

    $samples[$i] = Clamp16 ($sig * $env * 28000)
}
Write-WavFile -Path (Join-Path $outDir "Tesla_wind_up.wav") -Samples $samples

# ============================================================
# 2. Tesla_spin.wav — Looping electric hum (~0.35s)
#    Pulsating, buzzy mid-frequency drone with harmonics
# ============================================================
Write-Host "Generating Tesla_spin..."
$duration = 0.35
$numSamples = [int]($sampleRate * $duration)
$samples = New-Object Int16[] $numSamples
$phase1 = 0.0; $phase2 = 0.0; $phase3 = 0.0

for ($i = 0; $i -lt $numSamples; $i++) {
    $t = $i / $sampleRate
    $tNorm = $t / $duration

    # Steady mid-range base with subtle drift
    $baseFreq = 220.0 + 30.0 * [math]::Sin($pi2 * 3.0 * $tNorm)

    # FM for electric texture
    $modFreq = 120.0 + 20.0 * [math]::Sin($pi2 * 5.0 * $tNorm)
    $phase2 += $pi2 * $modFreq / $sampleRate
    $fm = 80.0 * [math]::Sin($phase2)

    $freq = $baseFreq + $fm
    $phase1 += $pi2 * $freq / $sampleRate

    # Thick harmonic stack
    $sig = 0.40 * [math]::Sin($phase1)
    $sig += 0.25 * [math]::Sin($phase1 * 1.995)  # slightly detuned octave
    $sig += 0.15 * [math]::Sin($phase1 * 3.01)
    $sig += 0.10 * [math]::Sin($phase1 * 4.98)
    $sig += 0.08 * [math]::Sin($phase1 * 7.02)   # high harmonic for sizzle

    # Distortion
    $sig = [math]::Tanh($sig * 2.0)

    # Pulsating amplitude modulation for "spinning" feel
    $phase3 += $pi2 * 18.0 / $sampleRate  # 18 Hz pulse
    $pulse = 0.75 + 0.25 * [math]::Sin($phase3)
    $sig *= $pulse

    # Light noise
    $noise = ($rng.NextDouble() * 2.0 - 1.0) * 0.06
    $sig += $noise

    # Smooth envelope to avoid clicks
    $fadeLen = 0.01 * $sampleRate  # 10ms fade
    $envStart = if ($i -lt $fadeLen) { $i / $fadeLen } else { 1.0 }
    $samplesLeft = $numSamples - $i
    $envEnd = if ($samplesLeft -lt $fadeLen) { $samplesLeft / $fadeLen } else { 1.0 }
    $env = $envStart * $envEnd

    $samples[$i] = Clamp16 ($sig * $env * 26000)
}
Write-WavFile -Path (Join-Path $outDir "Tesla_spin.wav") -Samples $samples

# ============================================================
# 3. Tesla_wind_down.wav — Descending power-down (~0.7s)
#    Falling frequency + decaying amplitude + dying crackle
# ============================================================
Write-Host "Generating Tesla_wind_down..."
$duration = 0.7
$numSamples = [int]($sampleRate * $duration)
$samples = New-Object Int16[] $numSamples
$phase1 = 0.0; $phase2 = 0.0; $phase3 = 0.0

for ($i = 0; $i -lt $numSamples; $i++) {
    $t = $i / $sampleRate
    $tNorm = $t / $duration  # 0→1
    $tInv = 1.0 - $tNorm      # 1→0

    # Envelope: starts full, decays with slight exponential curve
    $env = [math]::Pow($tInv, 1.5)

    # Frequency sweeps down from ~400 to ~40 Hz
    $baseFreq = 40.0 * [math]::Pow(10.0, $tInv)

    # FM diminishes as power dies
    $modDepth = 120.0 * $tInv * $tInv
    $modFreq = 80.0 * $tInv + 20.0
    $phase2 += $pi2 * $modFreq / $sampleRate
    $fm = $modDepth * [math]::Sin($phase2)

    $freq = $baseFreq + $fm
    $phase1 += $pi2 * $freq / $sampleRate

    # Harmonics thin out as it winds down
    $sig = 0.45 * [math]::Sin($phase1)
    $sig += 0.25 * $tInv * [math]::Sin($phase1 * 2.02)
    $sig += 0.15 * $tInv * $tInv * [math]::Sin($phase1 * 3.01)
    $sig += 0.08 * $tInv * $tInv * [math]::Sin($phase1 * 5.005)

    # Distortion decreases
    $sig = [math]::Tanh($sig * (1.0 + 1.5 * $tInv))

    # Dying crackle
    $noise = ($rng.NextDouble() * 2.0 - 1.0) * 0.15 * $tInv
    # Intermittent pops: random chance decreasing over time
    if ($rng.NextDouble() -lt 0.02 * $tInv) {
        $noise += ($rng.NextDouble() - 0.5) * 0.4 * $tInv
    }
    $sig += $noise

    # Tremolo slows down as it dies
    $phase3 += $pi2 * (15.0 * $tInv + 2.0) / $sampleRate
    $tremolo = 1.0 - 0.2 * $tInv * (0.5 + 0.5 * [math]::Sin($phase3))
    $sig *= $tremolo

    $samples[$i] = Clamp16 ($sig * $env * 28000)
}
Write-WavFile -Path (Join-Path $outDir "Tesla_wind_down.wav") -Samples $samples

# ============================================================
# 4. TeslaBeamFire.wav — Heavy electric zap blast (~0.25s)
#    Sharp attack, crunchy distortion, multiple layered freqs,
#    short bright tail. THIS is the main "firing" sound.
# ============================================================
Write-Host "Generating TeslaBeamFire..."
$duration = 0.25
$numSamples = [int]($sampleRate * $duration)
$samples = New-Object Int16[] $numSamples
$phase1 = 0.0; $phase2 = 0.0; $phase3 = 0.0; $phase4 = 0.0

for ($i = 0; $i -lt $numSamples; $i++) {
    $t = $i / $sampleRate
    $tNorm = $t / $duration
    $tInv = 1.0 - $tNorm

    # Sharp attack envelope: instant attack, exponential decay
    $env = [math]::Pow($tInv, 0.8) * [math]::Min(1.0, $i / (0.003 * $sampleRate))

    # Two detuned "zap" oscillators — one drops fast, one stays high
    $freqA = 800.0 * [math]::Pow(0.15, $tNorm)  # drops from 800→~170
    $freqB = 1600.0 + 400.0 * [math]::Sin($pi2 * 35.0 * $t)  # high unstable tone

    # FM for electric crackle
    $phase3 += $pi2 * (200.0 + 100.0 * $tInv) / $sampleRate
    $fm = (60.0 + 150.0 * $tInv) * [math]::Sin($phase3)

    $phase1 += $pi2 * ($freqA + $fm) / $sampleRate
    $phase2 += $pi2 * $freqB / $sampleRate

    # Sub-bass thump for weight
    $phase4 += $pi2 * (60.0 + 20.0 * $tInv) / $sampleRate
    $sub = 0.35 * [math]::Sin($phase4) * [math]::Pow($tInv, 2.0)

    # Layer the oscillators
    $sig = 0.35 * [math]::Sin($phase1)
    $sig += 0.20 * [math]::Sin($phase1 * 2.01)
    $sig += 0.15 * [math]::Sin($phase1 * 3.03)
    $sig += 0.12 * $tInv * [math]::Sin($phase2)       # high sizzle
    $sig += 0.08 * $tInv * [math]::Sin($phase2 * 0.5)  # sub-harmonic of sizzle
    $sig += $sub

    # Heavy distortion — double-staged for real crunch
    $sig = [math]::Tanh($sig * 3.0)
    $sig = [math]::Tanh($sig * 1.8)

    # Transient noise burst at the start for impact
    $noiseBurst = 0.0
    if ($tNorm -lt 0.15) {
        $noiseBurst = ($rng.NextDouble() * 2.0 - 1.0) * 0.5 * (1.0 - $tNorm / 0.15)
    }
    $sig += $noiseBurst

    # Crackle throughout
    $noise = ($rng.NextDouble() * 2.0 - 1.0) * 0.08 * $tInv
    $sig += $noise

    $samples[$i] = Clamp16 ($sig * $env * 30000)
}
Write-WavFile -Path (Join-Path $outDir "TeslaBeamFire.wav") -Samples $samples

Write-Host "`nAll Tesla Rifle sounds generated successfully!"
