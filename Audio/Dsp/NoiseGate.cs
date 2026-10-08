namespace VRCGuiter.Audio.Dsp;

public sealed class NoiseGate
{
    public volatile float ThresholdDb = -60f;

    private float _env, _gain = 1f, _envDecay, _attack, _release;
    private int _hold, _holdSamples;

    public void Configure(int sampleRate)
    {
        _envDecay = (float)Math.Exp(-1.0 / (0.02 * sampleRate));
        _attack = (float)(1 - Math.Exp(-1.0 / (0.003 * sampleRate)));
        _release = (float)(1 - Math.Exp(-1.0 / (0.3 * sampleRate)));
        _holdSamples = (int)(0.25 * sampleRate);
        _env = 0; _gain = 1; _hold = 0;
    }

    public void Process(float[] x, int n)
    {
        float th = ThresholdDb;
        if (th <= -99.5f) return;
        float thLin = (float)Math.Pow(10, th / 20);
        for (int i = 0; i < n; i++)
        {
            float a = Math.Abs(x[i]);
            _env = a > _env ? a : _env * _envDecay;
            float target;
            if (_env > thLin) { target = 1f; _hold = _holdSamples; }
            else if (_hold > 0) { _hold--; target = 1f; }
            else target = 0f;
            _gain += (target - _gain) * (target > _gain ? _attack : _release);
            x[i] *= _gain;
        }
    }
}
