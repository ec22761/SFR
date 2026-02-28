# DeepenSound.ps1 — Pitch-shift a WAV file down by resampling.
# Usage: .\DeepenSound.ps1 -InputPath <path> -OutputPath <path> -PitchFactor <float>
# PitchFactor < 1.0 = deeper (e.g., 0.75 = 25% deeper)

param(
    [Parameter(Mandatory)][string]$InputPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][float]$PitchFactor
)

Add-Type -TypeDefinition @"
using System;
using System.IO;

public static class WavPitchShift
{
    public static void Deepen(string inputPath, string outputPath, double pitchFactor)
    {
        byte[] fileBytes = File.ReadAllBytes(inputPath);

        // Parse WAV header
        if (fileBytes.Length < 44)
            throw new Exception("File too small to be a valid WAV.");

        int numChannels = BitConverter.ToInt16(fileBytes, 22);
        int sampleRate = BitConverter.ToInt32(fileBytes, 24);
        int bitsPerSample = BitConverter.ToInt16(fileBytes, 34);
        int bytesPerSample = bitsPerSample / 8;

        // Find data chunk
        int dataOffset = 12;
        int dataSize = 0;
        while (dataOffset < fileBytes.Length - 8)
        {
            string chunkId = System.Text.Encoding.ASCII.GetString(fileBytes, dataOffset, 4);
            int chunkSize = BitConverter.ToInt32(fileBytes, dataOffset + 4);
            if (chunkId == "data")
            {
                dataOffset += 8;
                dataSize = chunkSize;
                break;
            }
            dataOffset += 8 + chunkSize;
        }

        if (dataSize == 0)
            throw new Exception("Could not find data chunk in WAV file.");

        int totalSamples = dataSize / bytesPerSample;
        int samplesPerChannel = totalSamples / numChannels;

        // Read samples as doubles
        double[] samples = new double[totalSamples];
        for (int i = 0; i < totalSamples; i++)
        {
            int pos = dataOffset + i * bytesPerSample;
            if (bytesPerSample == 2)
            {
                short val = BitConverter.ToInt16(fileBytes, pos);
                samples[i] = val / 32768.0;
            }
            else if (bytesPerSample == 1)
            {
                samples[i] = (fileBytes[pos] - 128) / 128.0;
            }
            else if (bytesPerSample == 4)
            {
                int val = BitConverter.ToInt32(fileBytes, pos);
                samples[i] = val / 2147483648.0;
            }
        }

        // Resample: to pitch down, we stretch the audio (more samples)
        // then keep the same sample rate, which makes it sound deeper.
        int newSamplesPerChannel = (int)(samplesPerChannel / pitchFactor);
        int newTotalSamples = newSamplesPerChannel * numChannels;
        double[] newSamples = new double[newTotalSamples];

        for (int ch = 0; ch < numChannels; ch++)
        {
            for (int i = 0; i < newSamplesPerChannel; i++)
            {
                double srcIdx = i * pitchFactor;
                int idx0 = (int)srcIdx;
                int idx1 = idx0 + 1;
                double frac = srcIdx - idx0;

                if (idx1 >= samplesPerChannel) idx1 = samplesPerChannel - 1;

                double s0 = samples[idx0 * numChannels + ch];
                double s1 = samples[idx1 * numChannels + ch];
                newSamples[i * numChannels + ch] = s0 + (s1 - s0) * frac;
            }
        }

        // Write output WAV
        int newDataSize = newTotalSamples * bytesPerSample;
        int byteRate = sampleRate * numChannels * bytesPerSample;
        int blockAlign = numChannels * bytesPerSample;

        using (var fs = new FileStream(outputPath, FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            // RIFF header
            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + newDataSize);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            // fmt chunk
            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16); // chunk size
            bw.Write((short)1); // PCM
            bw.Write((short)numChannels);
            bw.Write(sampleRate); // keep original sample rate
            bw.Write(byteRate);
            bw.Write((short)blockAlign);
            bw.Write((short)bitsPerSample);

            // data chunk
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(newDataSize);

            for (int i = 0; i < newTotalSamples; i++)
            {
                double val = Math.Max(-1.0, Math.Min(1.0, newSamples[i]));
                if (bytesPerSample == 2)
                {
                    short s = (short)(val * 32767);
                    bw.Write(s);
                }
                else if (bytesPerSample == 1)
                {
                    byte b = (byte)((val * 128) + 128);
                    bw.Write(b);
                }
                else if (bytesPerSample == 4)
                {
                    int s = (int)(val * 2147483647);
                    bw.Write(s);
                }
            }
        }

        Console.WriteLine("Written: " + outputPath + " (" + newTotalSamples + " samples, pitch factor " + pitchFactor + ")");
    }
}
"@ -Language CSharp -ErrorAction Stop

[WavPitchShift]::Deepen($InputPath, $OutputPath, $PitchFactor)
