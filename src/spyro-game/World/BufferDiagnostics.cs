using OpenRender;
using OpenTK.Graphics.OpenGL4;
using System.Text;

namespace SpyroGame.World;

/// <summary>
/// Diagnostic utilities for sampling and validating GPU buffer contents.
/// Used for debugging shader execution and buffer initialization issues.
/// </summary>
public static class BufferDiagnostics
{
    /// <summary>
    /// Sample the first N elements of a uint buffer and return statistics
    /// </summary>
    public static BufferSample SampleUintBuffer(uint bufferHandle, int sampleSize = 256)
    {
        if (bufferHandle == 0)
            return new BufferSample { IsValid = false, ErrorMessage = "Invalid buffer handle (0)" };

        try
        {
            // Get buffer size
            GL.GetNamedBufferParameter(bufferHandle, BufferParameterName.BufferSize, out int bufferSize);
            if (bufferSize == 0)
                return new BufferSample { IsValid = false, ErrorMessage = "Buffer size is 0" };

            // Clamp sample size to buffer size
            var actualSampleSize = Math.Min(sampleSize, bufferSize / sizeof(uint));
            if (actualSampleSize <= 0)
                return new BufferSample { IsValid = false, ErrorMessage = "Sample size <= 0" };

            // Read data
            var data = new uint[actualSampleSize];
            GL.GetNamedBufferSubData(bufferHandle, IntPtr.Zero, actualSampleSize * sizeof(uint), data);

            // Calculate statistics
            var nonZeroCount = data.Count(x => x != 0);
            var uniqueValues = new HashSet<uint>(data).Count;
            var minValue = data.Min();
            var maxValue = data.Max();
            var avgValue = data.Length > 0 ? data.Average(x => x) : 0.0;

            return new BufferSample
            {
                IsValid = true,
                BufferSize = bufferSize,
                SampleSize = actualSampleSize,
                NonZeroCount = nonZeroCount,
                UniqueValueCount = uniqueValues,
                MinValue = minValue,
                MaxValue = maxValue,
                AverageValue = avgValue,
                FirstFewValues = data.Take(16).ToArray()
            };
        }
        catch (Exception ex)
        {
            return new BufferSample 
            { 
                IsValid = false, 
                ErrorMessage = $"Exception: {ex.Message}" 
            };
        }
    }

    /// <summary>
    /// Sample the first N elements of an int buffer and return statistics
    /// </summary>
    public static BufferSampleInt SampleIntBuffer(uint bufferHandle, int sampleSize = 256)
    {
        if (bufferHandle == 0)
            return new BufferSampleInt { IsValid = false, ErrorMessage = "Invalid buffer handle (0)" };

        try
        {
            GL.GetNamedBufferParameter(bufferHandle, BufferParameterName.BufferSize, out int bufferSize);
            if (bufferSize == 0)
                return new BufferSampleInt { IsValid = false, ErrorMessage = "Buffer size is 0" };

            var actualSampleSize = Math.Min(sampleSize, bufferSize / sizeof(int));
            if (actualSampleSize <= 0)
                return new BufferSampleInt { IsValid = false, ErrorMessage = "Sample size <= 0" };

            var data = new int[actualSampleSize];
            GL.GetNamedBufferSubData(bufferHandle, IntPtr.Zero, actualSampleSize * sizeof(int), data);

            var nonZeroCount = data.Count(x => x != 0);
            var negativeCount = data.Count(x => x < 0);
            var uniqueValues = new HashSet<int>(data).Count;
            var minValue = data.Min();
            var maxValue = data.Max();
            var avgValue = data.Length > 0 ? data.Average(x => (double)x) : 0.0;

            return new BufferSampleInt
            {
                IsValid = true,
                BufferSize = bufferSize,
                SampleSize = actualSampleSize,
                NonZeroCount = nonZeroCount,
                NegativeCount = negativeCount,
                UniqueValueCount = uniqueValues,
                MinValue = minValue,
                MaxValue = maxValue,
                AverageValue = avgValue,
                FirstFewValues = data.Take(16).ToArray()
            };
        }
        catch (Exception ex)
        {
            return new BufferSampleInt 
            { 
                IsValid = false, 
                ErrorMessage = $"Exception: {ex.Message}" 
            };
        }
    }

