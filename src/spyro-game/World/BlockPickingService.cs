using OpenRender.Core.Rendering;
using OpenTK.Mathematics;

namespace SpyroGame.World;

/// <summary>
/// Service responsible for block picking logic, decoupled from rendering.
/// Manages picking state, timing, and caching independently.
/// Note: Block picking functionality removed - UI will display N/A.
/// </summary>
public class BlockPickingService
{
    private readonly VoxelWorld world;
    private readonly VoxelTerrainRenderer terrainRenderer;
    
    // Timing
    private double lastPickTime = 0.0;
    private const double PickIntervalSeconds = 0.25; // 4 Hz
    
    // Camera tracking
    private Vector3 lastCameraPosition;
    private Vector3 lastCameraDirection;
    private const float CameraMovementThreshold = 0.01f; // Minimum movement to trigger re-pick
    private bool isFirstUpdate = true; // Track first update to initialize camera tracking
    
    // Cached result
    private BlockState? cachedPickedBlock = null;
    
    public BlockPickingService(VoxelWorld world, VoxelTerrainRenderer terrainRenderer)
    {
        this.world = world;
        this.terrainRenderer = terrainRenderer;
    }
    
    /// <summary>
    /// Currently picked block (cached result).
    /// Always returns null since picking is disabled.
    /// </summary>
    public BlockState? PickedBlock => null;
    
    /// <summary>
    /// Update the picking service. Call this once per frame.
    /// Currently a no-op since picking is disabled.
    /// </summary>
    /// <param name="currentTime">Current game time in seconds</param>
    /// <param name="camera">Current camera</param>
    /// <param name="screenCenterX">Screen center X coordinate</param>
    /// <param name="screenCenterY">Screen center Y coordinate</param>
    /// <param name="maxDistance">Maximum picking distance</param>
    public void Update(double currentTime, ICamera camera, int screenCenterX, int screenCenterY, float maxDistance = 5.0f)
    {
        // Initialize camera tracking on first update
        if (isFirstUpdate)
        {
            isFirstUpdate = false;
            lastCameraPosition = camera.Position;
            lastCameraDirection = camera.Front;
        }
        
        // Block picking disabled - FBO removed to eliminate jitter
        // UI will display N/A for picked block
        cachedPickedBlock = null;
        terrainRenderer.PickedBlock = null;
    }
    
    /// <summary>
    /// Clear the cached picked block.
    /// </summary>
    public void Clear()
    {
        cachedPickedBlock = null;
        terrainRenderer.PickedBlock = null;
    }
    
    /// <summary>
    /// Check if camera moved significantly since last pick.
    /// </summary>
    private bool HasCameraMoved(Vector3 currentPosition, Vector3 currentDirection)
    {
        var positionDelta = (currentPosition - lastCameraPosition).Length;
        var directionDelta = (currentDirection - lastCameraDirection).Length;
        
        return positionDelta > CameraMovementThreshold || directionDelta > CameraMovementThreshold;
    }
}
