﻿using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Burst;

namespace uLipSync
{

[BurstCompile]
public struct LipSyncJob : IJob
{
    public struct Result
    {
        public float f1;
        public float f2;
        public float f3;
        public float volume;
    }

    [ReadOnly] public NativeArray<float> input;
    [ReadOnly] public int startIndex;
    [ReadOnly] public int lpcOrder;
    [ReadOnly] public int sampleRate;
    [ReadOnly] public float volumeThresh;
    [ReadOnly] public int maxFreq;
    [ReadOnly] public float minLog10H;
    [ReadOnly] public float filterH;
    [ReadOnly] public WindowFunc windowFunc;
    public NativeArray<float> H;
    public NativeArray<float> dH;
    public NativeArray<float> ddH;
    public NativeArray<Result> result;

    public void Execute()
    {
        float volume = Algorithm.GetRMSVolume(ref input);
        if (volume < volumeThresh)
        {
            var res1 = result[0];
            var res2 = result[1];
            res1.volume = res2.volume = volume;
            result[0] = res1;
            result[1] = res2;
            return;
        }

        // copy input ring buffer to a temporary array
        var data = new NativeArray<float>(input.Length, Allocator.Temp);
        Algorithm.CopyRingBuffer(ref input, ref data, startIndex);

        // multiply window function
        Algorithm.ApplyWindow(ref data, windowFunc);

        // auto correlational function
        var r = new NativeArray<float>(lpcOrder + 1, Allocator.Temp);
        Algorithm.ZeroClear(ref r);
        for (int l = 0; l < lpcOrder + 1; ++l)
        {
            for (int n = 0; n < input.Length - l; ++n)
            {
                r[l] += data[n] * data[n + l];
            }
        }

        data.Dispose();

        // calculate LPC factors using Levinson-Durbin algorithm
        var a = new NativeArray<float>(lpcOrder + 1, Allocator.Temp);
        var e = new NativeArray<float>(lpcOrder + 1, Allocator.Temp);
        Algorithm.ZeroClear(ref a);
        Algorithm.ZeroClear(ref e);
        a[0] = 1f;
        a[1] = -r[1] / r[0];
        e[1] = r[0] + r[1] * a[1];
        for (int k = 1; k < lpcOrder; ++k)
        {
            float lambda = 0f;
            for (int j = 0; j < k + 1; ++j)
            {
                lambda -= a[j] * r[k + 1 - j];
            }
            lambda /= e[k];

            var U = new NativeArray<float>(k + 2, Allocator.Temp);
            var V = new NativeArray<float>(k + 2, Allocator.Temp);

            U[0] = 1f;
            V[0] = 0f;
            U[k + 1] = 0f;
            V[k + 1] = 1f;
            for (int i = 1; i < k + 1; ++i)
            {
                U[i] = V[k + 1 - i] = a[i];
            }

            for (int i = 0; i < k + 2; ++i)
            {
                a[i] = U[i] + lambda * V[i];
            }

            e[k + 1] = e[k] * (1f - lambda * lambda);

            U.Dispose();
            V.Dispose();
        }

        r.Dispose();

        // calculate frequency characteristics
        int Nf = (int)((float)H.Length * sampleRate / maxFreq);
        var Htmp = new NativeArray<float>(H.Length, Allocator.Temp);
        Algorithm.ZeroClear(ref Htmp);
        float numerator = math.sqrt(math.abs(e[e.Length - 1]));
        for (int n = 0; n < H.Length; ++n)
        {
            float nr = 0f, ni = 0f, dr = 0f, di = 0f;
            for (int i = 0; i < lpcOrder + 1; ++i)
            {
                float theta = -2f * math.PI * i * n / Nf;
                float re = math.cos(theta);
                float im = math.sin(theta);
                nr += e[lpcOrder - i] * re;
                ni += e[lpcOrder - i] * im;
                dr += a[lpcOrder - i] * re;
                di += a[lpcOrder - i] * im;
            }
            float denominator = math.sqrt(dr * dr + di * di);
            if (denominator > math.EPSILON)
            {
                Htmp[n] = numerator / denominator;
            }
        }

        a.Dispose();
        e.Dispose();

        float filter = 1f - math.clamp(filterH, 0f, 1f);
        for (int i = 0; i < H.Length; ++i)
        {
            H[i] += (Htmp[i] - H[i]) * filter;
        }

        Htmp.Dispose();

        dH[0] = H[1] - H[0];
        dH[1] = H[2] - H[1];
        ddH[0] = dH[1] - dH[0];
        for (int i = 1; i < dH.Length; ++i)
        {
            dH[i] = H[i] - H[i - 1];
            ddH[i] = dH[i] - dH[i - 1];
        }

        float deltaFreq = (float)maxFreq / H.Length;

        // get first and second formants by peak
        {
            var res = new Result();
            res.volume = volume;

            for (int i = 1; i < H.Length - 1; ++i)
            {
                var freq = deltaFreq * i;
                if (freq < 100) continue;

                if (H[i] > H[i - 1] && 
                    H[i] > H[i + 1] && 
                    math.log10(H[i]) > minLog10H)
                {
                    if (res.f1 == 0f)
                    {
                        res.f1 = freq;
                    }
                    else if (res.f2 == 0f)
                    {
                        if (freq - res.f1 < 50) continue;
                        res.f2 = freq;
                    }
                    else
                    {
                        if (freq - res.f2 < 50) continue;
                        res.f3 = freq;
                        break;
                    }
                }
            }

            result[0] = res;
        }

        // get formants by the second derivative of H
        {
            var res = new Result();
            res.volume = volume;

            for (int i = 1; i < ddH.Length - 1; ++i)
            {
                var freq = deltaFreq * (i - 1);
                if (freq < 100) continue;

                if (ddH[i] < ddH[i - 1] && 
                    ddH[i] < ddH[i + 1] && 
                    math.log10(ddH[i]) < -2f &&
                    math.log10(H[i]) > minLog10H)
                {
                    if (res.f1 == 0f)
                    {
                        res.f1 = freq;
                    }
                    else if (res.f2 == 0f)
                    {
                        if (freq - res.f1 < 50) continue;
                        res.f2 = freq;
                    }
                    else
                    {
                        if (freq - res.f2 < 50) continue;
                        res.f3 = freq;
                        break;
                    }
                }
            }

            result[1] = res;
        }
    }
}

}
