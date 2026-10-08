using NAudio.Dsp;

namespace VRCGuiter.Audio.Dsp;

public sealed class SpectralNoiseReducer
{
    public const int FftSize = 1024;
    public const int Hop = 256;
    public const int Bins = FftSize / 2 + 1;
    private const int LogN = 10;
    private const float Beta = 0.95f;

    private readonly float[] _window = new float[FftSize];
    private readonly float _olaNorm;
    private readonly float[] _frame = new float[FftSize];
    private readonly float[] _ola = new float[FftSize];
    private readonly Complex[] _spec = new Complex[FftSize];
    private readonly float[] _hopBuf = new float[Hop];
    private int _hopCount;

    private readonly float[] _outRing = new float[1 << 15];
    private int _outHead, _outTail, _outCount;

    private float[]? _profile;
    private readonly float[] _prevGain = new float[Bins];
    private readonly float[] _prevPost = new float[Bins];

    private float[]? _learnSum;
    private int _learnFrames, _learnTarget;

    public volatile float Strength = 0.6f;

    public bool Learning => _learnSum != null;
    public bool HasProfile => _profile != null;

    public event Action<float[]>? LearnCompleted;

    public SpectralNoiseReducer()
    {
        for (int i = 0; i < FftSize; i++)
            _window[i] = (float)Math.Sqrt(0.5 * (1 - Math.Cos(2 * Math.PI * i / FftSize)));
        double norm = 0;
        for (int k = 0; k < FftSize / Hop; k++) norm += _window[k * Hop] * _window[k * Hop];
        _olaNorm = (float)norm;
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_frame); Array.Clear(_ola); Array.Clear(_hopBuf);
        _hopCount = 0; _outHead = _outTail = _outCount = 0;
        for (int i = 0; i < Hop; i++) Push(0f);
        Array.Fill(_prevGain, 1f); Array.Fill(_prevPost, 1f);
        _learnSum = null;
    }

    public void SetProfile(float[]? profile)
    {
        _profile = profile != null && profile.Length == Bins ? (float[])profile.Clone() : null;
        Array.Fill(_prevGain, 1f); Array.Fill(_prevPost, 1f);
    }

    public float[]? GetProfile() => _profile == null ? null : (float[])_profile.Clone();

    public void StartLearning(int frames)
    {
        _learnTarget = Math.Max(1, frames);
        _learnFrames = 0;
        _learnSum = new float[Bins];
    }

    public void Process(float[] buf, int n)
    {
        for (int i = 0; i < n; i++)
        {
            _hopBuf[_hopCount++] = buf[i];
            if (_hopCount == Hop) { ProcessHop(); _hopCount = 0; }
        }
        for (int i = 0; i < n; i++) buf[i] = Pop();
    }

    private void ProcessHop()
    {
        Array.Copy(_frame, Hop, _frame, 0, FftSize - Hop);
        Array.Copy(_hopBuf, 0, _frame, FftSize - Hop, Hop);
        for (int k = 0; k < FftSize; k++) { _spec[k].X = _frame[k] * _window[k]; _spec[k].Y = 0f; }
        FastFourierTransform.FFT(true, LogN, _spec);

        var learn = _learnSum;
        if (learn != null)
        {
            for (int k = 0; k < Bins; k++) learn[k] += _spec[k].X * _spec[k].X + _spec[k].Y * _spec[k].Y;
            if (++_learnFrames >= _learnTarget)
            {
                var profile = new float[Bins];
                for (int k = 0; k < Bins; k++) profile[k] = learn[k] / _learnFrames;
                _learnSum = null;
                SetProfile(profile);
                LearnCompleted?.Invoke(profile);
            }
        }
        else
        {
            var profile = _profile;
            float s = Strength;
            if (profile != null && s > 0.001f)
            {
                float floorG = (float)Math.Pow(10, -2.0 * s);
                float overSub = 1f + s;
                for (int k = 0; k < Bins; k++)
                {
                    float p = _spec[k].X * _spec[k].X + _spec[k].Y * _spec[k].Y;
                    float noise = profile[k] * overSub + 1e-20f;
                    float post = p / noise;
                    float xi = Beta * _prevGain[k] * _prevGain[k] * _prevPost[k] + (1f - Beta) * Math.Max(post - 1f, 0f);
                    float g = xi / (1f + xi);
                    if (g < floorG) g = floorG;
                    _prevGain[k] = g; _prevPost[k] = post;
                    _spec[k].X *= g; _spec[k].Y *= g;
                    if (k > 0 && k < FftSize / 2) { _spec[FftSize - k].X *= g; _spec[FftSize - k].Y *= g; }
                }
            }
        }

        FastFourierTransform.FFT(false, LogN, _spec);
        float inv = 1f / _olaNorm;
        for (int k = 0; k < FftSize; k++) _ola[k] += _spec[k].X * _window[k] * inv;
        for (int k = 0; k < Hop; k++) Push(_ola[k]);
        Array.Copy(_ola, Hop, _ola, 0, FftSize - Hop);
        Array.Clear(_ola, FftSize - Hop, Hop);
    }

    private void Push(float v)
    {
        if (_outCount == _outRing.Length) return;
        _outRing[_outTail] = v;
        _outTail = (_outTail + 1) % _outRing.Length;
        _outCount++;
    }

    private float Pop()
    {
        if (_outCount == 0) return 0f;
        float v = _outRing[_outHead];
        _outHead = (_outHead + 1) % _outRing.Length;
        _outCount--;
        return v;
    }
}
