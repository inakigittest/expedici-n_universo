shader_type spatial;
// Humo procedural para MultiMesh: carteles que suben y derivan con el viento, en bucle.
// INSTANCE_CUSTOM: x = tamaño, y = fase, z = altura máxima.
render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;

uniform vec2 wind = vec2(0.0, 0.0);
uniform float darkness = 0.35;
varying float v_life;
varying float v_phase;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float noise(vec2 p)
{
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

void vertex()
{
	float life = fract(TIME * 0.06 + INSTANCE_CUSTOM.y);
	vec4 origin = MODEL_MATRIX[3];
	origin.y += life * INSTANCE_CUSTOM.z;
	origin.xz += wind * life * INSTANCE_CUSTOM.z * 0.6;
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], origin);
	MODELVIEW_NORMAL_MATRIX = mat3(MODELVIEW_MATRIX);
	VERTEX *= INSTANCE_CUSTOM.x * (0.6 + 1.4 * life);
	v_life = life;
	v_phase = INSTANCE_CUSTOM.y;
}

void fragment()
{
	vec2 c = UV - 0.5;
	float r = length(c) * 2.0;
	float n = noise(UV * 3.0 + vec2(v_phase * 9.0, TIME * 0.2));
	float a = smoothstep(1.0, 0.2, r + (n - 0.5) * 0.5);
	ALBEDO = mix(vec3(0.16, 0.14, 0.13), vec3(0.55, 0.53, 0.5), v_life) * (1.0 - darkness * 0.5);
	ALPHA = a * 0.42 * smoothstep(0.0, 0.12, v_life) * (1.0 - v_life);
}
