# OpenRender
The OpenRender framework is a very simple OpenTK/OpenGL based game engine I am using for my Indie projects and demos. Since OpenRender does not aim to be a full featured game engine nor compete with Unity, Unreal Engine or alike
but rather be a renderer with basic scene management and some generic building blocks, OpenRenderer has no UI or editor.

## Main structure
- SceneManager, has one or many Scenes
- Scene, contains SceneNodes
- SceneNode, contains a Mesh a Material and set of controlling properties (position, rotation, scale etc.)
- Mesh, represents the geometry and is basically a collection of VertexBuffer objects (atm only a single VBO is supported)
- Material, contains the shader program and textures with lightning descriptions

The SceneNode is the main building block of the scene. Since the SceneNode gets the geometry and material from outside you can build most of the stuff without implementing custom SceneNodes.
In cases where custom render states, programs or buffer layouts are needed, you can implement your own nodes by inheriting from SceneNode.

### Components
The components folder contains custom SceneNode implementations like:
- Sprite, for rendering 2D objects
- AnimatedSprite, for rendering 2D objects with sprite sheet animations
- SkyBox etc.


## Shader binding
OpenRender passes a predefined set of parameters to shader programs.

### Attributes
```
layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec3 aColor;
layout (location = 3) in vec2 aTexCoord;
```
If using custom shaders with built-in geometry and buffers, make sure that your program uses the same attribute locations.

### Uniforms
```
uniform mat4 model;
uniform int uTotalLights;
uniform int uHasDiffuseTexture;  
uniform float uDetailTextureFactor;
```

### Uniforms blocks
```
layout (std140) uniform camera {    
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
    vec3 cameraDir;
};

struct Light {    
    vec3 position;    
    vec3 ambient;
    vec3 diffuse;
    vec3 specular;
    float falloff;
};
layout(std140) uniform light {
    Light dirLight;
};

struct Material {   
    vec3 diffuse;
    vec3 emissive;
    vec3 specular;
    float shininess;
};
layout(std140) uniform material {
    Material mat;
};
```
#### Uniform block binding points:
- camera 0, 
- light 1 and 
- material 2

> **NOTE**: 
the above are not uniform block indices but binding points. The indices are assigned by OpenGL and can be queried.

## Texture binding
In order to bind textures to samplers during draw calls, the Material needs to contain the texture descriptions and it needs to be initialized.
The textures defined in the material are automatically bound to following sampler names.

### Sampler names
```
uniform sampler2D texture_diffuse;
uniform sampler2D texture_detail;
uniform sampler2D texture_normal;
uniform sampler2D texture_specular;
uniform sampler2D texture_additional1;
uniform sampler2D texture_additional2;
uniform sampler2D texture_additional3;
uniform sampler2D texture_additional4;
```

In addition the following uniforms are automatically setup:
```
uniform int uHasDiffuseTexture;          //  should the diffuse color be sampled from texture_diffuse
uniform float uDetailTextureFactor;      //  scale of detail texture that is blended with diffuse, 0 if detail sampling is not used
```

***Note:*** the standard shader programs use only the `texture_diffuse` and `texture_detail`. Other samplers are set from the material but you need to use your own shader program that makes use of them.

### Texture types
* diffuse 
* detail
* normal
* specular (app specific - not used by the OpenRender standard shaders)
* additional (up to 4, app specific - not used by OpenRender standard shaders)
* cube map (app specific - not used by OpenRender standard shaders except by SkyBox scene node)

#### TextureType enum
The `Texture` class contains a `TextureType` member of type:
```
public enum TextureType
{
    Unknown,
    Diffuse,
    Detail,
    Normal,
    Specular,
    Additional1,
    Additional2,
    Additional3,
    Additional4,
    CubeMap
}
``` 
Unknown textures are treated as diffuse. This means if you create a material with `Unknown` texture type but no `Diffuse` texture, the `Unknown` texture will be used as the `Diffuse` texture.
