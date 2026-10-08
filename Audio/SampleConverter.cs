using NAudio.Wave;

namespace VRCGuiter.Audio;

public static class SampleConverter
{
    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    public static bool IsFloat(WaveFormat wf) =>
        wf.Encoding == WaveFormatEncoding.IeeeFloat ||
        (wf is WaveFormatExtensible ex && ex.SubFormat == FloatSubtype);

    /// <summary>任意の PCM/float バッファをモノラル float に変換（全チャンネル平均）。戻り値 = フレーム数。</summary>
    public static int ToMono(byte[] buf, int bytes, WaveFormat wf, ref float[] dst)
    {
        int ch = wf.Channels, bps = wf.BitsPerSample / 8;
        if (ch <= 0 || bps <= 0) return 0;
        int frames = bytes / (ch * bps);
        if (dst.Length < frames) dst = new float[Math.Max(frames, dst.Length * 2)];
        bool fl = IsFloat(wf);
        float norm = 1f / ch;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0f;
            int o = f * ch * bps;
            for (int c = 0; c < ch; c++, o += bps) sum += Read(buf, o, bps, fl);
            dst[f] = sum * norm;
        }
        return frames;
    }

    private static float Read(byte[] b, int o, int bps, bool fl) => bps switch
    {
        4 when fl => BitConverter.ToSingle(b, o),
        4 => BitConverter.ToInt32(b, o) / 2147483648f,
        3 => ((b[o] << 8) | (b[o + 1] << 16) | (b[o + 2] << 24)) / 2147483648f,
        2 => BitConverter.ToInt16(b, o) / 32768f,
        _ => (b[o] - 128) / 128f,
    };
}
