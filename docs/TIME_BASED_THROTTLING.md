# Time-Based Throttling vs Frame-Based Throttling

## The Problem

When throttling expensive operations like frustum culling, you need to ensure they don't run too frequently. However, games often use **fixed timestep accumulators** which can cause `UpdateFrame` to be called multiple times per render frame.

## Why Frame-Based Throttling Fails

### Frame-Based Approach (FRAGILE)
```csharp
private int frameCounter = 0;
const int FrustumCullingUpdateInterval = 10;

frameCounter++;
if (frameCounter >= FrustumCullingUpdateInterval)
{
    frameCounter = 0;
    ExecuteFrustumCulling(); // Only every 10th UpdateFrame call
}
```

**Problem:** If `UpdateFrame` is called 10 times per render frame (due to time accumulator), this executes **every render frame** instead of every 10th!

### Real-World Scenario
```
Frame 1 @ 0.000s:
  UpdateFrame(0.016) called 1 time    → counter=1
  
Frame 2 @ 0.016s:
  UpdateFrame(0.016) called 1 time    → counter=2
  
Frame 3 @ 0.032s (lag spike!):
  UpdateFrame(0.001) called 10 times  → counter=3,4,5,6,7,8,9,10 → TRIGGERS!
  UpdateFrame(0.001)                  → counter=1
  UpdateFrame(0.001)                  → counter=2
  ...
```

**Result:** Culling happens way more frequently than intended when frame time is uneven!

---

## Time-Based Throttling (ROBUST)

### Time-Based Approach
```csharp
private double lastCullingTime = -1.0; // -1 = trigger immediately
const double CullingIntervalSeconds = 0.166; // 166ms

var currentTime = SceneManager.Time;
if ((currentTime - lastCullingTime) >= CullingIntervalSeconds)
{
    lastCullingTime = currentTime;
    ExecuteFrustumCulling(); // Only every 166ms of real time
}
```

**Benefits:**
- ✅ Works with fixed timestep accumulators
- ✅ Works with variable timestep
- ✅ Consistent frequency regardless of update pattern
- ✅ Easy to reason about (milliseconds, not frames)

### Same Scenario with Time-Based
```
Frame 1 @ 0.000s:
  UpdateFrame called 1 time    → 0.000s >= -1.0 → TRIGGERS → lastCullingTime=0.000
  
Frame 2 @ 0.016s:
  UpdateFrame called 1 time    → 0.016s < 0.166 → skips
  
Frame 3 @ 0.032s (lag spike!):
  UpdateFrame called 10 times  → 0.032s < 0.166 → skips all
  
Frame 10 @ 0.166s:
  UpdateFrame called 1 time    → 0.166s >= 0.166 → TRIGGERS → lastCullingTime=0.166
```

**Result:** Culling happens exactly every 166ms regardless of frame pacing!

---

## Implementation Details

### SceneManager.Time
The `SceneManager.Time` property provides elapsed time since app start:

```csharp
public class SceneManager : GameWindow
{
    private readonly Stopwatch sw = new();
    
    public double Time => sw.Elapsed.TotalSeconds;
}
```

**Properties:**
- Monotonically increasing
- High resolution (Stopwatch precision)
- Consistent across all UpdateFrame calls in same render frame

### Initial Trigger
```csharp
private double lastCullingTime = -1.0; // Special value
```

By initializing to `-1.0`, the first check `(currentTime - lastCullingTime) >= CullingIntervalSeconds` will always be true, ensuring immediate execution on scene start.

---

## Comparison Table

| Aspect | Frame-Based | Time-Based |
|--------|-------------|------------|
| Fixed timestep accumulator | ❌ Breaks | ✅ Works |
| Variable framerate | ⚠️ Inconsistent | ✅ Consistent |
| Lag spikes | ❌ Over-triggers | ✅ Stable |
| Easy to understand | ⚠️ "10 frames" ambiguous | ✅ "166ms" clear |
| Performance predictable | ❌ No | ✅ Yes |
| Future-proof | ❌ Fragile | ✅ Robust |

---

## Code Evolution

### v1: Frame-Based (BROKEN)
```csharp
// Called at top of UpdateFrame()
base.UpdateFrame(elapsedSeconds); // Might call UpdateFrame recursively!

frameCounter++;
if (frameCounter >= 10)
{
    frameCounter = 0;
    ExecuteFrustumCulling();
}
```

**Issue:** `base.UpdateFrame()` processes action queue which might trigger more `UpdateFrame` calls.

### v2: Frame-Based Before Base (WORKS BUT FRAGILE)
```csharp
// Called before base.UpdateFrame()
frameCounter++;
if (frameCounter >= 10)
{
    frameCounter = 0;
    ExecuteFrustumCulling();
}

base.UpdateFrame(elapsedSeconds);
```

