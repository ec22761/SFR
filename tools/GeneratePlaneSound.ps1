# GeneratePlaneSound.ps1
# Generates a propeller plane flyby sound for the Air Strike weapon.
# Output: 16-bit mono PCM WAV at 44100 Hz, ~1.8s, with doppler-style pitch sweep.

$sampleRate = 44100
$bitsPerSample = 16
$channels = 1
$outDir = Join-Path $PSScriptRoot "..\Content\Data\Sounds\Weapons"
$null = New-Item -ItemType Directory -Force -Path $outDir

function Write-WavFile {
    param([string]$Path, [Int16[]]$Samples)
    $dataSize = $Samples.Length * 2
    $fmtSize = 16
    $riffSize = 4 + (8 + $fmtSize) + (8 + $dataSize)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("RIFF"))
    $bw.Write([int]$riffSize)
    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("WAVE"))

    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("fmt "))
    $bw.Write([int]$fmtSize)
    $bw.Write([Int16]1)
    $bw.Write([Int16]$channels)
    $bw.Write([int]$sampleRate)
    $byteRate = $sampleRate * $channels * ($bitsPerSample / 8)
    $bw.Write([int]$byteRate)
    $blockAlign = $channels * ($bitsPerSample / 8)
    $bw.Write([Int16]$blockAlign)
    $bw.Write([Int16]$bitsPerSample)

    $bw.Write([System.Text.Encoding]::ASCII.GetBytes("data"))
    $bw.Write([int]$dataSize)
    foreach ($s in $Samples) { $bw.Write($s) }

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
$rng = New-Object System.Random(7)

# ============================================================
# Plane_flyby.wav — propeller-driven plane passing overhead
# Engine drone + prop AM modulation + slight doppler pitch sweep
# ============================================================
Write-Host "Generating Plane_flyby..."
$duration = 1.8
$numSamples = [int]($sampleRate * $duration)
$samples = New-Object Int16[] $numSamples

# Phase accumulators (so frequency modulation stays smooth)
$pEngine = 0.0
$pProp1  = 0.0
$pProp2  = 0.0
$pWhine  = 0.0
$pSub    = 0.0

# Smoothed wind/noise low-pass state
$noiseLP = 0.0

for ($i = 0; $i -lt $numSamples; $i++) {
    $t = $i / $sampleRate
    $tNorm = $t / $duration  # 0..1

    # Doppler-style pitch curve: starts low, peaks at ~0.5, drops below baseline as it passes.
    # f = 1 + 0.18 * cos((tNorm - 0.5) * pi)  -> bell that goes 1.0 -> 1.18 -> 1.0
    # Then bias slightly so receding pitch dips below approach pitch.
    $bell  = [math]::Cos(($tNorm - 0.5) * [math]::PI)              # -0..1..-0
    $tilt  = -0.10 * $tNorm                                         # gradual fall
    $pitch = 1.0 + 0.16 * $bell + $tilt                             # ~0.90..1.16..0.90

    # Volume envelope: fade in, swell at center, fade out
    $fadeIn  = [math]::Min(1.0, $tNorm / 0.18)
    $fadeOut = [math]::Min(1.0, (1.0 - $tNorm) / 0.30)
    $swell   = 0.55 + 0.45 * [math]::Max(0.0, $bell)
    $env     = $fadeIn * $fadeOut * $swell

    # ---- Components ----
    # Sub thump (~55 Hz) for body
    $fSub = 55.0 * $pitch
    $pSub += $pi2 * $fSub / $sampleRate
    $sub = [math]::Sin($pSub) * 0.35

    # Engine drone — sawtooth-ish via summed sines @ ~110 Hz
    $fEng = 110.0 * $pitch
    $pEngine += $pi2 * $fEng / $sampleRate
    $eng = ([math]::Sin($pEngine) + 0.55 * [math]::Sin(2 * $pEngine) + 0.32 * [math]::Sin(3 * $pEngine) + 0.18 * [math]::Sin(4 * $pEngine)) * 0.30

    # Propeller buzz — two close detuned tones @ ~205 / 213 Hz, AM-modulated by blade-pass (~30 Hz)
    $fProp1 = 205.0 * $pitch
    $fProp2 = 213.0 * $pitch
    $pProp1 += $pi2 * $fProp1 / $sampleRate
    $pProp2 += $pi2 * $fProp2 / $sampleRate
    $bladeAm = 0.6 + 0.4 * [math]::Sin($pi2 * 30.0 * $pitch * $t)
    $prop = ([math]::Sin($pProp1) + [math]::Sin($pProp2)) * 0.22 * $bladeAm

    # High whine — thin tone @ ~720 Hz, quieter
    $fWhine = 720.0 * $pitch
    $pWhine += $pi2 * $fWhine / $sampleRate
    $whine = [math]::Sin($pWhine) * 0.07

    # Wind/air noise — pink-ish low-passed white noise
    $rawNoise = ($rng.NextDouble() * 2.0 - 1.0)
    $noiseLP = $noiseLP * 0.92 + $rawNoise * 0.08
    $noise = $noiseLP * 0.20

    # Mix
    $sample = ($sub + $eng + $prop + $whine + $noise) * $env * 26000.0
    $samples[$i] = Clamp16 $sample
}

$outPath = Join-Path $outDir "Plane_flyby.wav"
Write-WavFile -Path $outPath -Samples $samples
Write-Host "Done."
