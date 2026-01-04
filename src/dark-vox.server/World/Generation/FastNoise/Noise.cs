// MIT License
// 
// Copyright (c) 2025 Miles Oetzel
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.


// This library is written to be compatible with both Unity and CoreCLR.
// In CoreCLR, vectorization is achieved using the System.Numerics.Vector<T> API.
// In Unity, vectorization is achieved using Burst auto-vectorization.
// So in CoreCLR, Int and Float represent Vector<int> and Vector<float>,
// while in Unity Int and Float simply represent int and float, since Burst will automatically preform vectorization.
// The benefit of this approach is it involves very little platform-specific vectorized code,
// so there is no need for multiple versions based on Fma/Avx2 support or ARM.


using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Float = System.Numerics.Vector<float>;
using Int = System.Numerics.Vector<int>;
using Util = System.Numerics.Vector;

namespace NoiseDotNet;

/// <summary>
/// SIMD-accelerated implementations of coherent noise functions.
/// </summary>
public static class Noise
{
    // In CoreCLR, we can simply call directly into the vectorized code,
    // However in Unity, we need to run the vectorized code in a Burst compiled job.
    // For this reason, there are two versions of each noise function in Unity:
    // A version that takes in pointers  which is called by the Burst job (Spans have limited support in Burst),
    // and a version that takes in Spans, which creates and runs the Burst job.

    /// <summary>
    /// <para> Vectorized 2D gradient noise function. Underlying algorithm is Quadratic noise, a modified version of Perlin noise. </para>
    /// <para> Output range is approximately -1.0 to 1.0, but in rare cases may exceed this range. </para>
    /// </summary>
    /// <param name="xCoords">The x-coordinates of the sample points.</param>
    /// <param name="yCoords">The y-coordinates of the sample points.</param>
    /// <param name="output">The output buffer evaluations are written into.</param>
    /// <param name="xFreq">x-coordinates are multiplied by this number before being used.</param>
    /// <param name="yFreq">y-coordinates are multiplied by this number before being used.</param>
    /// <param name="amplitude">The output of the noise function is multiplied by this number before being written into the output buffer.</param>
    /// <param name="seed">The seed for the noise function.</param>
    public static unsafe void GradientNoise2D(Span<float> xCoords, Span<float> yCoords, Span<float> output, float xFreq, float yFreq, float amplitude, int seed)
    {
        var seedVec = Util.Create(seed);
        Float xfVec = Util.Create(xFreq), yfVec = Util.Create(yFreq), ampVec = Util.Create(amplitude);
        var length = output.Length;
        if (length < Float.Count)
        {
            // if the buffer doesn't have enough elements to fit into a vector,
            // we can't use the load and store instructions, so we have to build the vector element by element instead.
            Float xVec = default, yVec = default;
            for (var i = 0; i < length; ++i)
            {
                xVec = xVec.WithElement(i, xCoords[i]);
                yVec = yVec.WithElement(i, yCoords[i]);
            }
            var result = GradientNoise2DVector(xVec * xfVec, yVec * yfVec, seedVec) * ampVec;
            for (var i = 0; i < length; ++i)
            {
                output[i] = result.GetElement(i);
            }
        }
        else
        {
            for (var i = 0; i < length - Float.Count; i += Float.Count)
            {
                var xVec = Util.LoadUnsafe(ref xCoords[i]);
                var yVec = Util.LoadUnsafe(ref yCoords[i]);
                var result = GradientNoise2DVector(xVec * xfVec, yVec * yfVec, seedVec) * ampVec;
                result.StoreUnsafe(ref output[i]);
            }
            {
                var i = length - Float.Count;
                var xVec = Util.LoadUnsafe(ref xCoords[i]);
                var yVec = Util.LoadUnsafe(ref yCoords[i]);
                var result = GradientNoise2DVector(xVec * xfVec, yVec * yfVec, seedVec) * ampVec;
                result.StoreUnsafe(ref output[i]);
            }
        }
    }

