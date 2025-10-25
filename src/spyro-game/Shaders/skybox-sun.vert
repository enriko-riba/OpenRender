#version 460 core

layout (std140, binding = 0) uniform camera {
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
    vec3 cameraDir;
};

layout (location = 0) in vec3 aPosition;
out vec3 vRayVS;
out vec3 vRayWS;

void main()
{
    // For the sun/sky, pass the simple position. This results in a view-space vector in the fragment shader that is known to work.
    vRayVS = aPosition;
    // Stars need a world-space direction that ignores perspective to stay fixed on the cubemap.
    vRayWS = transpose(mat3(view)) * aPosition;

    // Standard skybox rendering logic...
    mat4 viewNoTranslation = mat4(mat3(view));
    vec4 pos = projection * viewNoTranslation * vec4(aPosition, 1.0);
    gl_Position = pos.xyww;
}
