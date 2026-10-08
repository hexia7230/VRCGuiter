using NAudio.CoreAudioApi;
using NAudio.Wave;
using VRCGuiter.Audio.Dsp;

namespace VRCGuiter.Audio;

/// <summary>マイク → ハイパス → ノイズ除去 → ゲート → リバーブ → 音量 → リミッター → 仮想ケーブル（+モニター）</summary>
public sealed class AudioEngine : IDisposable
{
    private readonly object _lock = new();
    private WasapiCapture? _capture;
    private PlaybackSink? _cable;
    private PlaybackSink? _monitor;
    private Biquad _hpf = Biquad.HighPass(48000, 50);
    private float[] _mono = new float[8192];
    private float _inPeak, _outPeak;

    public SpectralNoiseReducer NoiseReducer { get; } = new();
    public NoiseGate Gate { get; } = new();
    public Freeverb Reverb { get; } = new();
    public PeakLimiter Limiter { get; } = new();

    /// <summary>リニア倍率</summary>
    public volatile float OutputGain = 1f;

    public int SampleRate { get; private set; }
    public bool Exclusive { get; private set; }
    public bool Running => _capture != null;
    public int LatencyMsEstimate => 20 + SpectralNoiseReducer.FftSize * 1000 / Math.Max(1, SampleRate) + 30;

    /// <summary>入力/出力ピーク（0..1）。読み出すとリセット。</summary>
    public float ReadInputPeak() { float v = _inPeak; _inPeak = 0; return v; }
    public float ReadOutputPeak() { float v = _outPeak; _outPeak = 0; return v; }

    /// <summary>デバイスエラー等で停止したとき（例外は null の場合あり）。</summary>
    public event Action<Exception?>? Stopped;
    /// <summary>ノイズ学習完了（オーディオスレッドから）</summary>
    public event Action<float[]>? NoiseLearned;

    public AudioEngine()
    {
        NoiseReducer.LearnCompleted += p => NoiseLearned?.Invoke(p);
    }

    public void Start(MMDevice input, MMDevice output, bool exclusive, MMDevice? monitor)
    {
        Stop();
        // 排他モードはデバイスによって開けないことがあるので、失敗したら共有モードで開き直す
        Exception? lastError = null;
        foreach (bool tryExclusive in exclusive ? new[] { true, false } : new[] { false })
        {
            WasapiCapture? capture = null;
            try
            {
                capture = CreateCapture(input, tryExclusive, out bool isExclusive);
                if (tryExclusive && !isExclusive) { capture.Dispose(); continue; }
                StartWith(capture, output, monitor, isExclusive);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                try { capture?.Dispose(); } catch { }
                if (!tryExclusive) throw;
            }
        }
        throw lastError ?? new InvalidOperationException("開始できません");
    }

    private void StartWith(WasapiCapture capture, MMDevice output, MMDevice? monitor, bool isExclusive)
    {
        try
        {
            SampleRate = capture.WaveFormat.SampleRate;
            Exclusive = isExclusive;
            _hpf = Biquad.HighPass(SampleRate, 50);
            NoiseReducer.Reset();
            Gate.Configure(SampleRate);
            Reverb.Configure(SampleRate);
            Limiter.Configure(SampleRate);

            var cable = new PlaybackSink(output, SampleRate);
            PlaybackSink? mon = null;
            if (monitor != null)
            {
                try { mon = new PlaybackSink(monitor, SampleRate); } catch { mon = null; }
            }
            lock (_lock) { _cable = cable; _monitor = mon; }
            cable.Start();
            mon?.Start();

            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnRecordingStopped;
            _capture = capture;
            capture.StartRecording();
        }
        catch
        {
            _capture = null;
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnRecordingStopped;
            lock (_lock) { _cable?.Dispose(); _cable = null; _monitor?.Dispose(); _monitor = null; }
            throw;
        }
    }

