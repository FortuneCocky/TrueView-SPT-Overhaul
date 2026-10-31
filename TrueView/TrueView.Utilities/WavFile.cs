using System;
using System.IO;

namespace TrueView.Utilities;

internal static class WavFile
{
	/// <summary>Reads a 16-bit PCM WAV and mixes it down to mono samples in -1..1.</summary>
	public static float[] ReadMono(string path, out int sampleRate)
	{
		byte[] b = File.ReadAllBytes(path);
		if (b.Length < 44 || b[0] != 'R' || b[8] != 'W') throw new InvalidDataException($"{Path.GetFileName(path)} is not a WAV file");
		int channels = 0, rate = 0, bits = 0, dataPos = -1, dataLen = 0;
		for (int i = 12; i + 8 <= b.Length;)
		{
			string id = System.Text.Encoding.ASCII.GetString(b, i, 4);
			int size = BitConverter.ToInt32(b, i + 4);
			if (id == "fmt ")
			{
				channels = BitConverter.ToInt16(b, i + 10);
				rate = BitConverter.ToInt32(b, i + 12);
				bits = BitConverter.ToInt16(b, i + 22);
			}
			else if (id == "data")
			{
				dataPos = i + 8;
				dataLen = Math.Min(size, b.Length - dataPos);
				break;
			}
			i += 8 + size + (size & 1);
		}
		if (dataPos < 0 || bits != 16 || channels < 1) throw new InvalidDataException($"{Path.GetFileName(path)}: need 16-bit PCM");

		int frames = dataLen / (2 * channels);
		var mono = new float[frames];
		for (int f = 0; f < frames; f++)
		{
			float sum = 0f;
			int p = dataPos + f * 2 * channels;
			for (int c = 0; c < channels; c++) sum += BitConverter.ToInt16(b, p + c * 2) / 32768f;
			mono[f] = sum / channels;
		}
		sampleRate = rate;
		return mono;
	}
}
