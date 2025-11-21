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
    private readonly ChunkStreamingManager? streamingManager;
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
    /// Constructor for GPU streaming mode.
    /// </summary>
    public BlockPickingService(ChunkStreamingManager streamingManager, ICamera camera)
    {
        // In GPU mode, we don't have direct access to VoxelWorld or VoxelTerrainRenderer in the same way
        // But since picking is disabled anyway, we just need to satisfy the constructor
        this.world = null!; 
        this.streamingManager = streamingManager;
        this.terrainRenderer = streamingManager.GetTerrainRenderer();
    }
    
    /// <summary>
    /// Currently picked block (cached result).
    /// </summary>
    public BlockState? PickedBlock => cachedPickedBlock;
    
    /// <summary>
    /// Update the picking service. Call this once per frame.
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
        
        // Only update if enough time passed or camera moved significantly
        bool shouldUpdate = (currentTime - lastPickTime >= PickIntervalSeconds) || 
                           HasCameraMoved(camera.Position, camera.Front);
                           
        if (shouldUpdate && streamingManager != null)
        {
            lastPickTime = currentTime;
            lastCameraPosition = camera.Position;
            lastCameraDirection = camera.Front;
            
            if (streamingManager.CollisionManager.Raycast(camera.Position, camera.Front, maxDistance, out Vector3 hitPoint, out Vector3i blockPos, out Vector3 normal, out BlockType blockType))
            {
                cachedPickedBlock = new BlockState(blockPos, blockType);
            }
            else
            {
                cachedPickedBlock = null;
            }
            
            if (terrainRenderer != null)
            {
                terrainRenderer.PickedBlock = cachedPickedBlock;
            }
        }
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
