#version 460 core

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


uniform samplerCube texture_cubemap;

//---------IN------------
in vec3 texCoordCube;
in vec3 pos;

//---------UNIFORM------------
uniform sampler2D tint;  // the color of the sky on the half-sphere where the sun is. (time x height)
uniform sampler2D tint2; // the color of the sky on the opposite half-sphere. (time x height)
uniform sampler2D sun;   // sun texture (radius x time)
//---------OUT------------
out vec3 color;

//---------MAIN------------
void main() {
    vec3 sun_norm = normalize(dirLight.position);
    vec3 pos_norm = normalize(pos);
    float dist = dot(sun_norm, pos_norm);

    // We read the tint texture according to the position of the sun
    vec3 color_wo_sun = texture(tint2, vec2((sun_norm.y + 1.0) / 2.0, max(0.01, pos_norm.y))).rgb;
    vec3 color_w_sun = texture(tint, vec2((sun_norm.y + 1.0) / 2.0, max(0.01, pos_norm.y))).rgb;
    color = mix(color_wo_sun, color_w_sun, dist * 0.5 + 0.5);

    // Sun
    float radius = length(pos_norm - sun_norm);
    if (radius < 0.05) {  // We are in the area of the sky covered by the sun
        float time = clamp(sun_norm.y, 0.01f, 1f);
        radius = radius / 0.05;
        if (radius < 1.0 - 0.001) {  // < we need a small bias to avoid flickering on the border of the texture
            // We read the alpha value from a texture where x = radius and y=height in the sky (~time)
            vec4 sun_color = texture(sun, vec2(radius, time));
            color = mix(color, sun_color.rgb, sun_color.a);
        }
    }
}
