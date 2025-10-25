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
in vec3 vRayVS;
in vec3 vRayWS;

out vec4 FragColor;

// Function to generate a pseudo-random number
float rand(vec2 co){
    return fract(sin(dot(co.xy ,vec2(12.9898,78.233))) * 43758.5453);
}

// A simple procedural star field
float stars(vec3 viewDir) {
    float starIntensity = 0.0;
    // Densities adjusted to reduce star count
    float starDensity1 = 0.998;
    float starDensity2 = 0.9985;
    float starDensity3 = 0.999;

    vec2 uv1 = viewDir.xy * 1500.0;
    vec2 uv2 = viewDir.yz * 2000.0;
    vec2 uv3 = viewDir.xz * 2500.0;

    float starNoise1 = rand(uv1);
    if (starNoise1 > starDensity1) {
        float twinkle = rand(vec2(starNoise1, uTime * 0.2));
        float brightness = pow((starNoise1 - starDensity1) / (1.0 - starDensity1), 2.0);
        starIntensity += brightness * (0.5 + twinkle * 0.5);
    }

    float starNoise2 = rand(uv2);
    if (starNoise2 > starDensity2) {
        float twinkle = rand(vec2(starNoise2, uTime * 0.2 + 0.3));
        float brightness = pow((starNoise2 - starDensity2) / (1.0 - starDensity2), 2.0);
        starIntensity += brightness * (0.5 + twinkle * 0.5);
    }

    float starNoise3 = rand(uv3);
    if (starNoise3 > starDensity3) {
        float twinkle = rand(vec2(starNoise3, uTime * 0.2 + 0.6));
        float brightness = pow((starNoise3 - starDensity3) / (1.0 - starDensity3), 2.0);
        starIntensity += brightness * (0.5 + twinkle * 0.5);
    }

    return clamp(starIntensity * 1.5, 0.0, 1.0);
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

    // --- THE FIX for STARS ---
    // Use the new, clean world-space vector for the stars.
    float stars = stars(rayWS);
    float starVisibility = smoothstep(0.0, -0.3, elevation);
    sky += vec3(stars) * starVisibility;

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