    /// <summary>
    /// <para> Vectorized 3D gradient noise function. Underlying algorithm is Quadratic noise, a modified version of Perlin noise. </para>
    /// <para> Output range is approximately -1.0 to 1.0, but in rare cases may exceed this range. </para>
    /// </summary>
    /// <param name="xCoords">The x-coordinates of the sample points.</param>
    /// <param name="yCoords">The y-coordinates of the sample points.</param>
    /// <param name="zCoords">The z-coordinates of the sample points.</param>
    /// <param name="output">The output buffer evaluations are written into.</param>
    /// <param name="xFreq">x-coordinates are multiplied by this number before being used.</param>
    /// <param name="yFreq">y-coordinates are multiplied by this number before being used.</param>
    /// <param name="zFreq">z-coordinates are multiplied by this number before being used.</param>
    /// <param name="amplitude">The output of the noise function is multiplied by this number before being written into the output buffer.</param>
    /// <param name="seed">The seed for the noise function.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe void GradientNoise3D(Span<float> xCoords, Span<float> yCoords, Span<float> zCoords, Span<float> output, float xFreq, float yFreq, float zFreq, float amplitude, int seed)
    {
        var seedVec = Util.Create(seed);
        Float xfVec = Util.Create(xFreq), yfVec = Util.Create(yFreq), zfVec = Util.Create(zFreq), ampVec = Util.Create(amplitude);
        var length = output.Length;
        if (length < Float.Count)
        {
            // if the buffer doesn't have enough elements to fit into a vector,
            // we can't use the load and store instructions, so we have to build the vector element by element instead.
            Float xVec = default, yVec = default, zVec = default;
            for (var i = 0; i < length; ++i)
            {
                xVec = xVec.WithElement(i, xCoords[i]);
                yVec = yVec.WithElement(i, yCoords[i]);
                zVec = zVec.WithElement(i, zCoords[i]);
            }
            var result = GradientNoise3DVector(xVec * xfVec, yVec * yfVec, zVec * zfVec, seedVec) * ampVec;
            for (var i = 0; i < length; ++i)
            {
                output[i] = result.GetElement(i);
            }
        }
        else
        {
            for (var i = 0; i < length - Float.Count; i += Float.Count)
            {
                var xVec = Util.LoadUnsafe(ref xCoords[i]) * xfVec;
                var yVec = Util.LoadUnsafe(ref yCoords[i]) * yfVec;
                var zVec = Util.LoadUnsafe(ref zCoords[i]) * zfVec;
                var result = GradientNoise3DVector(xVec, yVec, zVec, seedVec) * ampVec;
                result.StoreUnsafe(ref output[i]);
            }
            {
                var i = length - Float.Count;
                var xVec = Util.LoadUnsafe(ref xCoords[i]) * xfVec;
                var yVec = Util.LoadUnsafe(ref yCoords[i]) * yfVec;
                var zVec = Util.LoadUnsafe(ref zCoords[i]) * zfVec;
                var result = GradientNoise3DVector(xVec, yVec, zVec, seedVec) * ampVec;
                result.StoreUnsafe(ref output[i]);
            }
        }
    }


    /// <summary>
    /// <para> Vectorized 2D cellular noise function.</para>
    /// <para> Outputs the distance to the center of the Voronoi cell and the distance to the edge of the Voronoi cell in two separate buffers. </para>
    /// </summary>
    /// <param name="xCoords">The x-coordinates of the sample points.</param>
    /// <param name="yCoords">The y-coordinates of the sample points.</param>
    /// <param name="centerDistOutput">The output buffer cell center distances are written into.</param>
    /// <param name="edgeDistOutput">The output buffer cell edge distances are written into.</param>
    /// <param name="xFreq">x-coordinates are multiplied by this number before being used.</param>
    /// <param name="yFreq">y-coordinates are multiplied by this number before being used.</param>
    /// <param name="centerDistAmplitude">Center distance outputs are multiplied by this number before being written into the output buffer.</param>
    /// <param name="edgeDistAmplitude">Edge distance outputs are multiplied by this number before being written into the output buffer.</param>
    /// <param name="seed">The seed for the noise function.</param>
    public static unsafe void CellularNoise2D(Span<float> xCoords, Span<float> yCoords, Span<float> centerDistOutput, Span<float> edgeDistOutput, float xFreq, float yFreq, float centerDistAmplitude, float edgeDistAmplitude, int seed)
    {
        var seedVec = Util.Create(seed);
        var xfVec = Util.Create(xFreq);
        var yfVec = Util.Create(yFreq);
        var centerAmpVec = Util.Create(centerDistAmplitude);
        var edgeAmpVec = Util.Create(edgeDistAmplitude);
        var length = centerDistOutput.Length;
        if (length < Float.Count)
        {
            // if the buffer doesn't have enough elements to fit into a vector,
            // we can't use the load and store instructions, so we have to build the vector element by element instead.
            var xVec = default(Float);
            var yVec = default(Float);
            for (var i = 0; i < length; ++i)
            {
                xVec = xVec.WithElement(i, xCoords[i]);
                yVec = yVec.WithElement(i, yCoords[i]);
            }
            (var centerDist, var edgeDist) = CellularNoise2DVector(xVec * xfVec, yVec * yfVec, seedVec);
            centerDist *= centerAmpVec;
            edgeDist *= edgeAmpVec;
            for (var i = 0; i < length; ++i)
            {
                centerDistOutput[i] = centerDist.GetElement(i);
                edgeDistOutput[i] = edgeDist.GetElement(i);
            }
        }
        else
        {
            for (var i = 0; i < length - Float.Count; i += Float.Count)
            {
                var xVec = Util.LoadUnsafe(ref xCoords[i]);
                var yVec = Util.LoadUnsafe(ref yCoords[i]);
                (var centerDist, var edgeDist) = CellularNoise2DVector(xVec * xfVec, yVec * yfVec, seedVec);
                centerDist *= centerAmpVec;
                edgeDist *= edgeAmpVec;
                centerDist.StoreUnsafe(ref centerDistOutput[i]);
                edgeDist.StoreUnsafe(ref edgeDistOutput[i]);
            }
            {
                var i = length - Float.Count;
                var xVec = Util.LoadUnsafe(ref xCoords[i]);
                var yVec = Util.LoadUnsafe(ref yCoords[i]);
                (var centerDist, var edgeDist) = CellularNoise2DVector(xVec * xfVec, yVec * yfVec, seedVec);
                centerDist *= centerAmpVec;
                edgeDist *= edgeAmpVec;
                centerDist.StoreUnsafe(ref centerDistOutput[i]);
                edgeDist.StoreUnsafe(ref edgeDistOutput[i]);
            }
        }
    }


