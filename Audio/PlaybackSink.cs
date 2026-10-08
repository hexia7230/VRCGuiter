using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace VRCGuiter.Audio;

/// <summary>モノラル float を受け取り、指定の再生デバイスへ流す（必要ならリサンプリング）。</summary>
public sealed class PlaybackSink : IDisposable
{
    private readonly WasapiOut _out;
    private readonly BufferedWaveProvider _bwp;
    private readonly WdlResampler? _rs;
    private readonly int _channels;
    private float[] _tmp = new float[16384];
    private byte[] _bytes = new byte[65536];
    private bool _disposed;

    public int SampleRate { get; }
    public string DeviceName { get; }

    public PlaybackSink(MMDevice device, int inputRate, int latencyMs = 30, int bufferMs = 600)
    {
        DeviceName = device.FriendlyName;
        WaveFormat mix;
        using (var ac = device.AudioClient) mix = ac.MixFormat;
        WaveFormat fmt = SampleConverter.IsFloat(mix) ? mix : WaveFormat.CreateIeeeFloatWaveFormat(mix.SampleRate, mix.Channels);
        SampleRate = fmt.SampleRate;
        _channels = fmt.Channels;
        _bwp = new BufferedWaveProvider(fmt)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
            BufferDuration = TimeSpan.FromMilliseconds(bufferMs),
        };
        _out = new WasapiOut(device, AudioClientShareMode.Shared, true, latencyMs);
        _out.Init(_bwp);
        if (inputRate != SampleRate)
        {
            _rs = new WdlResampler();
            _rs.SetMode(true, 2, false);
            _rs.SetFilterParms();
            _rs.SetFeedMode(true);
            _rs.SetRates(inputRate, SampleRate);
        }
        // 最初のアンダーランを避けるための無音
        int pre = SampleRate * 40 / 1000;
        var silence = new byte[pre * _channels * 4];
        _bwp.AddSamples(silence, 0, silence.Length);
    }

    public void Start() => _out.Play();

    public double BufferedMs => _bwp.BufferedDuration.TotalMilliseconds;

    public void Write(float[] mono, int n)
    {
        if (_disposed || n <= 0) return;
        // 入出力のクロックずれでバッファが溜まり続けた場合はこのブロックを捨てる
        if (BufferedMs > 250) return;

        float[] src = mono; int cnt = n;
        if (_rs != null)
        {
            int need = _rs.ResamplePrepare(n, 1, out float[] inbuf, out int off);
            Array.Copy(mono, 0, inbuf, off, Math.Min(n, need));
            if (_tmp.Length < n * 3 + 64) _tmp = new float[n * 3 + 64];
            cnt = _rs.ResampleOut(_tmp, 0, n, _tmp.Length, 1);
            src = _tmp;
        }
        int bytes = cnt * _channels * 4;
        if (_bytes.Length < bytes) _bytes = new byte[bytes * 2];
        int p = 0;
        for (int i = 0; i < cnt; i++)
        {
            int bits = BitConverter.SingleToInt32Bits(src[i]);
            for (int c = 0; c < _channels; c++)
            {
                _bytes[p++] = (byte)bits; _bytes[p++] = (byte)(bits >> 8);
                _bytes[p++] = (byte)(bits >> 16); _bytes[p++] = (byte)(bits >> 24);
            }
        }
        _bwp.AddSamples(_bytes, 0, bytes);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _out.Stop(); } catch { }
        try { _out.Dispose(); } catch { }
    }
}
