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
    vec3 diffuse;
    vec3 emissive;
    vec3 specular;
    float shininess;
    float detailTextureScaleFactor;
    float detailTextureBlendFactor;
};
layout(std140, binding = 2) uniform material {
    Material mat;
};

struct Textures {
    sampler2D diffuse;
    sampler2D detail;
    sampler2D normal;
    sampler2D specular;
    sampler2D bump;
    sampler2D t6;
    sampler2D t7;
    sampler2D t8;
};
layout(std140, binding = 3) uniform textures {
    Textures tex;
};

in vec3 vertexColor;                    //  interpolated vertex color
in vec3 vertexNormal;                   //  interpolated normal
in vec2 texCoord;                       //  uv texture coordinates
in vec3 fragPos;

out vec4 outputColor;

vec3 LambertianComponent(in vec3 directionToLight, in vec3 N, in vec3 lightColor);
vec3 SpecularComponent(in vec3 directionToLight, in vec3 N, in vec3 worldPosition, in vec3 lightColor, in vec3 specularColor, in float specularPower);
vec3 BumpMap(vec3 normal)
{
     vec3 posDX = dFdxFine(fragPos);  // choose dFdx (#version 420) or dFdxFine (#version 450) here
     vec3 posDY = dFdyFine (fragPos);
     vec3 r1 = cross (posDY, normal);
     vec3 r2 = cross (normal, posDX);
     float det = dot (posDX , r1);
     float Hll = texture(tex.bump, texCoord).x;    //-- height from bump map texture, tc=texture coordinates
     float Hlr = texture(tex.bump, texCoord + dFdx(texCoord.xy)).x;
     float Hul = texture(tex.bump, texCoord + dFdy(texCoord.xy)).x;
     vec3 surf_grad = sign(det) * ( (Hlr - Hll) * r1 + (Hul - Hll)* r2 );    
     const float bump_amt = 0.8;       // bump_amt = adjustable bump amount
     return normal * (1.0-bump_amt) + bump_amt * normalize(abs(det) * normal - surf_grad );  // bump normal    
}

void main()
{ 
    outputColor = vec4(0);
    vec3 N = normalize(vertexNormal);
    vec3 L = -normalize(dirLight.position);
   
    vec3 texDiffuse = vec3(1);
    vec3 texDetail = vec3(1);
    
    texDiffuse = vec3(texture(tex.diffuse, texCoord)) + vertexColor;
    texDetail = vec3(texture(tex.detail, texCoord * mat.detailTextureScaleFactor));
    float blendFactor = mat.detailTextureScaleFactor == 0 ? 0 : mat.detailTextureBlendFactor;
    texDiffuse = mix(texDiffuse, texDetail, blendFactor) * mat.diffuse;
    vec3 texColor = texDiffuse * min(vertexColor + mat.diffuse, 1.0);

    //N = BumpMap(norm);    
    float lambert = clamp(dot(N, L), 0, 1);
    vec3 Ac = dirLight.ambient;
    vec3 Dc = dirLight.diffuse * lambert;
    vec3 Sc = vec3(0);

    if(lambert > 0)
    {
        // blinn-phong
        vec3 V = normalize(cameraPos - fragPos);
        vec3 H = normalize(L + V);
        float specular = clamp(dot(H, N), 0, 1);
        float exponent = pow(2, mat.shininess * 2.0) + 2;       
        Sc = pow(specular, exponent) * mat.shininess * dirLight.specular * mat.specular;
    }
    outputColor += vec4(mat.emissive.rgb + (Sc + Ac + Dc) * texColor, 1);
    outputColor = clamp(outputColor, 0, 1);
}


//-----------------------------------------------------------------------------------------------
//
//	Description:	basic lambertian light equation.
//				
//-----------------------------------------------------------------------------------------------
//vec3 LambertianComponent(in vec3 directionToLight, in vec3 N, in vec3 lightColor)
//{
//    float d = dot(directionToLight, N);
//	float lamb = max(d, 0);
//	return lamb * lightColor;
//}
//
//-----------------------------------------------------------------------------------------------
//
//	Description:	basic specular light equation.
//				
//-----------------------------------------------------------------------------------------------
//vec3 SpecularComponent(in vec3 directionToLight, in vec3 N, in vec3 worldPosition, in vec3 lightColor, in vec3 specularColor, in float specularPower)
//{
//	vec3 resultColor = vec3(0);
//	if(length(specularColor)>0)
//	{
//        float NdotL = dot(N, directionToLight);
//        if(NdotL > 0) 
//        {
//		    vec3 reflectionVector = normalize(reflect(-directionToLight, N));
//		    vec3 directionToCamera = normalize(cameraPos - worldPosition);
//            float d = dot(reflectionVector, directionToCamera);
//		    resultColor = lightColor * clamp(d * specularColor, 0.0, 1.0) * specularPower;
//		    resultColor = clamp(resultColor, 0.0, 1.0);
//        }
//	}	
//	return resultColor;
//}
//
/*

//-----------------------------------------------------------------------------------------------
//
//	Description:	specular light equation based on the simplified Blinn Phong equation.
//				
//-----------------------------------------------------------------------------------------------
vec3 BlinnPhongSpecular( in vec3 directionToLight, in vec3 N, in vec3 worldPosition, in vec3 lightColor, in vec3 specularColor, in float specularPower)
{    
	vec3 specular = vec3(0);
	if(length(specularColor)>0)
	{
		vec3 viewer = normalize(cameraPos - worldPosition);
		
		// Compute the half vector
		vec3 half_vector = normalize(directionToLight + viewer);
	 
		// Compute the angle between the half vector and normal
		float  HdotN = max( 0.0f, dot( half_vector, N ) );
	 
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