    /// <summary>
    /// Verify that a buffer was properly cleared (all zeros)
    /// </summary>
    public static bool VerifyBufferCleared(uint bufferHandle, int checkSize = 1024)
    {
        var sample = SampleUintBuffer(bufferHandle, checkSize);
        return sample.IsValid && sample.NonZeroCount == 0;
    }

    /// <summary>
    /// Log a buffer sample to the console with formatted output
    /// </summary>
    public static void LogBufferSample(string bufferName, BufferSample sample)
    {
        if (!sample.IsValid)
        {
            Log.Warn($"[BufferDiag] {bufferName}: INVALID - {sample.ErrorMessage}");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[BufferDiag] {bufferName}:");
        sb.AppendLine($"  Size: {sample.BufferSize} bytes ({sample.BufferSize / 1024}KB)");
        sb.AppendLine($"  Sample: {sample.SampleSize} elements");
        sb.AppendLine($"  Non-zero: {sample.NonZeroCount}/{sample.SampleSize} ({(double)sample.NonZeroCount / sample.SampleSize * 100:F1}%)");
        sb.AppendLine($"  Unique values: {sample.UniqueValueCount}");
        sb.AppendLine($"  Range: {sample.MinValue} - {sample.MaxValue}");
        sb.AppendLine($"  Average: {sample.AverageValue:F2}");
        sb.Append($"  First 16: ");
        sb.Append(string.Join(", ", sample.FirstFewValues.Select(x => x.ToString())));

        Log.Info(sb.ToString());
    }

    /// <summary>
    /// Log an int buffer sample to the console
    /// </summary>
    public static void LogIntBufferSample(string bufferName, BufferSampleInt sample)
    {
        if (!sample.IsValid)
        {
            Log.Warn($"[BufferDiag] {bufferName}: INVALID - {sample.ErrorMessage}");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[BufferDiag] {bufferName}:");
        sb.AppendLine($"  Size: {sample.BufferSize} bytes ({sample.BufferSize / 1024}KB)");
        sb.AppendLine($"  Sample: {sample.SampleSize} elements");
        sb.AppendLine($"  Non-zero: {sample.NonZeroCount}/{sample.SampleSize}");
        sb.AppendLine($"  Negative: {sample.NegativeCount}");
        sb.AppendLine($"  Unique values: {sample.UniqueValueCount}");
        sb.AppendLine($"  Range: {sample.MinValue} - {sample.MaxValue}");
        sb.AppendLine($"  Average: {sample.AverageValue:F2}");
        sb.Append($"  First 16: ");
        sb.Append(string.Join(", ", sample.FirstFewValues.Select(x => x.ToString())));

        Log.Info(sb.ToString());
    }

    /// <summary>
    /// Check if all GL errors are clear
    /// </summary>
    public static bool CheckGlErrors(string context)
    {
        var error = GL.GetError();
        if (error != ErrorCode.NoError)
        {
            Log.Error($"[BufferDiag] GL Error in {context}: {error}");
            return false;
        }
        return true;
    }
}

/// <summary>
/// Result of sampling a uint buffer
/// </summary>
public struct BufferSample
{
    public bool IsValid;
    public string ErrorMessage;
    public int BufferSize;
    public int SampleSize;
    public int NonZeroCount;
    public int UniqueValueCount;
    public uint MinValue;
    public uint MaxValue;
    public double AverageValue;
    public uint[] FirstFewValues;
}

/// <summary>
/// Result of sampling an int buffer
/// </summary>
public struct BufferSampleInt
{
    public bool IsValid;
    public string ErrorMessage;
    public int BufferSize;
    public int SampleSize;
    public int NonZeroCount;
    public int NegativeCount;
    public int UniqueValueCount;
    public int MinValue;
    public int MaxValue;
    public double AverageValue;
    public int[] FirstFewValues;
}