    /// <summary>
    /// <para> Vectorized 3D cellular noise function.</para>
    /// <para> Outputs the distance to the center of the Voronoi cell and the distance to the edge of the Voronoi cell in two separate buffers. </para>
    /// </summary>
    /// <param name="xCoords">The x-coordinates of the sample points.</param>
    /// <param name="yCoords">The y-coordinates of the sample points.</param>
    /// <param name="zCoords">The z-coordinates of the sample points.</param>
    /// <param name="centerDistOutput">The output buffer cell center distances are written into.</param>
    /// <param name="edgeDistOutput">The output buffer cell edge distances are written into.</param>
    /// <param name="xFreq">x-coordinates are multiplied by this number before being used.</param>
    /// <param name="yFreq">y-coordinates are multiplied by this number before being used.</param>
    /// <param name="zFreq">z-coordinates are multiplied by this number before being used.</param>
    /// <param name="centerDistAmplitude">Center distance outputs are multiplied by this number before being written into the output buffer.</param>
    /// <param name="edgeDistAmplitude">Edge distance outputs are multiplied by this number before being written into the output buffer.</param>
    /// <param name="seed">The seed for the noise function.</param>
    public static unsafe void CellularNoise3D(ReadOnlySpan<float> xCoords, ReadOnlySpan<float> yCoords, ReadOnlySpan<float> zCoords, Span<float> centerDistOutput, Span<float> edgeDistOutput, float xFreq, float yFreq, float zFreq, float centerDistAmplitude, float edgeDistAmplitude, int seed)
    {
        var seedVec = Util.Create(seed);
        var xfVec = Util.Create(xFreq);
        var yfVec = Util.Create(yFreq);
        var zfVec = Util.Create(zFreq);
        var centerAmpVec = Util.Create(centerDistAmplitude);
        var edgeAmpVec = Util.Create(edgeDistAmplitude);
        var length = centerDistOutput.Length;
        if (length < Float.Count)
        {
            // if the buffer doesn't have enough elements to fit into a vector,
            // we can't use the load and store instructions, so we have to build the vector element by element instead.
            var xVec = default(Float);
            var yVec = default(Float);
            var zVec = default(Float);
            for (var i = 0; i < length; ++i)
            {
                xVec = xVec.WithElement(i, xCoords[i]);
                yVec = yVec.WithElement(i, yCoords[i]);
                zVec = zVec.WithElement(i, zCoords[i]);
            }
            (var centerDist, var edgeDist) = CellularNoise3DVector(xVec * xfVec, yVec * yfVec, zVec * zfVec, seedVec);
            centerDist *= centerAmpVec;
            edgeDist *= edgeAmpVec;
            for (var i = 0; i < length; ++i)
            {
                centerDistOutput[i] = centerDist.GetElement(i);
                edgeDistOutput[i] = edgeDist.GetElement(i);
            }
        }
        else
        {
            for (var i = 0; i < length - Float.Count; i += Float.Count)
            {
                var xVec = Util.LoadUnsafe(in xCoords[i]);
                var yVec = Util.LoadUnsafe(in yCoords[i]);
                var zVec = Util.LoadUnsafe(in zCoords[i]);

                (var centerDist, var edgeDist) = CellularNoise3DVector(xVec * xfVec, yVec * yfVec, zVec * zfVec, seedVec);
                centerDist *= centerAmpVec;
                edgeDist *= edgeAmpVec;
                centerDist.StoreUnsafe(ref centerDistOutput[i]);
                edgeDist.StoreUnsafe(ref edgeDistOutput[i]);
            }
            {
                var i = length - Float.Count;

                var xVec = Util.LoadUnsafe(in xCoords[i]);
                var yVec = Util.LoadUnsafe(in yCoords[i]);
                var zVec = Util.LoadUnsafe(in zCoords[i]);

                (var centerDist, var edgeDist) = CellularNoise3DVector(xVec * xfVec, yVec * yfVec, zVec * zfVec, seedVec);
                centerDist *= centerAmpVec;
                edgeDist *= edgeAmpVec;
                centerDist.StoreUnsafe(ref centerDistOutput[i]);
                edgeDist.StoreUnsafe(ref edgeDistOutput[i]);
            }
        }
    }


