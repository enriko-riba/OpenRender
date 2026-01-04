#version 460 core

layout (std140, binding = 0) uniform camera {
    mat4 view;
    mat4 projection;
    vec3 cameraPos;
    vec3 cameraDir;
};

layout (location = 0) in vec3 aPosition;

out vec3 rayWS;

void main()
{
    mat3 invViewRot = transpose(mat3(view));        // inverse rotation of view
    rayWS = normalize(invViewRot * aPosition);      // world-space ray for this vertex

    mat4 viewNoTranslation = mat4(mat3(view));
    vec4 pos = projection * viewNoTranslation * vec4(aPosition, 1.0);
    gl_Position = pos.xyww;                         // keep forcing depth to 1
}
