namespace VRCGuiter.Audio.Dsp;

/// <summary>Freeverb（Schroeder/Moorer）のモノラル版 + プリディレイ。</summary>
public sealed class Freeverb
{
    private static readonly int[] CombTunings = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
    private static readonly int[] AllpassTunings = { 556, 441, 341, 225 };

    private float[][] _comb = Array.Empty<float[]>();
    private int[] _combIdx = Array.Empty<int>();
    private float[] _combStore = Array.Empty<float>();
    private float[][] _ap = Array.Empty<float[]>();
    private int[] _apIdx = Array.Empty<int>();
    private float[] _pre = Array.Empty<float>();
    private int _preIdx;
    private int _sampleRate = 48000;

    /// <summary>0..1 原音に混ぜる量</summary>
    public volatile float Wet = 0.3f;
    /// <summary>0..1 部屋の広さ（残響の長さ）</summary>
    public volatile float Room = 0.55f;
    /// <summary>0..1 高域の減衰（大きいほど暗い）</summary>
    public volatile float Damp = 0.5f;
    public volatile float PreDelayMs = 20f;

    public void Configure(int sampleRate)
    {
        _sampleRate = sampleRate;
        double s = sampleRate / 44100.0;
        _comb = CombTunings.Select(t => new float[Math.Max(8, (int)(t * s))]).ToArray();
        _combIdx = new int[CombTunings.Length];
        _combStore = new float[CombTunings.Length];
        _ap = AllpassTunings.Select(t => new float[Math.Max(8, (int)(t * s))]).ToArray();
        _apIdx = new int[AllpassTunings.Length];
        _pre = new float[(int)(0.2 * sampleRate) + 1];
        _preIdx = 0;
    }

    public void Process(float[] x, int n)
    {
        float wet = Wet;
        if (wet <= 0.001f) return;
        float room = 0.7f + 0.28f * Room;
        float damp1 = 0.4f * Damp, damp2 = 1f - damp1;
        int preSamples = Math.Clamp((int)(PreDelayMs * 0.001f * _sampleRate), 0, _pre.Length - 1);
        float dry = 1f - 0.5f * wet;
        float wetGain = wet * 1.2f;

        for (int i = 0; i < n; i++)
        {
            float input = x[i];

            _pre[_preIdx] = input;
            int rd = _preIdx - preSamples; if (rd < 0) rd += _pre.Length;
            float din = _pre[rd] * 0.015f + 1e-18f; // 1e-18f は非正規化数対策
            if (++_preIdx >= _pre.Length) _preIdx = 0;

            float sum = 0f;
            for (int c = 0; c < _comb.Length; c++)
            {
                var buf = _comb[c]; int idx = _combIdx[c];
                float o = buf[idx];
                _combStore[c] = o * damp2 + _combStore[c] * damp1;
                buf[idx] = din + _combStore[c] * room;
                if (++idx >= buf.Length) idx = 0;
                _combIdx[c] = idx;
                sum += o;
            }
            for (int a = 0; a < _ap.Length; a++)
            {
                var buf = _ap[a]; int idx = _apIdx[a];
                float bufout = buf[idx];
                float o = -sum + bufout;
                buf[idx] = sum + bufout * 0.5f;
                if (++idx >= buf.Length) idx = 0;
                _apIdx[a] = idx;
                sum = o;
            }
            x[i] = input * dry + sum * wetGain;
        }
    }
}