    // All noise function implementations must be inlined so they can be auto-vectorized by Burst. 
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Float GradientNoise2DVector(Float x, Float y, Int seed)
    {
        var xFloor = Util.Floor(x);
        var yFloor = Util.Floor(y);
        // In CoreCLR, Vector.ConvertToInt32() adds additional instructions to make sure that 
        // that the conversion behaves is consistent across platforms when the float is outside the range of an int.
        // Since in practical use of this function it will never be out of bounds, we can use ConvertToInt32Native, which avoids this overhead.
        var ix = Util.ConvertToInt32Native(xFloor);
        var iy = Util.ConvertToInt32Native(yFloor);
        var fx = x - xFloor;
        var fy = y - yFloor;

        // These constants were chosen using an optimizer to avoid visually obvious non-random effects in the hash result.
        var ConstX = Util.Create(180601904);
        var ConstY = Util.Create(174181987);
        var ConstXOR = Util.Create(203663684);

        var llHash = ix * ConstX + iy * ConstY + seed;
        var lrHash = llHash + ConstX;
        var ulHash = llHash + ConstY;
        var urHash = llHash + ConstX + ConstY;

        llHash *= llHash ^ ConstXOR;
        lrHash *= lrHash ^ ConstXOR;
        ulHash *= ulHash ^ ConstXOR;
        urHash *= urHash ^ ConstXOR;

        var GradAndMask = Util.Create(unchecked((int)0b11000000001100000000100000000111));
        var GradOrMask = Util.Create(unchecked((int)0b00011111100001111111001111101000));

        llHash = (llHash & GradAndMask) | GradOrMask;
        lrHash = (lrHash & GradAndMask) | GradOrMask;
        ulHash = (ulHash & GradAndMask) | GradOrMask;
        urHash = (urHash & GradAndMask) | GradOrMask;

        var fxm1 = fx - Util.Create(1f);
        var fym1 = fy - Util.Create(1f);

        const int GradShift1 = 1, GradShift2 = 20;
        const int GradShift3 = 11;

        /// CoreCLR won't generate the vblendvps instruction here without explicitly calling it using the System.Runtime.Intrinsics API
        var llGrad = Util.MultiplyAddEstimate(
            BlendVPS(llHash, fx, fy), Util.AsVectorSingle(llHash << GradShift1),
            BlendVPS(llHash, fy, fx) * Util.AsVectorSingle(llHash << GradShift2));
        var lrGrad = Util.MultiplyAddEstimate(
            BlendVPS(lrHash, fxm1, fy), Util.AsVectorSingle(lrHash << GradShift1),
            BlendVPS(lrHash, fy, fxm1) * Util.AsVectorSingle(lrHash << GradShift2));
        var ulGrad = Util.MultiplyAddEstimate(
            BlendVPS(ulHash, fx, fym1), Util.AsVectorSingle(ulHash << GradShift1),
            BlendVPS(ulHash, fym1, fx) * Util.AsVectorSingle(ulHash << GradShift2));
        var urGrad = Util.MultiplyAddEstimate(
            BlendVPS(urHash, fxm1, fym1), Util.AsVectorSingle(urHash << GradShift1),
            BlendVPS(urHash, fym1, fxm1) * Util.AsVectorSingle(urHash << GradShift2));

        llGrad = Util.MultiplyAddEstimate(llGrad, llGrad * Util.AsVectorSingle(llHash << GradShift3), llGrad);
        lrGrad = Util.MultiplyAddEstimate(lrGrad, lrGrad * Util.AsVectorSingle(lrHash << GradShift3), lrGrad);
        ulGrad = Util.MultiplyAddEstimate(ulGrad, ulGrad * Util.AsVectorSingle(ulHash << GradShift3), ulGrad);
        urGrad = Util.MultiplyAddEstimate(urGrad, urGrad * Util.AsVectorSingle(urHash << GradShift3), urGrad);

        // smootherstep interpolation
        var sx = fx * fx * fx * Util.MultiplyAddEstimate(Util.MultiplyAddEstimate(fx, Util.Create(6f), Util.Create(-15f)), fx, Util.Create(10f));
        var sy = fy * fy * fy * Util.MultiplyAddEstimate(Util.MultiplyAddEstimate(fy, Util.Create(6f), Util.Create(-15f)), fy, Util.Create(10f));

        var lLerp = Util.MultiplyAddEstimate(lrGrad - llGrad, sx, llGrad);
        var uLerp = Util.MultiplyAddEstimate(urGrad - ulGrad, sx, ulGrad);
        var result = Util.MultiplyAddEstimate(uLerp - lLerp, sy, lLerp);
        return result;
    }

