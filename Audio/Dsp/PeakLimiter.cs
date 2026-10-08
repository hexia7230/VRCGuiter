namespace VRCGuiter.Audio.Dsp;

public sealed class PeakLimiter
{
    public float Ceiling = 0.95f;
    private float _env, _release;

    public void Configure(int sampleRate)
    {
        _release = (float)Math.Exp(-1.0 / (0.15 * sampleRate));
        _env = 0;
    }

    public void Process(float[] x, int n)
    {
        for (int i = 0; i < n; i++)
        {
            float a = Math.Abs(x[i]);
            _env = a > _env ? a : a + (_env - a) * _release;
            float g = _env > Ceiling ? Ceiling / _env : 1f;
            float y = x[i] * g;
            x[i] = y > 1f ? 1f : (y < -1f ? -1f : y);
        }
    }
}
