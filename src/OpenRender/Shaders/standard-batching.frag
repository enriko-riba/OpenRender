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

 struct Material {   
    vec4 diffuse;
    vec4 emissive;
    vec3 specular;
    float shininess;
    vec4 extra;
    //float shininess;
    //int uHasDiffuseTexture;
    //float uDetailTextureFactor;
};
layout(std430, binding = 1) readonly buffer ssbo_material {
    Material mat[];
};

struct Textures {
    sampler2D diffuse;
    sampler2D detail;
    sampler2D normal;
};
layout(std430, binding = 2) readonly buffer ssbo_textures {
    Textures textures[];
};

//uniform int uHasDiffuseTexture;         //  should the diffuse color be sampled from texture_diffuse1
//uniform float uDetailTextureFactor;     //  scale of detail texture that is blended with diffuse, if 0 detail sampling is not used
//uniform int uHasNormalTexture;          //  should the normal map txture be sampled from texture_normal
const int uHasNormalTexture = 0;

in vec3 vertexColor;                    //  interpolated vertex color
in vec3 vertexNormal;                   //  interpolated normal
in vec2 texCoord;                       //  uv texture coordinates
in vec3 fragPos;

flat in int drawID;
out vec4 outputColor;

vec3 LambertianComponent(in vec3 directionToLight, in vec3 worldNormal, in vec3 lightColor);
vec3 SpecularComponent(in vec3 directionToLight, in vec3 worldNormal, in vec3 worldPosition, in vec3 lightColor, in vec3 specularColor, in float specularPower);

void main()
{    
    outputColor = vec4(0);
    vec3 norm = normalize(vertexNormal);
    vec3 viewDir = normalize(cameraPos - fragPos);
    vec3 lightDir = -normalize(dirLight.position);
   
    vec3 texDiffuse = vec3(1);
    vec3 texDetail = vec3(1);
    
    Material mat = mat[drawID];
    sampler2D texture_diffuse = textures[drawID].diffuse;
    sampler2D texture_detail = textures[drawID].detail;
    sampler2D texture_normal = textures[drawID].normal;

    texDiffuse = vec3(texture(texture_diffuse, texCoord));
    if(mat.extra.x > 0)
    {
        texDetail = vec3(texture(texture_detail, texCoord * mat.extra.x));//mat.uDetailTextureFactor));
    }
    vec3 texColor = texDiffuse * texDetail;
    
    if(uHasNormalTexture > 0)
    {   
        vec3 normalMap = texture(texture_normal, texCoord).rgb;
        norm = normalize(normalMap * 2.0 - 1.0);
    }

    vec3 Ac = texColor * dirLight.ambient * (vertexColor + mat.diffuse.rgb);
    vec3 Dc = texColor * LambertianComponent(lightDir, norm, dirLight.diffuse) * (vertexColor + mat.diffuse.rgb);
    vec3 Sc = texColor * SpecularComponent(lightDir, norm, fragPos, dirLight.specular, mat.specular, mat.shininess) * (vertexColor + mat.diffuse.rgb);    
    outputColor += vec4(mat.emissive.rgb + Ac + Dc + Sc, 1);
}


//-----------------------------------------------------------------------------------------------
//
//	Description:	basic lambertian light equation.
//				
//-----------------------------------------------------------------------------------------------
vec3 LambertianComponent(in vec3 directionToLight, in vec3 worldNormal, in vec3 lightColor)
{
    float d = dot(directionToLight, worldNormal);
	float lamb = max(d, 0);
	return lamb * lightColor;
}

//-----------------------------------------------------------------------------------------------
//
//	Description:	basic specular light equation.
//				
//-----------------------------------------------------------------------------------------------
vec3 SpecularComponent(in vec3 directionToLight, in vec3 worldNormal, in vec3 worldPosition, in vec3 lightColor, in vec3 specularColor, in float specularPower)
{
	vec3 resultColor = vec3(0);
	if(length(specularColor)>0)
	{
        float NdotL = dot(worldNormal, directionToLight);
        if(NdotL > 0) 
        {
		    vec3 reflectionVector = normalize(reflect(-directionToLight, worldNormal));
		    vec3 directionToCamera = normalize(cameraPos - worldPosition);
            float d = dot(reflectionVector, directionToCamera);
		    resultColor = lightColor * clamp(d * specularColor, 0.0, 1.0) * specularPower;
		    resultColor = clamp(resultColor, 0.0, 1.0);
        }
	}	
	return resultColor;
}

/*

//-----------------------------------------------------------------------------------------------
//
//	Description:	specular light equation based on the simplified Blinn Phong equation.
//				
//-----------------------------------------------------------------------------------------------
vec3 BlinnPhongSpecular( in vec3 directionToLight, in vec3 worldNormal, in vec3 worldPosition, in vec3 lightColor, in vec3 specularColor, in float specularPower)
{    
	vec3 specular = vec3(0);
	if(length(specularColor)>0)
	{
		vec3 viewer = normalize(cameraPos - worldPosition);
		
		// Compute the half vector
		vec3 half_vector = normalize(directionToLight + viewer);
	 
		// Compute the angle between the half vector and normal
		float  HdotN = max( 0.0f, dot( half_vector, worldNormal ) );
	 
		// Compute the specular color
		specular = specularColor * pow( HdotN, specularPower ) * lightColor;
		specular = clamp(specular, 0, 1.0);
    }   
    return specular;
}

//-----------------------------------------------------------------------------------------------
//
//	Description:	inverted range attenuation equation.
//				
//-----------------------------------------------------------------------------------------------
float Attenuation(float lDistance, float lRange, float falloff)
{
    float att = 1.0 - clamp(lDistance*falloff / lRange, 0 , 1.0);
    return att;
}
*/