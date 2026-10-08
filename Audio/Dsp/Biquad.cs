namespace VRCGuiter.Audio.Dsp;

public sealed class Biquad
{
    private float _b0, _b1, _b2, _a1, _a2, _z1, _z2;

    public static Biquad HighPass(double sampleRate, double cutoffHz, double q = 0.7071)
    {
        var bq = new Biquad();
        double w0 = 2 * Math.PI * cutoffHz / sampleRate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double alpha = sin / (2 * q);
        double b0 = (1 + cos) / 2, b1 = -(1 + cos), b2 = (1 + cos) / 2;
        double a0 = 1 + alpha, a1 = -2 * cos, a2 = 1 - alpha;
        bq._b0 = (float)(b0 / a0); bq._b1 = (float)(b1 / a0); bq._b2 = (float)(b2 / a0);
        bq._a1 = (float)(a1 / a0); bq._a2 = (float)(a2 / a0);
        return bq;
    }

    public void Process(float[] x, int n)
    {
        for (int i = 0; i < n; i++)
        {
            float input = x[i];
            float y = _b0 * input + _z1;
            _z1 = _b1 * input - _a1 * y + _z2;
            _z2 = _b2 * input - _a2 * y;
            x[i] = y;
        }
    }
}
