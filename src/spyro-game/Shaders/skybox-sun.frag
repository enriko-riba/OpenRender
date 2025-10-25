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

// TWO VECTORS from vertex shader
 in vec3 vRayWS;
in vec3 vRayVS;

out vec4 FragColor;

float hash21(vec2 p) {
    p = fract(p * vec2(234.34, 435.345) + vec2(34.345, 19.45));
    p += dot(p, p + 34.345);
    return fract(p.x * p.y);
}

vec2 hash22(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * vec3(443.8975, 441.4234, 437.1950));
    p3 += dot(p3, p3.yzx + 19.19);
    return fract(vec2((p3.x + p3.y) * p3.z, (p3.x + p3.z) * p3.y));
}

// A simple procedural star field, parameterized on spherical coordinates
float stars(vec3 rayWS) {
    vec3 dir = normalize(rayWS);
    const float TWO_PI = 6.28318530718;
    const float INV_TWO_PI = 0.1591549430918;
    const float INV_PI = 0.3183098861838;

    float lon = atan(dir.z, dir.x);               // [-pi, pi]
    lon = lon < 0.0 ? lon + TWO_PI : lon;         // [0, 2pi)
    float lat = acos(clamp(dir.y, -1.0, 1.0));    // [0, pi]

    const float CELL_COUNT_U = 1400.0;
    const float CELL_COUNT_V = 700.0;
    const float STAR_DENSITY = 0.0016;
    const float MIN_RADIUS = 0.045;
    const float MAX_RADIUS = 0.110;
    const float MIN_BRIGHTNESS = 0.55;
    const float MAX_BRIGHTNESS = 1.75;
    const float TWINKLE_AMOUNT = 0.35;

    vec2 coord = vec2(lon * (CELL_COUNT_U * INV_TWO_PI),
                      lat * (CELL_COUNT_V * INV_PI));
    vec2 baseCell = floor(coord);
    vec2 frac = coord - baseCell;

    float sinLat = max(sin(lat), 0.02);
    float starAccum = 0.0;

    for (int j = -1; j <= 1; ++j) {
        for (int i = -1; i <= 1; ++i) {
            vec2 neighborCell = baseCell + vec2(i, j);

            float wrappedU = neighborCell.x - floor(neighborCell.x / CELL_COUNT_U) * CELL_COUNT_U;
            float clampedV = clamp(neighborCell.y, 0.0, CELL_COUNT_V - 1.0);
            vec2 cellId = vec2(wrappedU, clampedV);

            float spawn = hash21(cellId + vec2(17.0, 29.0));
            if (spawn > STAR_DENSITY * sinLat) {
                continue;
            }

            vec2 jitter = hash22(cellId + vec2(13.0, 7.0)) - 0.5;
            vec2 starPos = cellId + vec2(0.5) + 0.45 * jitter;

            vec2 diff = coord - starPos;
            diff.x -= round(diff.x / CELL_COUNT_U) * CELL_COUNT_U;
            diff.x *= sinLat;

            float radiusSeed = hash21(cellId + vec2(41.0, 71.0));
            float radius = mix(MIN_RADIUS, MAX_RADIUS, radiusSeed);
            float shape = exp(-dot(diff, diff) / max(radius * radius, 1e-6));

            float brightnessSeed = hash21(cellId + vec2(59.0, 83.0));
            float brightness = mix(MIN_BRIGHTNESS, MAX_BRIGHTNESS, brightnessSeed);

            float twinkleSeed = hash21(cellId + vec2(101.0, 37.0));
            float twinkleFreq = mix(0.8, 1.9, twinkleSeed);
            float twinkle = 1.0 + TWINKLE_AMOUNT * sin(uTime * twinkleFreq + twinkleSeed * TWO_PI);

            starAccum += shape * brightness * twinkle;
        }
    }

    return clamp(starAccum, 0.0, 1.0);
}

void main()
{
    // Use the two incoming vectors
    vec3 rayVS = normalize(vRayVS); // View-space ray for sun/sky
    vec3 rayWS = normalize(vRayWS); // World-space ray for stars

    vec3 sunDirWS = normalize(-dirLight.position);
    float elevation = sunDirWS.y;

    // Sky colors are calculated using the view-space ray, as this was working for the overall sky gradient.
    vec3 daySkyColor = vec3(0.3, 0.6, 1.0) * mix(1.2, 0.7, rayVS.y);
    vec3 nightSkyColor = vec3(0.005, 0.01, 0.025) * mix(1.2, 0.7, rayVS.y);

    // Day/night transition
    float dayFactor = smoothstep(-0.1, 0.1, elevation);
    vec3 sky = mix(nightSkyColor, daySkyColor, dayFactor);

    // Transition to black at deep night
    float deepNightFactor = smoothstep(-0.4, -0.6, elevation);
    sky = mix(sky, vec3(0.0), deepNightFactor);

    // The sun, dawn, and glow calculations are left as they were, using their mix of coordinate spaces that was empirically working.
    float dawnDuskFactor = smoothstep(-0.2, 0.0, elevation) * (1.0 - smoothstep(0.0, 0.2, elevation));
    if (dawnDuskFactor > 0.0)
    {
        vec3 dawnDir = normalize(vec3(sunDirWS.x, 0.0, sunDirWS.z));
        float glow = pow(max(0.0, dot(rayVS, dawnDir)), 10.0);
        vec3 dawnColor = vec3(1.0, 0.4, 0.1);
        sky += dawnColor * glow * dawnDuskFactor;
    }

    // Use the new, clean world-space vector for the stars.
    float starValue = stars(rayWS);
    float starVisibility = smoothstep(0.0, -0.3, elevation);
    sky += vec3(starValue) * starVisibility;

    // Sun disc calculation
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
