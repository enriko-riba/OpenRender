## Shader binding
OpenRender passes a predefined set of parameters to shader programs.

### Attributes
```
layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec3 aColor;
layout (location = 3) in vec2 aTexCoord;
```

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

struct Material {   
    vec3 diffuse;
    vec3 emissive;
    vec3 specular;
    float shininess;
};
layout(std140) uniform material {
    Material mat;
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
```

## Texture binding
In order to bind textures to samplers during draw calls, the Material needs to contain the texture descriptions and it needs to be initialized.
The textures defined in the material are automatically sent to shader programs and bound to following sampler names.

### Sampler names
```
uniform sampler2D texture_diffuse;
uniform sampler2D texture_detail;
uniform sampler2D texture_specular;
uniform sampler2D texture_additional1;
uniform sampler2D texture_additional2;
uniform sampler2D texture_additional3;
uniform sampler2D texture_additional4;
```

In addition the following uniforms are automatically setup:
```
uniform int uHasDiffuseTexture;          //  should the diffuse color be sampled from texture_diffuse1
uniform float uDetailTextureFactor;      //  scale of detail texture that is blended with diffuse, if 0 detail sampling is not used
```

***Note:*** the standard shader programs use only the `texture_diffuse` and `texture_detail`. Other samplers are set from the material but you need to use your own shader program that makes use of them.

### Texture types
* diffuse (up to 2)
* specular (1)
* detail (up to 2)
* additional (up to 2, app specific - not used by OpenRender)

#### TextureType enum
The `Texture` class contains a `TextureType` member of type:
```
public enum TextureType
{
    Unknown,
    Diffuse,
    DetailMap,
    Specular,
    Additional,
    Additional2,
    Additional3,
    Additional4
}
``` 
Unknown textures are treated as diffuse.



### Texture related parameters
The following uniforms are used:
```
uniform int hasDiffuseTexture;
uniform float detailTextureFactor;
```

**`hasDiffuseTexture`** is telling the fragment shader if the diffuse1 sampler should be used. When this value is 0 the diffuse1 texture is not contributing to color output.

**`detailTextureFactor`** is telling the fragment shader if the diffuse2 sampler should be used. When this value is > 0 the diffuse2 texture is blended with the output color.