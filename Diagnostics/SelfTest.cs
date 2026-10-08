using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VRCGuiter.Audio;
using VRCGuiter.Audio.Dsp;
using VRCGuiter.VirtualMic;

namespace VRCGuiter.Diagnostics;

/// <summary>開発用: VRCGuiter.exe --selftest ログファイル で実行。DSP と実デバイスの疎通を確認する。</summary>
public static class SelfTest
{
    private const int Sr = 48000;

    public static int Run(string logPath)
    {
        var sb = new StringBuilder();
        int failures = 0;
        void Log(string s) => sb.AppendLine(s);
        void Check(bool ok, string what) { sb.AppendLine((ok ? "[PASS] " : "[FAIL] ") + what); if (!ok) failures++; }

        try
        {
            TestDsp(Log, Check);

            using var en = new MMDeviceEnumerator();
            Log("--- devices ---");
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
                Log($"{d.DataFlow}: {d.FriendlyName} | adapter={VirtualCable.AdapterOf(d)}");
            var cables = VirtualCable.Detect(en);
            Log("--- cables ---");
            foreach (var c in cables)
                Log($"{c.Kind} pri={c.Priority} label={c.Label} render={c.Render.Name} capture={c.Capture.Name}");
            foreach (var c in cables)
            {
                try { TestLoopback(c, Log, Check); }
                catch (Exception ex) { Log("loopback exception (" + c.Label + "): " + ex.Message); }
            }
            TestEngine(en, cables, Log, Check);
        }
        catch (Exception ex)
        {
            Log("EXCEPTION: " + ex);
            failures++;
        }
        Log(failures == 0 ? "ALL PASS" : $"FAILURES: {failures}");
        File.WriteAllText(logPath, sb.ToString());
        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ DSP

    private static void TestDsp(Action<string> log, Action<bool, string> check)
    {
        log("--- dsp ---");
        var rnd = new Random(12345);
        float Noise() => (float)(rnd.NextDouble() * 2 - 1) * 0.0173f; // RMS ≈ 0.01 (-40 dBFS)

        // ノイズ除去: 学習 → 同じ種類のノイズがどれだけ減るか
        var nr = new SpectralNoiseReducer { Strength = 0.6f };
        nr.StartLearning(2 * Sr / SpectralNoiseReducer.Hop);
        var blk = new float[480];
        for (int b = 0; b < 2 * Sr / 480 + 2; b++) { for (int i = 0; i < 480; i++) blk[i] = Noise(); nr.Process(blk, 480); }
        check(nr.HasProfile, "noise profile learned");
        double inSq = 0, outSq = 0; long cnt = 0;
        for (int b = 0; b < 2 * Sr / 480; b++)
        {
            for (int i = 0; i < 480; i++) blk[i] = Noise();
            double s = 0; for (int i = 0; i < 480; i++) s += blk[i] * blk[i];
            nr.Process(blk, 480);
            if (b < 30) continue; // 立ち上がりは除外
            inSq += s; for (int i = 0; i < 480; i++) outSq += blk[i] * blk[i]; cnt += 480;
        }
        double reduction = 10 * Math.Log10(inSq / Math.Max(outSq, 1e-20));
        log($"noise reduction (stationary noise, strength 60%): {reduction:0.0} dB");
        check(reduction >= 12, "noise reduced by >= 12 dB");

        // トーン（楽器音の代わり）が残るか: 440Hz -20dBFS + ノイズ
        double inTone = 0, outTone = 0; int toneBlocks = 0;
        double ph = 0;
        var inBlk = new float[480];
        var g1 = new Goertzel(440, Sr); var g2 = new Goertzel(440, Sr);
        for (int b = 0; b < 2 * Sr / 480; b++)
        {
            for (int i = 0; i < 480; i++) { inBlk[i] = 0.1f * (float)Math.Sin(ph) + Noise(); ph += 2 * Math.PI * 440 / Sr; blk[i] = inBlk[i]; }
            nr.Process(blk, 480);
            if (b < Sr / 480) continue;
            g1.Add(inBlk, 480); g2.Add(blk, 480); toneBlocks++;
        }
        inTone = g1.Power(); outTone = g2.Power();
        double toneDiff = 10 * Math.Log10(outTone / inTone);
        log($"tone retention at 440Hz: {toneDiff:+0.00;-0.00} dB ({toneBlocks} blocks)");
        check(Math.Abs(toneDiff) <= 1.5, "tone kept within 1.5 dB");

        // ゲート
        var gate = new NoiseGate { ThresholdDb = -60 };
        gate.Configure(Sr);
        float last = 1;
        for (int b = 0; b < Sr / 480 * 2; b++) { for (int i = 0; i < 480; i++) blk[i] = 0.0001f * (i % 2 == 0 ? 1 : -1); gate.Process(blk, 480); last = Math.Abs(blk[479]); }
        check(last < 1e-6f, $"gate closes on -80 dB signal (residual {last:E2})");
        for (int b = 0; b < 20; b++) { for (int i = 0; i < 480; i++) blk[i] = 0.1f * (float)Math.Sin(i * 0.1); gate.Process(blk, 480); }
        check(Math.Abs(blk[200]) > 0.05f, "gate opens on -20 dB signal");

        // リバーブ: インパルスに尾がつき、減衰する
        var rev = new Freeverb { Wet = 0.3f, Room = 0.55f, Damp = 0.5f, PreDelayMs = 20 };
        rev.Configure(Sr);
        double e100 = 0, e1500 = 0, e3000 = 0; bool nan = false;
        for (int b = 0; b < Sr / 480 * 4; b++)
        {
            Array.Clear(blk); if (b == 0) blk[0] = 1f;
            rev.Process(blk, 480);
            double e = 0; for (int i = 0; i < 480; i++) { e += blk[i] * blk[i]; if (float.IsNaN(blk[i])) nan = true; }
            int ms = b * 10;
            if (ms >= 100 && ms < 300) e100 += e; else if (ms >= 1500 && ms < 1700) e1500 += e; else if (ms >= 3000 && ms < 3200) e3000 += e;
        }
        log($"reverb tail energy: 100ms={e100:E2} 1500ms={e1500:E2} 3000ms={e3000:E2}");
        check(!nan && e100 > 1e-6 && e1500 < e100 && e3000 < e1500, "reverb tail present and decaying");

        // リミッター
        var lim = new PeakLimiter(); lim.Configure(Sr);
        float mx = 0;
        for (int b = 0; b < 20; b++) { for (int i = 0; i < 480; i++) blk[i] = 3f * (float)Math.Sin(i * 0.2); lim.Process(blk, 480); for (int i = 0; i < 480; i++) mx = Math.Max(mx, Math.Abs(blk[i])); }
        check(mx <= 1.0f && mx >= 0.9f, $"limiter keeps peak <= 1.0 (max {mx:0.000})");

        // ハイパス
        var hp = Biquad.HighPass(Sr, 50);
        double lo = 0, hi = 0;
        for (int b = 0; b < 50; b++) { for (int i = 0; i < 480; i++) blk[i] = (float)Math.Sin(2 * Math.PI * 20 * (b * 480 + i) / Sr); hp.Process(blk, 480); if (b > 25) for (int i = 0; i < 480; i++) lo += blk[i] * blk[i]; }
        hp = Biquad.HighPass(Sr, 50);
        for (int b = 0; b < 50; b++) { for (int i = 0; i < 480; i++) blk[i] = (float)Math.Sin(2 * Math.PI * 1000 * (b * 480 + i) / Sr); hp.Process(blk, 480); if (b > 25) for (int i = 0; i < 480; i++) hi += blk[i] * blk[i]; }
        log($"highpass: 20Hz {10 * Math.Log10(lo / (24 * 480 * 0.5)):0.0} dB, 1kHz {10 * Math.Log10(hi / (24 * 480 * 0.5)):0.0} dB");
        check(lo < hi * 0.1, "highpass attenuates 20 Hz by > 10 dB");

        // サンプル変換
        var wf16 = new WaveFormat(48000, 16, 2);
        var bytes = new byte[8];
        BitConverter.GetBytes((short)16384).CopyTo(bytes, 0); BitConverter.GetBytes((short)-16384).CopyTo(bytes, 2);
        BitConverter.GetBytes((short)32767).CopyTo(bytes, 4); BitConverter.GetBytes((short)32767).CopyTo(bytes, 6);
        var mono = new float[4];
        int n = SampleConverter.ToMono(bytes, 8, wf16, ref mono);
        check(n == 2 && Math.Abs(mono[0]) < 1e-6 && Math.Abs(mono[1] - 0.99997) < 1e-3, $"pcm16 stereo → mono ({mono[0]:0.000}, {mono[1]:0.000})");
    }

    private sealed class Goertzel
    {
        private readonly double _coeff; private double _s1, _s2; private long _n;
        public Goertzel(double freq, double sr) { _coeff = 2 * Math.Cos(2 * Math.PI * freq / sr); }
        public void Add(float[] x, int n) { for (int i = 0; i < n; i++) { double s = x[i] + _coeff * _s1 - _s2; _s2 = _s1; _s1 = s; } _n += n; }
        public double Power() => (_s1 * _s1 + _s2 * _s2 - _coeff * _s1 * _s2) / _n;
    }

    // ------------------------------------------------------------------ 実デバイス

    private static void TestLoopback(CablePair c, Action<string> log, Action<bool, string> check)
    {
        log($"--- loopback: {c.Label} ---");
        using var sink = new PlaybackSink(c.Render.Device, Sr, 30, 1000);
        using var cap = new WasapiCapture(c.Capture.Device, true, 20);
        double sumSq = 0; long cnt = 0; var tmp = new float[8192];
        var gate = new object();
        cap.DataAvailable += (_, e) =>
        {
            int n = SampleConverter.ToMono(e.Buffer, e.BytesRecorded, cap.WaveFormat, ref tmp);
            lock (gate) { for (int i = 0; i < n; i++) sumSq += tmp[i] * tmp[i]; cnt += n; }
        };
        cap.StartRecording();
        sink.Start();
        Thread.Sleep(700);
        double silence; lock (gate) { silence = cnt > 0 ? Math.Sqrt(sumSq / cnt) : 0; sumSq = 0; cnt = 0; }

        var blk = new float[480]; double ph = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool measuring = false;
        while (sw.ElapsedMilliseconds < 1800)
        {
            while (sink.BufferedMs > 120) Thread.Sleep(2);
            for (int i = 0; i < 480; i++) { blk[i] = 0.25f * (float)Math.Sin(ph); ph += 2 * Math.PI * 1000 / Sr; }
            sink.Write(blk, 480);
            if (!measuring && sw.ElapsedMilliseconds > 400) { lock (gate) { sumSq = 0; cnt = 0; } measuring = true; }
        }
        double tone; long toneCnt; lock (gate) { tone = cnt > 0 ? Math.Sqrt(sumSq / cnt) : 0; toneCnt = cnt; }
        cap.StopRecording();
        log($"capture format: {cap.WaveFormat}  silence rms={silence:E2}  tone rms={tone:0.0000} ({toneCnt} samples)");
        check(tone > 0.01 && tone > silence * 10, $"loopback passes audio through {c.Label}");
    }

    private static void TestEngine(MMDeviceEnumerator en, List<CablePair> cables, Action<string> log, Action<bool, string> check)
    {
        log("--- engine ---");
        var captures = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Where(d => cables.All(c => c.Capture.Id != d.ID)).ToList();
        var input = captures.FirstOrDefault(d => d.FriendlyName.Contains("USB", StringComparison.OrdinalIgnoreCase)) ?? captures.FirstOrDefault();
        var output = cables.FirstOrDefault()?.Render.Device;
        if (input == null || output == null) { log("skip: no input or cable"); return; }
        log($"input={input.FriendlyName} output={output.FriendlyName}");

        foreach (bool exclusive in new[] { false, true })
        {
            using var engine = new AudioEngine();
            Exception? stoppedWith = null; bool stopped = false;
            engine.Stopped += ex => { stopped = true; stoppedWith = ex; };
            try
            {
                engine.Start(input, output, exclusive, null);
                Thread.Sleep(1500);
                float pk = engine.ReadInputPeak();
                log($"exclusive={exclusive}: running rate={engine.SampleRate} actualExclusive={engine.Exclusive} inputPeak={pk:0.0000} latency≈{engine.LatencyMsEstimate}ms");
                engine.StartNoiseLearning(0.5);
                Thread.Sleep(900);
                check(engine.NoiseReducer.HasProfile, $"engine noise learning completes (exclusive={exclusive})");
                check(!stopped, $"engine ran without stopping (exclusive={exclusive}) {stoppedWith?.Message}");
                engine.Stop();
            }
            catch (Exception ex)
            {
                log($"exclusive={exclusive}: start failed: {ex.Message}");
                check(!exclusive, $"engine start (exclusive={exclusive})");
            }
        }
    }
}
