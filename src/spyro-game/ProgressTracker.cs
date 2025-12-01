using System.Diagnostics;

namespace SpyroGame;

/// <summary>
/// Tracks loading progress with smooth interpolation and sub-operation support.
/// Provides 0-100% progress tracking with detailed status messages.
/// </summary>
public class ProgressTracker
{
    private readonly Stopwatch timer = new();
    private readonly List<ProgressOperation> operations = [];
    
    private float currentProgress = 0f;
    private float targetProgress = 0f;
    private const float ProgressSmoothingSpeed = 8.0f; // Increased from 2.0 for faster catch-up
    
    public float Progress => currentProgress;
    public string CurrentOperation { get; private set; } = "Initializing...";
    public string DetailedStatus { get; private set; } = "";
    public TimeSpan ElapsedTime => timer.Elapsed;
    
    public ProgressTracker()
    {
        timer.Start();
    }
    
    /// <summary>
    /// Add a progress operation with its weight (contribution to total 0-100% progress).
    /// </summary>
    public void AddOperation(string name, float startPercent, float endPercent)
    {
        operations.Add(new ProgressOperation
        {
            Name = name,
            StartPercent = startPercent,
            EndPercent = endPercent
        });
    }
    
    /// <summary>
    /// Update current operation progress (0.0 to 1.0 within the operation's range).
    /// </summary>
    public void UpdateOperation(string operationName, float operationProgress, string detailedStatus = "")
    {
        var operation = operations.FirstOrDefault(o => o.Name == operationName);
        if (operation == null) return;
        
        operationProgress = Math.Clamp(operationProgress, 0f, 1f);
        
        // Calculate target progress based on operation's range
        targetProgress = operation.StartPercent + 
                        (operation.EndPercent - operation.StartPercent) * operationProgress;
        
        CurrentOperation = operationName;
        DetailedStatus = detailedStatus;
    }
    
    /// <summary>
    /// Complete an operation and move to the next.
    /// </summary>
    public void CompleteOperation(string operationName)
    {
        var operation = operations.FirstOrDefault(o => o.Name == operationName);
        if (operation == null) return;
        
        targetProgress = operation.EndPercent;
        CurrentOperation = operationName;
        DetailedStatus = "Complete";
    }
    
    /// <summary>
    /// Update smooth progress interpolation. Call every frame.
    /// </summary>
    public void Update(double deltaTime)
    {
        // Smoothly interpolate current progress towards target
        if (Math.Abs(currentProgress - targetProgress) > 0.1f)
        {
            var step = ProgressSmoothingSpeed * (float)deltaTime;
            currentProgress = currentProgress + (targetProgress - currentProgress) * Math.Min(1f, step);
            
            // Clamp to prevent overshoot
            currentProgress = Math.Clamp(currentProgress, 0f, 100f);
        }
        else
        {
            // Snap to target when very close to prevent slow crawl at end
            currentProgress = targetProgress;
        }
    }
    
    /// <summary>
    /// Check if progress has caught up to target (useful for waiting before transitions).
    /// </summary>
    public bool HasCaughtUp() => Math.Abs(currentProgress - targetProgress) < 0.5f;
    
    /// <summary>
    /// Force progress to specific value (for immediate updates).
    /// </summary>
    public void SetProgress(float percent)
    {
        targetProgress = Math.Clamp(percent, 0f, 100f);
        currentProgress = targetProgress;
    }
    
    private class ProgressOperation
    {
        public string Name { get; set; } = "";
        public float StartPercent { get; set; }
        public float EndPercent { get; set; }
    }
}
