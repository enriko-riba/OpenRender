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

layout (std140, binding = 1) uniform light { Light dirLight; };

// Fog UBO (shared across shaders)
// fogColor4.rgb = fog color, fogParams = (near, far, enabled, unused)
layout (std140, binding = 4) uniform fogBlock {
    vec4 fogColor4;
    vec4 fogParams;
};

uniform float uSunHaloRadius;
uniform float uSunIntensity;
uniform float uCosSunAngularRadius;
uniform float uTime;
uniform vec2  uViewportSize;
uniform int   uIsUnderwater;

out vec4 FragColor;

// ---------------- utils ----------------
float hash13(vec3 p){
    p = fract(p * vec3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return fract((p.x + p.y) * p.z);
}
vec3 hash33(vec3 p){
    p = fract(p * vec3(0.1031, 0.11369, 0.13787));
    p += dot(p, p.yzx + 19.19);
    return fract(vec3(
        (p.x + p.y) * p.z,
        (p.x + p.z) * p.y,
        (p.y + p.z) * p.x
    ));
}
float starLayer(vec3 dir, float scale, float radiusScale,
                float minB, float maxB,
                float twinkleSpeed, float twinkleAmt, vec3 seedOff){
    vec3 p = dir * scale + seedOff;
    vec3 baseCell = floor(p);
    vec3 frac = fract(p);
    float best = 1e9; vec3 bestCell = vec3(0);
    for(int ix=-1; ix<=1; ++ix)
    for(int iy=-1; iy<=1; ++iy)
    for(int iz=-1; iz<=1; ++iz){
        vec3 n = vec3(ix,iy,iz);
        vec3 c = baseCell + n;
        vec3 q = n + hash33(c);
        float d = dot(q-frac, q-frac);
        if(d < best){ best = d; bestCell = c; }
    }
    if(hash13(bestCell) > 0.2) return 0.0;
    float rSeed = hash13(bestCell + vec3(17,23,29));
    float radius = mix(0.6, 1.0, rSeed) * radiusScale;
    float shape = exp(-best * radius);
    float bSeed = hash13(bestCell + vec3(31,37,41));
    float bright = mix(minB, maxB, bSeed);
    float tSeed = hash13(bestCell + vec3(43,47,53));
    float tFreq = mix(0.7, 1.9, tSeed);
    float twinkle = 1.0 + twinkleAmt * sin(uTime * twinkleSpeed * tFreq + tSeed * 6.2831853);
    return shape * bright * twinkle;
}
float stars(vec3 dir){
    dir = normalize(dir);
    float s = 0.0;
    s += starLayer(dir, 120.0, 420.0, 0.45, 1.10, 1.05, 0.010, vec3(17,29,47));
    //s += starLayer(dir, 360.0, 210.75, 0.60, 1.35, 0.0, 0.015, vec3(71,11,53));
    //s += starLayer(dir, 520.0, 412.25, 0.78, 1.80, 0.0, 0.020, vec3(131,19,83));
    return clamp(s, 0.0, 1.0);
}
float getSkyFogFactor(vec3 d){
    float verticality = 1.0 - dot(d, vec3(0,1,0));
    return clamp(verticality * 0.65, 0.0, 1.0);
}
float random(vec2 p){ return fract(sin(dot(p, vec2(12.9898,78.233))) * 43758.5453); }

// ------------- main --------------------
void main(){
    // Pixel NDC
    vec2 ndc = (gl_FragCoord.xy / uViewportSize) * 2.0 - 1.0;   // [-1,1]

    // Use the exact matrices used for skybox drawing: projection * mat4(mat3(view))
    mat4 viewNoTranslation = mat4(mat3(view));
    mat4 invViewProj = inverse(projection * viewNoTranslation);

    // Unproject a point on the far plane and get the world-space ray
    vec4 ws = invViewProj * vec4(ndc, 1.0, 1.0);               // clip z=+1 (far)
    vec3 dir = normalize(ws.xyz / ws.w);                       // world-space view ray

    // Sun
    vec3  sunDirWS  = normalize(-dirLight.position);
    float elevation = sunDirWS.y;

    // Base sky
    vec3 daySky   = vec3(0.3, 0.6, 1.0) * mix(1.2, 0.7, dir.y);
    vec3 nightSky = vec3(0.005, 0.01, 0.025) * mix(1.2, 0.7, dir.y);
    float dayK    = smoothstep(-0.1, 0.1, elevation);
    vec3 sky      = mix(nightSky, daySky, dayK);

    float deepNightK = smoothstep(-0.4, -0.6, elevation);
    sky = mix(sky, vec3(0.0), deepNightK);

    float dawnDuskK = smoothstep(-0.2, 0.0, elevation) * (1.0 - smoothstep(0.0, 0.2, elevation));
    if(dawnDuskK > 0.0){
        vec3 dawnDir = normalize(vec3(sunDirWS.x, 0.0, sunDirWS.z));
        float glow = pow(max(0.0, dot(dir, dawnDir)), 10.0);
        sky += vec3(1.0, 0.3, 0.1) * glow * dawnDuskK;
    }

    // Stars (night only)
    float starVal = stars(dir);
    float starVis = 1.0 - smoothstep(-0.3, 0.0, elevation);
    sky += vec3(starVal) * starVis;

    // Sun disc + halo (angular)
    float cosTheta = clamp(dot(dir, sunDirWS), -1.0, 1.0);
    float aa = max(fwidth(cosTheta), 1e-5);
    float sunDisc = smoothstep(uCosSunAngularRadius - aa, uCosSunAngularRadius + aa, cosTheta);

    float cosHalo = cos(uSunHaloRadius);
    float halo = smoothstep(cosHalo, uCosSunAngularRadius, cosTheta);
    halo *= halo;

    vec3 sunColor  = mix(vec3(0.90, 0.82, 0.70), vec3(1.00, 1.00, 0.70), clamp(elevation * 0.6 + 0.5, 0.0, 1.0));
    vec3 haloColor = mix(vec3(1.00, 0.60, 0.25), sunColor, smoothstep(0.0, 0.25, elevation));
    vec3 sunGlow   = (sunDisc * sunColor * 6.0 + halo * haloColor * 1.5) * uSunIntensity;

    vec3 base = sky + sunGlow;

    // Directional fog blend for sky
    float fogK   = getSkyFogFactor(dir);
    vec3  fogCol = (fogParams.z > 0.5) ? fogColor4.rgb : dirLight.ambient;
    
    // Darken horizon fog at night to avoid unnatural glow
    // Use sun elevation to determine day/night
    float dayFactor = smoothstep(-0.1, 0.1, elevation);
    // At night (dayFactor=0), darken the fog significantly to match the dark sky
    fogCol *= (0.1 + 0.9 * dayFactor);
    
    vec3  finalC = mix(base, fogCol, fogK);

    // Underwater Override
    if (uIsUnderwater == 1) {
        // Underwater: skybox should not show gradient/stars/sun.
        // Use the shared fog color so it matches terrain/water fog and preserves day/night tint.
        finalC = (fogParams.z > 0.5) ? fogColor4.rgb : (dirLight.ambient * vec3(0.12, 0.32, 0.45));
    }

    // Subtle dithering
    finalC += (random(gl_FragCoord.xy) - 0.5) / 255.0;

    FragColor = vec4(finalC, 1.0);
}
