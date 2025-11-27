# Player Movement and Collision Plan

## Current Implementation Analysis
The current player controller (`Player.cs`) uses a hybrid approach:
- **Movement**: Velocity-based for Y (gravity/jumping), but direct position manipulation for X/Z movement.
- **Collision**: 
  - Vertical: Simple raycast/block check at feet for grounding, and AABB check at head for ceiling.
  - Horizontal: Sphere-vs-AABB check against neighboring blocks.
- **Auto-step**: Existing logic attempts to detect obstacles and apply a vertical impulse (`StepUpImpulse`) if the collision normal aligns with movement direction.

### Identified Issues
1. **Sinking into blocks**: Occurs when jumping near walls or on edges. Likely due to precision errors or the sphere collider pushing the player into an invalid state which is then not resolved correctly in the Y axis.
2. **Narrow space sinking**: Jumping in narrow spaces causes penetration, likely because the collision resolution pushes the player into a neighbor block, and the subsequent frame's ground check fails or detects the wrong block.
3. **Edge sinking**: Jumping on block edges causes sinking. The single-point or limited-point ground check might be missing the block when on the edge.
4. **Auto-jump reliability**: Needs to be more robust and context-aware.
5. **Gap crossing**: Player falls into 1-block holes too easily.

## Requirements

### Player Dimensions
- **Height**: ~1.70m (Camera > 1 block, Player < 2 blocks).
- **Width**: Should fit in 1-block wide holes/trenches.

### Movement Logic
- **Jumping**: 
  - Preserves X/Z velocity.
  - +Y impulse should not reset X/Z velocity.
  - Allowed on 1-block high obstacles in all 4 cardinal directions.
- **Auto-jump**:
  - Triggered when moving against a 1-block tall obstacle.
  - **Angle Constraint**: Only trigger if the angle between movement direction and block surface is near 90 degrees (head-on). If the angle is shallow, the player should slide along the wall.
  - Not physically accurate: Can penetrate/teleport slightly to clear the obstacle if needed.
- **Falling**:
  - Should fit into 1-block holes.
  - **Gap Crossing**: With constant movement, 1-block wide depressions should be crossed without falling to the bottom (momentum preservation + "coyote time" or predictive landing).

## Proposed Solution

### 1. Collision Geometry & Resolution
- **Shape**: Use a **Capsule** or **Cylinder** (Height: 1.7m, Radius: ~0.3-0.4m) for collision. This handles corners better than AABB and prevents getting stuck on internal edges.
- **Algorithm**: Implement a "Collide and Slide" algorithm.
  1. Calculate intended velocity vector.
  2. Detect collisions along the path.
  3. If collision occurs, project the remaining velocity vector onto the collision plane (slide).
  4. Repeat until distance traveled or max iterations reached.
- **Separation**: Perform X/Z movement and collision resolution *independently* from Y movement initially, then combine, or handle full 3D slide but ensure gravity doesn't cause sliding off flat surfaces (use a "ground normal" check).

### 2. Ground Detection (The "Block Below")
- Instead of a single point check, use a **Shape Cast** (SphereCast or BoxCast) downwards from the player's feet.
- This ensures that even if standing on an edge, the ground is detected.
- **Block Below Definition**: The block with the highest Y surface that intersects the player's footprint at `Y - epsilon`.

### 3. Auto-Jump Implementation
- **Detection**: When a horizontal collision is detected:
  1. Check if the obstacle is a valid "step" (solid block).
  2. Check height: `Obstacle.Y_Top - Player.Y_Feet <= 1.1m`.
  3. Check clearance: Ensure space exists at `Obstacle.Y_Top + Player.Height`.
- **Angle Check**:
  - Calculate `Dot(MovementDirection.Normalized, -WallNormal)`.
  - If `Dot > 0.7` (approx 45 degrees incidence, tunable), trigger Auto-Jump.
  - Otherwise, allow standard "Collide and Slide" to handle it (player slides along wall).
- **Execution**: Apply an immediate vertical impulse or smooth position interpolation to `TargetY = Obstacle.Y_Top`.

### 4. Gap Crossing (Inertia & Gravity)
- **Momentum**: Ensure X/Z velocity is maintained when airborne.
- **Gap Logic**: 
  - If moving fast enough (> threshold), delay gravity application slightly ("Coyote Time") or use a predictive raycast.
  - If a gap is 1 block wide and the player is moving towards a solid block on the other side, the "Collide and Slide" logic combined with preserved momentum should naturally carry the player over if gravity isn't applied instantaneously at full force.
  - Alternatively, treat 1-block gaps as "walkable" if speed is high enough by checking the block *ahead* of the gap.

### 5. Implementation Steps
1. **Refactor `Player.cs`**:
   - Remove direct position manipulation in `HandleMovement`.
   - Introduce `Velocity` vector (3D).
   - Implement `Move(Vector3 delta)` method with collision resolution.
2. **Implement `ResolveCollision(Vector3 position, Vector3 velocity)`**:
   - Iterative solver.
   - Handle X/Z and Y separately to ensure stable grounding.
3. **Implement `CheckGround()`**:
   - Robust multi-point or shape cast.
4. **Implement `CheckAutoJump()`**:
   - Raycast forward at knee height and head height.
   - If knee hits and head doesn't, and angle is valid -> Auto-Jump.

## Edge Cases to Handle
- **Head Hitting Ceiling**: Zero out Y velocity immediately.
- **Jumping into Corners**: Ensure the slide vector doesn't push player into the other wall (multi-pass resolution).
- **Staircases**: Auto-jump should handle continuous 1-block steps smoothly.
