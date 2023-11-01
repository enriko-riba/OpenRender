# Batching multiple scene nodes geometry

Batching is a technique that allows you to combine multiple scene nodes geometry into a single draw call. This is useful when you have a lot of objects that share the same shader and vertex declaration and you want to reduce the number of draw calls.

## How it works
Scene nodes in the default layer are sorted by shader and vertex declaration. If two or more scene nodes share the same shader and vertex declaration, they are combined into a single draw call. This is done automatically by the engine and you don't have to do anything except opt-in on scene node level via its `IsBatchingAllowed` property.

Batched nodes can have different textures and materials. The engine will upload the materials and texture handles to SSBO and automatically switch textures and materials between sub-draw calls.

### Limitations
- Batching is only supported for scene nodes in the default layer.
- All nodes opting in into batching must use either the built-in "Shaders/standard-batching.vert", "Shaders/standard-batching.frag" shaders or a compatible shader.

## Sample overview
This sample shows how to use scene node batching. It renders 5000 objects with different textures and materials. All objects are batched into a single draw call.
