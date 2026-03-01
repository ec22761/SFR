# GenerateVacuumSound.ps1
# Generates a vacuum/suction WAV sound effect (~0.5s).
# Low whooshing air rush that sounds like objects being pulled in.
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
$rng = New-Object System.Random(77)

# ============================================================
# JunkCannonVacuum.wav — Beefy whooshing vacuum/suction (~0.5s)
# Descending filtered noise rush with low sub-bass rumble.
# Sounds like air and debris being sucked into the weapon.
# ============================================================
Write-Host "Generating JunkCannonVacuum..."
$duration = 0.5
$numSamples = [int]($sampleRate * $duration)
$samples = New-Object Int16[] $numSamples
$phase1 = 0.0; $phase2 = 0.0; $phase3 = 0.0; $phase4 = 0.0
$lpState = 0.0  # low-pass filter state

for ($i = 0; $i -lt $numSamples; $i++) {
    $t = $i / $sampleRate
    $tNorm = $t / $duration  # 0 -> 1

    # Envelope: fast attack, sustain, quick release at end
    $attack = [math]::Min(1.0, $tNorm * 8.0)
    $release = [math]::Min(1.0, (1.0 - $tNorm) * 5.0)
    $env = $attack * $release

    # --- Layer 1: Filtered noise (the main "whoosh") ---
    # Cutoff frequency descends: high->low (suction pulling inward)
    $cutoff = 3000.0 + 4000.0 * (1.0 - $tNorm) * (1.0 - $tNorm)
    $lpCoeff = [math]::Min(1.0, $pi2 * $cutoff / $sampleRate)
    $noise = $rng.NextDouble() * 2.0 - 1.0
    $lpState = $lpState + $lpCoeff * ($noise - $lpState)
    $whoosh = $lpState * 0.7

    # --- Layer 2: Sub-bass rumble (mechanical suction) ---
    $bassFreq = 65.0 + 20.0 * [math]::Sin($pi2 * 2.5 * $tNorm)
    $phase1 += $pi2 * $bassFreq / $sampleRate
    $bass = 0.35 * [math]::Sin($phase1)
    $bass += 0.15 * [math]::Sin($phase1 * 2.01)  # sub-harmonic

    # --- Layer 3: Turbulent mid-range whoosh ---
    $midFreq = 180.0 + 120.0 * (1.0 - $tNorm)
    $phase2 += $pi2 * $midFreq / $sampleRate
    # FM modulation for turbulence
    $turbMod = 40.0 * [math]::Sin($phase2 * 3.7)
    $phase3 += $pi2 * ($midFreq + $turbMod) / $sampleRate
    $mid = 0.2 * [math]::Sin($phase3)

    # --- Layer 4: High rattling debris noise ---
    $debrisNoise = ($rng.NextDouble() * 2.0 - 1.0)
    # Pulsate debris to sound like objects being pulled
    $phase4 += $pi2 * 25.0 / $sampleRate
    $debrisPulse = 0.5 + 0.5 * [math]::Sin($phase4)
    $debris = $debrisNoise * 0.08 * $debrisPulse * $tNorm

    # --- Mix everything ---
    $sig = $whoosh + $bass + $mid + $debris

    # Soft-clip for warmth
    $sig = [math]::Tanh($sig * 1.3)

    $samples[$i] = Clamp16 ($sig * $env * 24000)
}
Write-WavFile -Path (Join-Path $outDir "JunkCannonVacuum.wav") -Samples $samples

Write-Host "Done!"
