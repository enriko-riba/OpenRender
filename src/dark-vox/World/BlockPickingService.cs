using OpenRender.Core.Rendering;
using OpenTK.Mathematics;
using DarkVox.Client.Rendering;
using DarkVox.Components;
using DarkVox.Shared.World;

namespace DarkVox.World;

/// <summary>
/// Service responsible for block picking logic, decoupled from rendering.
/// Manages picking state, timing, and caching independently.
/// Note: Block picking functionality removed - UI will display N/A.
/// </summary>
public class BlockPickingService
{
    private readonly CollisionManager collisionManager;
    private readonly VoxelTerrainRenderer terrainRenderer;
    
    // Timing
    private double lastPickTime = 0.0;
    private const double PickIntervalSeconds = 0.5; // 2 Hz
    
    // Camera tracking
    private Vector3 lastCameraPosition;
    private Vector3 lastCameraDirection;
    private const float CameraMovementThreshold = 0.01f; // Minimum movement to trigger re-pick
    private bool isFirstUpdate = true; // Track first update to initialize camera tracking
    
    // Cached result
    private BlockState? cachedPickedBlock = null;
    private Vector3 cachedHitNormal = Vector3.Zero;
    private float cachedHitDistance = float.MaxValue;
    
    public BlockPickingService(CollisionManager collisionManager, VoxelTerrainRenderer terrainRenderer)
    {
        this.collisionManager = collisionManager;
        this.terrainRenderer = terrainRenderer;
    }
    
    /// <summary>
    /// Currently picked block (cached result).
    /// </summary>
    public BlockState? PickedBlock => cachedPickedBlock;

    /// <summary>
    /// Normal of the face that was hit.
    /// </summary>
    public Vector3 HitNormal => cachedHitNormal;

    /// <summary>
    /// Distance to the hit point.
    /// </summary>
    public float HitDistance => cachedHitDistance;

    public void Invalidate()
    {
        lastPickTime = double.NegativeInfinity;
    }

    public void ForceUpdate(double currentTime, ICamera camera, float maxDistance = 5.0f)
    {
        lastPickTime = currentTime;
        lastCameraPosition = camera.Position;
        lastCameraDirection = camera.Front;
        DoPick(camera, maxDistance);
    }
    
    /// <summary>
    /// Update the picking service. Call this once per frame.
    /// </summary>
    /// <param name="currentTime">Current game time in seconds</param>
    /// <param name="camera">Current camera</param>
    /// <param name="maxDistance">Maximum picking distance</param>
    public void Update(double currentTime, ICamera camera, float maxDistance = 5.0f)
    {
        // Initialize camera tracking on first update
        if (isFirstUpdate)
        {
            isFirstUpdate = false;
            lastCameraPosition = camera.Position;
            lastCameraDirection = camera.Front;
        }
        
        // Only update if enough time passed or camera moved significantly
        var shouldUpdate = (currentTime - lastPickTime >= PickIntervalSeconds) || 
                           HasCameraMoved(camera.Position, camera.Front);
                           
        if (shouldUpdate)
        {
            lastPickTime = currentTime;
            lastCameraPosition = camera.Position;
            lastCameraDirection = camera.Front;

            DoPick(camera, maxDistance);
        }
    }

    private void DoPick(ICamera camera, float maxDistance)
    {
        if (collisionManager.Raycast(camera.Position, camera.Front, maxDistance, out var hitPoint, out var blockPos, out var normal, out var descriptor))
        {
            cachedPickedBlock = new BlockState(blockPos, descriptor);
            cachedHitNormal = normal;
            cachedHitDistance = Vector3.Distance(camera.Position, hitPoint);
        }
        else
        {
            cachedPickedBlock = null;
            cachedHitNormal = Vector3.Zero;
            cachedHitDistance = float.MaxValue;
        }

        terrainRenderer?.PickedBlock = cachedPickedBlock;
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