    private static WasapiCapture CreateCapture(MMDevice input, bool exclusive, out bool isExclusive)
    {
        isExclusive = false;
        if (exclusive)
        {
            // 排他モード: Windows の音声処理（ノイズ抑制・AGC 等）を迂回する
            var candidates = new List<WaveFormat>();
            foreach (int rate in new[] { 48000, 44100, 96000 })
                foreach (int ch in new[] { 1, 2 })
                {
                    candidates.Add(new WaveFormatExtensible(rate, 24, ch));
                    candidates.Add(new WaveFormatExtensible(rate, 32, ch));
                    candidates.Add(new WaveFormat(rate, 16, ch));
                    candidates.Add(new WaveFormatExtensible(rate, 16, ch));
                }
            foreach (var fmt in candidates)
            {
                bool ok;
                try { using var ac = input.AudioClient; ok = ac.IsFormatSupported(AudioClientShareMode.Exclusive, fmt); }
                catch { ok = false; }
                if (!ok) continue;
                // 排他はイベント駆動だとバッファ境界の制約が厳しいのでポーリングで開く
                var c = new RawWasapiCapture(input, false, 20) { ShareMode = AudioClientShareMode.Exclusive, WaveFormat = fmt };
                isExclusive = true;
                return c;
            }
            // 対応フォーマットが無ければ共有モードにフォールバック
        }
        return new RawWasapiCapture(input, true, 20);
    }

    /// <summary>NAudio は共有モード用の自動変換フラグを排他モードでも付けてしまい E_INVALIDARG になるので外す。</summary>
    private sealed class RawWasapiCapture : WasapiCapture
    {
        public RawWasapiCapture(MMDevice device, bool useEventSync, int bufferMs) : base(device, useEventSync, bufferMs) { }

        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            ShareMode == AudioClientShareMode.Exclusive ? AudioClientStreamFlags.None : base.GetAudioClientStreamFlags();
    }

    public void SetMonitor(MMDevice? device)
    {
        PlaybackSink? old;
        PlaybackSink? created = null;
        if (device != null && Running)
        {
            created = new PlaybackSink(device, SampleRate);
            created.Start();
        }
        lock (_lock) { old = _monitor; _monitor = created; }
        old?.Dispose();
    }

    public void StartNoiseLearning(double seconds)
    {
        if (!Running) return;
        NoiseReducer.StartLearning((int)(seconds * SampleRate / SpectralNoiseReducer.Hop));
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var cap = _capture;
        if (cap == null) return;
        int n = SampleConverter.ToMono(e.Buffer, e.BytesRecorded, cap.WaveFormat, ref _mono);
        if (n == 0) return;
        var buf = _mono;

        float pk = 0f;
        for (int i = 0; i < n; i++) { float a = Math.Abs(buf[i]); if (a > pk) pk = a; }
        if (pk > _inPeak) _inPeak = pk;

        _hpf.Process(buf, n);
        NoiseReducer.Process(buf, n);
        Gate.Process(buf, n);
        Reverb.Process(buf, n);
        float g = OutputGain;
        if (g != 1f) for (int i = 0; i < n; i++) buf[i] *= g;
        Limiter.Process(buf, n);

        pk = 0f;
        for (int i = 0; i < n; i++) { float a = Math.Abs(buf[i]); if (a > pk) pk = a; }
        if (pk > _outPeak) _outPeak = pk;

        lock (_lock)
        {
            _cable?.Write(buf, n);
            _monitor?.Write(buf, n);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_capture == null) return; // 自分で止めたとき
        var ex = e.Exception;
        Stop();
        Stopped?.Invoke(ex);
    }

    public void Stop()
    {
        var cap = _capture;
        _capture = null;
        if (cap != null)
        {
            cap.DataAvailable -= OnData;
            cap.RecordingStopped -= OnRecordingStopped;
            try { cap.StopRecording(); } catch { }
            try { cap.Dispose(); } catch { }
        }
        PlaybackSink? c, m;
        lock (_lock) { c = _cable; m = _monitor; _cable = null; _monitor = null; }
        c?.Dispose();
        m?.Dispose();
    }

    public void Dispose() => Stop();
}
