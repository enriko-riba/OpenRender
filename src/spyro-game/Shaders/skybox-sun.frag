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

layout (std140, binding = 1) uniform light {
    Light dirLight;
};

uniform float uSunHaloRadius;
uniform float uSunIntensity;
uniform float uCosSunAngularRadius;
uniform float uTime;
uniform vec2 uViewportSize;
uniform mat4 uInvProjection;

out vec4 FragColor;

float hash13(vec3 p) {
    p = fract(p * vec3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return fract((p.x + p.y) * p.z);
}

vec3 hash33(vec3 p) {
    p = fract(p * vec3(0.1031, 0.11369, 0.13787));
    p += dot(p, p.yzx + 19.19);
    return fract(vec3(
        (p.x + p.y) * p.z,
        (p.x + p.z) * p.y,
        (p.y + p.z) * p.x
    ));
}

float starLayer(vec3 dir, float scale, float radiusScale,
                float minBrightness, float maxBrightness,
                float twinkleSpeed, float twinkleAmount, vec3 seedOffset)
{
    vec3 p = dir * scale + seedOffset;
    vec3 baseCell = floor(p);
    vec3 frac = fract(p);

    float bestDist = 1e9;
    vec3 bestCell = vec3(0.0);

    for (int ix = -1; ix <= 1; ++ix) {
        for (int iy = -1; iy <= 1; ++iy) {
            for (int iz = -1; iz <= 1; ++iz) {
                vec3 neighbor = vec3(ix, iy, iz);
                vec3 cell = baseCell + neighbor;
                vec3 point = neighbor + hash33(cell);
                vec3 diff = point - frac;
                float dist = dot(diff, diff);
                if (dist < bestDist) {
                    bestDist = dist;
                    bestCell = cell;
                }
            }
        }
    }

    float radiusSeed = hash13(bestCell + vec3(17.0, 23.0, 29.0));
    float radius = mix(0.6, 1.0, radiusSeed) * radiusScale;
    float shape = exp(-bestDist * radius);

    float brightnessSeed = hash13(bestCell + vec3(31.0, 37.0, 41.0));
    float brightness = mix(minBrightness, maxBrightness, brightnessSeed);

    float twinkleSeed = hash13(bestCell + vec3(43.0, 47.0, 53.0));
    float twinkleFreq = mix(0.7, 1.9, twinkleSeed);
    float twinkle = 1.0 + twinkleAmount * sin(uTime * twinkleSpeed * twinkleFreq + twinkleSeed * 6.2831853);

    return shape * brightness * twinkle;
}

// Worley-based star field, multiple scales to break tiling
float stars(vec3 rayWS) {
    vec3 dir = normalize(rayWS);

    float starField = 0.0;
    starField += starLayer(dir, 220.0, 8.0, 0.45, 1.10, 1.05, 0.30, vec3(17.0, 29.0, 47.0));
    starField += starLayer(dir, 360.0, 10.5, 0.60, 1.35, 1.45, 0.35, vec3(71.0, 11.0, 53.0));
    starField += starLayer(dir, 520.0, 13.5, 0.78, 1.80, 2.05, 0.40, vec3(131.0, 19.0, 83.0));

    return clamp(starField, 0.0, 1.0);
}

void main()
{
    vec2 ndc = (gl_FragCoord.xy / uViewportSize) * 2.0 - 1.0;
    vec4 clip = vec4(ndc, -1.0, 1.0);
    vec3 rayVS = normalize((uInvProjection * clip).xyz);
    mat3 invViewRot = transpose(mat3(view));
    vec3 rayWS = normalize(invViewRot * rayVS);

    vec3 sunDirWS = normalize(-dirLight.position);
    float elevation = sunDirWS.y;

    vec3 daySkyColor = vec3(0.3, 0.6, 1.0) * mix(1.2, 0.7, rayVS.y);
    vec3 nightSkyColor = vec3(0.005, 0.01, 0.025) * mix(1.2, 0.7, rayVS.y);

    float dayFactor = smoothstep(-0.1, 0.1, elevation);
    vec3 sky = mix(nightSkyColor, daySkyColor, dayFactor);

    float deepNightFactor = smoothstep(-0.4, -0.6, elevation);
    sky = mix(sky, vec3(0.0), deepNightFactor);

    float dawnDuskFactor = smoothstep(-0.2, 0.0, elevation) * (1.0 - smoothstep(0.0, 0.2, elevation));
    if (dawnDuskFactor > 0.0)
    {
        vec3 dawnDir = normalize(vec3(sunDirWS.x, 0.0, sunDirWS.z));
        float glow = pow(max(0.0, dot(rayVS, dawnDir)), 10.0);
        vec3 dawnColor = vec3(1.0, 0.4, 0.1);
        sky += dawnColor * glow * dawnDuskFactor;
    }

    float starValue = stars(rayWS);
    float starVisibility = smoothstep(0.0, -0.3, elevation);
    sky += vec3(starValue) * starVisibility;

    float cosTheta = clamp(dot(rayVS, sunDirWS), -1.0, 1.0);

    float rim = fwidth(cosTheta) * 0.5;
    float sunDisc = smoothstep(uCosSunAngularRadius - rim, uCosSunAngularRadius + rim, cosTheta);

    float cosHaloRadius = cos(uSunHaloRadius);
    float halo = smoothstep(cosHaloRadius, uCosSunAngularRadius, cosTheta);
    halo *= halo;

    vec3 sunColor   = mix(vec3(1.00, 0.92, 0.70), vec3(1.00, 1.00, 0.90), clamp(elevation * 0.6 + 0.5, 0.0, 1.0));
    vec3 haloColor  = mix(vec3(1.00, 0.60, 0.25), sunColor, smoothstep(0.0, 0.25, elevation));

    vec3 sunGlow = sunDisc * sunColor * 6.0 + halo * haloColor * 1.5;
    sunGlow *= uSunIntensity;

    FragColor = vec4(sky + sunGlow, 1.0);
}