    /// <summary>
    /// Implementation of the x86 vblendvps instruction with fallbacks for targets that don't support that instruction. 
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Float BlendVPS(Int selector, Float a, Float b)
    {
        return Avx.IsSupported
            ? Avx.BlendVariable(a.AsVector256(), b.AsVector256(), selector.AsVector256().AsSingle()).AsVector()
            : Sse41.IsSupported
                ? Sse41.BlendVariable(a.AsVector128(), b.AsVector128(), selector.AsVector128().AsSingle()).AsVector()
                : Util.ConditionalSelect(Util.LessThan(selector, Util.Create(0)), a, b);

    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Float GradientNoise3DVector(Float x, Float y, Float z, Int seed)
    {
        var xFloor = Util.Floor(x);
        var yFloor = Util.Floor(y);
        var zFloor = Util.Floor(z);
        var ix = Util.ConvertToInt32Native(xFloor);
        var iy = Util.ConvertToInt32Native(yFloor);
        var iz = Util.ConvertToInt32Native(zFloor);
        var fx = x - xFloor;
        var fy = y - yFloor;
        var fz = z - zFloor;

        var ConstX = Util.Create(180601904);
        var ConstY = Util.Create(174181987);
        var ConstZ = Util.Create(738599801);
        var ConstXOR = Util.Create(203663684);

        // X: (lower/upper) Y: (left/right) Z: (back/front). 
        var llbHash = ix * ConstX + iy * ConstY + iz * ConstZ + seed;
        var lrbHash = llbHash + ConstX;
        var ulbHash = llbHash + ConstY;
        var urbHash = llbHash + ConstX + ConstY;
        var llfHash = llbHash + ConstZ;
        var lrfHash = llbHash + ConstZ + ConstX;
        var ulfHash = llbHash + ConstZ + ConstY;
        var urfHash = llbHash + ConstZ + ConstX + ConstY;

        // This extra bit shift compared to the 2D version is needed because without it,
        // low-significance bits don't have high quality randomness.
        // In 2D, it is possible to avoid using these bits when generating a gradient vector,
        // but in 3D, it is more difficult to achieve. 
        llbHash *= (llbHash ^ ConstXOR) >> 16;
        lrbHash *= (lrbHash ^ ConstXOR) >> 16;
        ulbHash *= (ulbHash ^ ConstXOR) >> 16;
        urbHash *= (urbHash ^ ConstXOR) >> 16;
        llfHash *= (llfHash ^ ConstXOR) >> 16;
        lrfHash *= (lrfHash ^ ConstXOR) >> 16;
        ulfHash *= (ulfHash ^ ConstXOR) >> 16;
        urfHash *= (urfHash ^ ConstXOR) >> 16;

        var GradAndMask = Util.Create(unchecked((int)0b11000000001100000000100000000111));
        var GradOrMask = Util.Create(unchecked((int)0b00011111100001111110001111110000));

        llbHash = (llbHash & GradAndMask) | GradOrMask;
        lrbHash = (lrbHash & GradAndMask) | GradOrMask;
        ulbHash = (ulbHash & GradAndMask) | GradOrMask;
        urbHash = (urbHash & GradAndMask) | GradOrMask;
        llfHash = (llfHash & GradAndMask) | GradOrMask;
        lrfHash = (lrfHash & GradAndMask) | GradOrMask;
        ulfHash = (ulfHash & GradAndMask) | GradOrMask;
        urfHash = (urfHash & GradAndMask) | GradOrMask;

        const int GradShift1 = 1, GradShift2 = 20, GradShift3 = 11;
        var negOne = Util.Create(-1f);

        var sx = fx * fx * fx * Util.MultiplyAddEstimate(Util.MultiplyAddEstimate(fx, Util.Create(6f), Util.Create(-15f)), fx, Util.Create(10f));
        var sz = fz * fz * fz * Util.MultiplyAddEstimate(Util.MultiplyAddEstimate(fz, Util.Create(6f), Util.Create(-15f)), fz, Util.Create(10f));
        var sy = fy * fy * fy * Util.MultiplyAddEstimate(Util.MultiplyAddEstimate(fy, Util.Create(6f), Util.Create(-15f)), fy, Util.Create(10f));

        var llbGrad = Util.MultiplyAddEstimate(
            fx, Util.AsVectorSingle(llbHash << GradShift1), Util.MultiplyAddEstimate(
            fy, Util.AsVectorSingle(llbHash << GradShift2),
            fz * Util.AsVectorSingle(llbHash << GradShift3)));
        var lrbGrad = Util.MultiplyAddEstimate(
            fx + negOne, Util.AsVectorSingle(lrbHash << GradShift1), Util.MultiplyAddEstimate(
            fy, Util.AsVectorSingle(lrbHash << GradShift2),
            fz * Util.AsVectorSingle(lrbHash << GradShift3)));
        llbGrad = Util.MultiplyAddEstimate(llbGrad, llbGrad * Util.AsVectorSingle(Util.AsVectorInt32(negOne) ^ (llbHash & Util.Create(1 << 31))), llbGrad);
        lrbGrad = Util.MultiplyAddEstimate(lrbGrad, lrbGrad * Util.AsVectorSingle(Util.AsVectorInt32(negOne) ^ (lrbHash & Util.Create(1 << 31))), lrbGrad);
        var lbLerp = Util.MultiplyAddEstimate(lrbGrad - llbGrad, sx, llbGrad);

        var ulbGrad = Util.MultiplyAddEstimate(
            fx, Util.AsVectorSingle(ulbHash << GradShift1), Util.MultiplyAddEstimate(
            fy + negOne, Util.AsVectorSingle(ulbHash << GradShift2),
            fz * Util.AsVectorSingle(ulbHash << GradShift3)));
        var urbGrad = Util.MultiplyAddEstimate(
            fx + negOne, Util.AsVectorSingle(urbHash << GradShift1), Util.MultiplyAddEstimate(
            fy + negOne, Util.AsVectorSingle(urbHash << GradShift2),
            fz * Util.AsVectorSingle(urbHash << GradShift3)));
        ulbGrad = Util.MultiplyAddEstimate(ulbGrad, ulbGrad * Util.AsVectorSingle(Util.AsVectorInt32(negOne) ^ (ulbHash & Util.Create(1 << 31))), ulbGrad);
        urbGrad = Util.MultiplyAddEstimate(urbGrad, urbGrad * Util.AsVectorSingle(Util.AsVectorInt32(negOne) ^ (urbHash & Util.Create(1 << 31))), urbGrad);
        var ubLerp = Util.MultiplyAddEstimate(urbGrad - ulbGrad, sx, ulbGrad);

        fz += negOne;

        var llfGrad = Util.MultiplyAddEstimate(
            fx, Util.AsVectorSingle(llfHash << GradShift1), Util.MultiplyAddEstimate(
            fy, Util.AsVectorSingle(llfHash << GradShift2),
            fz * Util.AsVectorSingle(llfHash << GradShift3)));
        var lrfGrad = Util.MultiplyAddEstimate(
            fx + negOne, Util.AsVectorSingle(lrfHash << GradShift1), Util.MultiplyAddEstimate(
            fy, Util.AsVectorSingle(lrfHash << GradShift2),
            fz * Util.AsVectorSingle(lrfHash << GradShift3)));
        llfGrad = Util.MultiplyAddEstimate(llfGrad, llfGrad * Util.AsVectorSingle(Util.AsVectorInt32(negOne) ^ (llfHash & Util.Create(1 << 31))), llfGrad);
        lrfGrad = Util.MultiplyAddEstimate(lrfGrad, lrfGrad * Util.AsVectorSingle(Util.AsVectorInt32(negOne) ^ (lrfHash & Util.Create(1 << 31))), lrfGrad);
        var lfLerp = Util.MultiplyAddEstimate(lrfGrad - llfGrad, sx, llfGrad);

        var ulfGrad = Util.MultiplyAddEstimate(
            fx, Util.AsVectorSingle(ulfHash << GradShift1), Util.MultiplyAddEstimate(
            fy + negOne, Util.AsVectorSingle(ulfHash << GradShift2),
            fz * Util.AsVectorSingle(ulfHash << GradShift3)));
        var urfGrad = Util.MultiplyAddEstimate(
            fx + negOne, Util.AsVectorSingle(urfHash << GradShift1), Util.MultiplyAddEstimate(
            fy + negOne, Util.AsVectorSingle(urfHash << GradShift2),
            fz * Util.AsVectorSingle(urfHash << GradShift3)));
        ulfGrad = Util.MultiplyAddEstimate(ulfGrad, ulfGrad * Util.AsVectorSingle(Util.AsVectorInt32(negOne) ^ (ulfHash & Util.Create(1 << 31))), ulfGrad);
        urfGrad = Util.MultiplyAddEstimate(urfGrad, urfGrad * Util.AsVectorSingle(Util.AsVectorInt32(negOne) ^ (urfHash & Util.Create(1 << 31))), urfGrad);
        var ufLerp = Util.MultiplyAddEstimate(urfGrad - ulfGrad, sx, ulfGrad);

        var bLerp = Util.MultiplyAddEstimate(ubLerp - lbLerp, sy, lbLerp);
        var fLerp = Util.MultiplyAddEstimate(ufLerp - lfLerp, sy, lfLerp);

        var result = Util.MultiplyAddEstimate(fLerp - bLerp, sz, bLerp);
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Float centerDist, Float edgeDist) CellularNoise2DVector(Float x, Float y, Int seed)
    {
        var xFloor = Util.Floor(x);
        var yFloor = Util.Floor(y);
        var ix = Util.ConvertToInt32Native(xFloor);
        var iy = Util.ConvertToInt32Native(yFloor);
        var fx = x - xFloor;
        var fy = y - yFloor;

        var ConstX = Util.Create(180601904);
        var ConstY = Util.Create(174181987);

        var centerHash = ix * ConstX + iy * ConstY + seed;

        var d1 = Util.Create(2f);
        var d2 = Util.Create(2f);
        var one = Util.Create(1f);
        var two = Util.Create(2f);
        SingleCell2D(centerHash + ConstY, fx + one, fy, ref d1, ref d2);
        SingleCell2D(centerHash + ConstY - ConstX, fx + two, fy, ref d1, ref d2);
        SingleCell2D(centerHash + ConstY + ConstX, fx, fy, ref d1, ref d2);
        fy += one;
        SingleCell2D(centerHash, fx + one, fy, ref d1, ref d2);
        SingleCell2D(centerHash - ConstX, fx + two, fy, ref d1, ref d2);
        SingleCell2D(centerHash + ConstX, fx, fy, ref d1, ref d2);
        fy += one;
        SingleCell2D(centerHash - ConstY, fx + one, fy, ref d1, ref d2);
        SingleCell2D(centerHash - ConstY - ConstX, fx + two, fy, ref d1, ref d2);
        SingleCell2D(centerHash - ConstY + ConstX, fx, fy, ref d1, ref d2);

        d1 = Util.SquareRoot(d1);
        d2 = Util.SquareRoot(d2);

        var edgeDist = d2 - d1;
        return (d1, edgeDist);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SingleCell2D(Int hash, Float fx, Float fy, ref Float d1, ref Float d2)
    {
        var ConstXOR = Util.Create(203663684);
        hash *= hash ^ ConstXOR;
        var AndMask = Util.Create(unchecked((int)0b00000000011100000000011111111111));
        var OrMask = Util.Create(unchecked((int)0b00111111100000111111100000000000));
        hash = (hash & AndMask) | OrMask;
        var dx = fx - Util.AsVectorSingle(hash);
        var dy = fy - Util.AsVectorSingle(hash << 12);
        var d = Util.MultiplyAddEstimate(dx, dx, dy * dy);
        var smallest = Util.LessThan(d, d1);
        var secondSmallest = Util.LessThan(d, d2);
        d2 = Util.ConditionalSelect(smallest, d1, Util.ConditionalSelect(secondSmallest, d, d2));
        d1 = Util.ConditionalSelect(smallest, d, d1);

    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Float centerDist, Float edgeDist) CellularNoise3DVector(Float x, Float y, Float z, Int seed)
    {
        var xFloor = Util.Floor(x);
        var yFloor = Util.Floor(y);
        var zFloor = Util.Floor(z);
        var ix = Util.ConvertToInt32Native(xFloor);
        var iy = Util.ConvertToInt32Native(yFloor);
        var iz = Util.ConvertToInt32Native(zFloor);
        var fx = x - xFloor;
        var fy = y - yFloor;
        var fz = z - zFloor;

        var ConstX = Util.Create(180601904);
        var ConstY = Util.Create(174181987);
        var ConstZ = Util.Create(598742741);

        var centerHash = ix * ConstX + iy * ConstY + iz * ConstZ + seed;

        var d1 = Util.Create(2f);
        var d2 = Util.Create(2f);
        var one = Util.Create(1f);
        var two = Util.Create(2f);
        SingleCell3D(centerHash + ConstY, fx + one, fy, fz, ref d1, ref d2);
        SingleCell3D(centerHash + ConstY - ConstX, fx + two, fy, fz, ref d1, ref d2);
        SingleCell3D(centerHash + ConstY + ConstX, fx, fy, fz, ref d1, ref d2);
        fy += one;
        SingleCell3D(centerHash, fx + one, fy, fz, ref d1, ref d2);
        SingleCell3D(centerHash - ConstX, fx + two, fy, fz, ref d1, ref d2);
        SingleCell3D(centerHash + ConstX, fx, fy, fz, ref d1, ref d2);
        fy += one;
        SingleCell3D(centerHash - ConstY, fx + one, fy, fz, ref d1, ref d2);
        SingleCell3D(centerHash - ConstY - ConstX, fx + two, fy, fz, ref d1, ref d2);
        SingleCell3D(centerHash - ConstY + ConstX, fx, fy, fz, ref d1, ref d2);

        fz += one;
        centerHash -= ConstZ;

        SingleCell3D(centerHash - ConstY, fx + one, fy, fz, ref d1, ref d2);
        SingleCell3D(centerHash - ConstY - ConstX, fx + two, fy, fz, ref d1, ref d2);
        SingleCell3D(centerHash - ConstY + ConstX, fx, fy, fz, ref d1, ref d2);
        fy -= one;
        SingleCell3D(centerHash, fx + one, fy, fz, ref d1, ref d2);
        SingleCell3D(centerHash - ConstX, fx + two, fy, fz, ref d1, ref d2);
        SingleCell3D(centerHash + ConstX, fx, fy, fz, ref d1, ref d2);
        fy -= one;
        SingleCell3D(centerHash + ConstY, fx + one, fy, fz, ref d1, ref d2);
        SingleCell3D(centerHash + ConstY - ConstX, fx + two, fy, fz, ref d1, ref d2);
        SingleCell3D(centerHash + ConstY + ConstX, fx, fy, fz, ref d1, ref d2);

        fz -= Util.Create(2f);
        centerHash += ConstZ * 2;

        SingleCell3D(centerHash + ConstY, fx + one, fy, fz, ref d1, ref d2);
        SingleCell3D(centerHash + ConstY - ConstX, fx + two, fy, fz, ref d1, ref d2);
        SingleCell3D(centerHash + ConstY + ConstX, fx, fy, fz, ref d1, ref d2);
        fy += one;
        SingleCell3D(centerHash, fx + one, fy, fz, ref d1, ref d2);
        SingleCell3D(centerHash - ConstX, fx + two, fy, fz, ref d1, ref d2);
        SingleCell3D(centerHash + ConstX, fx, fy, fz, ref d1, ref d2);
        fy += one;
        SingleCell3D(centerHash - ConstY, fx + one, fy, fz, ref d1, ref d2);
        SingleCell3D(centerHash - ConstY - ConstX, fx + two, fy, fz, ref d1, ref d2);
        SingleCell3D(centerHash - ConstY + ConstX, fx, fy, fz, ref d1, ref d2);

        d1 = Util.SquareRoot(d1);
        d2 = Util.SquareRoot(d2);

        var edgeDist = d2 - d1;
        return (d1, edgeDist);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SingleCell3D(Int hash, Float fx, Float fy, Float fz, ref Float d1, ref Float d2)
    {
        var ConstXOR = Util.Create(203663684);
        hash *= (hash ^ ConstXOR) >> 16;
        var AndMask = Util.Create(unchecked((int)0b11000000000110000000001111111111));
        var OrMask = Util.Create(unchecked((int)0b00001111111000011111110000000000));
        hash = (hash & AndMask) | OrMask;
        var dx = fx - Util.AsVectorSingle(hash << 2);
        var dy = fy - Util.AsVectorSingle(hash << 13);
        var dz = fz - Util.Multiply(Util.ConvertToSingle(hash.As<int, uint>()), 1f / uint.MaxValue);

        var d = Util.MultiplyAddEstimate(dx, dx, Util.MultiplyAddEstimate(dy, dy, dz * dz));
        var smallest = Util.LessThan(d, d1);
        var secondSmallest = Util.LessThan(d, d2);
        d2 = Util.ConditionalSelect(smallest, d1, Util.ConditionalSelect(secondSmallest, d, d2));
        d1 = Util.ConditionalSelect(smallest, d, d1);

    }
}
