#version 460
#extension GL_ARB_bindless_texture : require

#define MAX_LIGHTS 4

//	light types
#define DIR_LIGHT			0
#define POINT_LIGHT			1
#define SPOT_LIGHT			2
#define NO_COLOR			vec4(0);			// for no light we must return 0 in alpha channel otherwise the transparent pixels would loose its transparency

uniform int uTotalLights;

layout (std140, binding = 0) uniform camera {    
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
layout(std140, binding = 1) uniform light {
    Light dirLight;
};

layout(std430, binding = 0) readonly buffer ssbo_textures {
    sampler2D bindlessTextures[];
};

 struct Material {   
    vec3 diffuse;
    vec3 emissive;
    vec3 specular;
    float shininess;
};
layout(std430, binding = 1) readonly buffer ssbo_materials {
    Material mat[];
};


uniform float uTime;
uniform ivec2 iResolution;

in vec3 vertexNormal;                   //  interpolated normal
in vec2 texCoord;                       //  uv texture coordinates
in vec3 fragPos;

out vec4 outputColor;

float getFogFactor(float d)
{
    //  FarPlane = 450f;
    const float FogMax = 447.0;
    const float FogMin = 5.0;

    if (d>=FogMax) return 1;
    if (d<=FogMin) return 0;

    return 1 - (FogMax - d) / (FogMax - FogMin);
}

const int waterTextureIndex = 1;
const int foamTextureIndex = 9;

#define PI = 3.1415926535897932;
#define TAU 6.28318530718
#define MAX_ITER 7

void main()
{ 
    Material mat = mat[waterTextureIndex];
    sampler2D tex = bindlessTextures[waterTextureIndex];
    sampler2D texFoam = bindlessTextures[foamTextureIndex];

    vec3 N = vertexNormal;
    vec3 L = -normalize(dirLight.position);

    vec4 texDiffuse = texture(tex, texCoord);
    vec4 texColor = texDiffuse * vec4(mat.diffuse, 1);
    float udx = sin(uTime) * 0.25;
    float udy = cos(uTime) * 0.25;
    vec4 foamColor = texture(texFoam, texCoord + vec2(udx, udy));

    float lambert = clamp(dot(N, L), 0, 1);    
    vec3 Dc = dirLight.diffuse * lambert;
    vec3 Sc = vec3(0);

    if(lambert > 0)
    {
        // blinn-phong
        vec3 V = normalize(cameraPos - fragPos);
        vec3 H = normalize(L + V);
        float specular = clamp(dot(H, N), 0, 1);
        float exponent = pow(2, mat.shininess * 2.0) + 2;       
        Sc = clamp(pow(specular, exponent) * mat.shininess * dirLight.specular * mat.specular, 0, 1);
    }
    outputColor = clamp(vec4(mat.emissive.rgb + (Sc + Dc), 1) * texColor, 0, 1);
    

    vec2 uv = 0.1 * fragPos.xy / iResolution.xy;
    vec2 p = mod(uv*TAU, TAU)-250.0;
    vec2 i = p;
	float c = 1.0;
	float inten = .005;

	for (int n = 0; n < MAX_ITER; n++) 
	{
		float t = uTime * (1.0 - (3.5 / float(n+1)));
		i = p + vec2(cos(t - i.x) + sin(t + i.y), sin(t - i.y) + cos(t + i.x));
		c += 1.0/length(vec2(p.x / (sin(i.x+t)/inten),p.y / (cos(i.y+t)/inten)));
	}
	c /= float(MAX_ITER);
	c = 1.17-pow(c, 1.4);
	vec3 colour = vec3(pow(abs(c), 8.0));
    colour = clamp(colour + vec3(0.0, 0.35, 0.5), 0.0, 1.0);
    foamColor *= vec4(colour, 1);
    outputColor *= foamColor;
    outputColor.a = 1;

    // fog
    vec4 fog = vec4(0.20f, 0.20f, 0.22f, 1.0f);
    float d = distance(fragPos, cameraPos);
    float factor = getFogFactor(d);
    outputColor = mix(outputColor, fog, factor);
}