**Issue:** Breaks with fixed timestep accumulator (10 calls per frame).

### v3: Time-Based (ROBUST)
```csharp
// Time-based check - works anywhere in UpdateFrame
var currentTime = SceneManager.Time;
if ((currentTime - lastCullingTime) >= 0.166)
{
    lastCullingTime = currentTime;
    ExecuteFrustumCulling();
}

base.UpdateFrame(elapsedSeconds); // Order doesn't matter anymore!
```

**Benefits:** Works with any update pattern, any framerate, any engine architecture.

---

## Performance Verification

### Expected Behavior
With `CullingIntervalSeconds = 0.166` (166ms):

**At 60 FPS (16.6ms per frame):**
- Culling triggers every **10 frames**
- **6 warnings per second**
- Spacing: exactly 166ms ± 1 frame (16.6ms)

**At 30 FPS (33.3ms per frame):**
- Culling triggers every **5 frames**
- **6 warnings per second**
- Spacing: exactly 166ms ± 1 frame (33.3ms)

**With Fixed Timestep (multiple updates per frame):**
- Culling triggers every **166ms of real time**
- **6 warnings per second**
- Spacing: exactly 166ms regardless of update count

### Console Output (Expected)
```
11/16/2025 19:15:00.000 [INF] Scene loaded
11/16/2025 19:15:00.001 [DebugSeverityMedium] Buffer warning... ← Immediate trigger
11/16/2025 19:15:00.167 [DebugSeverityMedium] Buffer warning... ← 166ms later
11/16/2025 19:15:00.333 [DebugSeverityMedium] Buffer warning... ← 166ms later
11/16/2025 19:15:00.500 [DebugSeverityMedium] Buffer warning... ← 167ms later
```

**Analysis:**
- Δ1 = 167ms - 1ms = 166ms ✅
- Δ2 = 333ms - 167ms = 166ms ✅
- Δ3 = 500ms - 333ms = 167ms ✅ (rounding)

---

## Best Practices

### ✅ DO: Use Time-Based Throttling
```csharp
private double lastOperationTime = -1.0;
private const double OperationIntervalSeconds = 0.1; // 100ms

public void Update()
{
    var now = SceneManager.Time;
    if ((now - lastOperationTime) >= OperationIntervalSeconds)
    {
        lastOperationTime = now;
        ExpensiveOperation();
    }
}
```

### ❌ DON'T: Use Frame Counters
```csharp
private int frameCounter = 0;

public void Update()
{
    frameCounter++;
    if (frameCounter >= 10)
    {
        frameCounter = 0;
        ExpensiveOperation(); // Frequency depends on update rate!
    }
}
```

### ✅ DO: Make Interval Configurable
```csharp
public double CullingIntervalSeconds { get; set; } = 0.166;

// Can adjust at runtime based on performance
if (SceneManager.Fps < 30)
    CullingIntervalSeconds = 0.333; // Reduce frequency on slow machines
```

### ❌ DON'T: Hardcode Frame Counts
```csharp
const int FrustumCullingUpdateInterval = 10; // Assumes 60fps!
```

---

## Migration Guide

### Step 1: Add Time Tracking
```csharp
// OLD
private int frameCounter = 0;
private const int FrustumCullingUpdateInterval = 10;

// NEW
private double lastCullingTime = -1.0;
private const double CullingIntervalSeconds = 0.166;
```

### Step 2: Replace Condition
```csharp
// OLD
frameCounter++;
if (frameCounter >= FrustumCullingUpdateInterval)
{
    frameCounter = 0;
    ExecuteFrustumCulling();
}

// NEW
var currentTime = SceneManager.Time;
if ((currentTime - lastCullingTime) >= CullingIntervalSeconds)
{
    lastCullingTime = currentTime;
    ExecuteFrustumCulling();
}
```

### Step 3: Test
- ✅ Run at various framerates (30, 60, 120 FPS)
- ✅ Verify warnings appear every ~166ms
- ✅ Cause lag spikes (e.g., drag window) - should stay consistent
- ✅ Check that multiple updates per frame don't break it

---

## Summary

**Time-based throttling is:**
- ✅ More robust (handles fixed timestep)
- ✅ More consistent (predictable timing)
- ✅ More maintainable (clear intent)
- ✅ More future-proof (engine-agnostic)

**Frame-based throttling is:**
- ❌ Fragile (breaks with time accumulators)
- ❌ Inconsistent (varies with framerate)
- ❌ Confusing ("10 frames" = how much time?)
- ❌ Engine-dependent (assumes update pattern)

**Always prefer time-based throttling for any operation that needs consistent pacing!**

---

**Status:** ✅ **IMPLEMENTED - Time-based throttling active**
