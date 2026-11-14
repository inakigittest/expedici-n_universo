shader_type spatial;
// Llama procedural (sin texturas) para MultiMesh: cartel vertical que mira a la cámara (eje Y fijo).
// INSTANCE_CUSTOM: x = tamaño, y = fase, z = intensidad (0..1).
render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled;

varying float v_phase;
varying float v_heat;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float noise(vec2 p)
{
	vec2 i = floor(p), f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1, 0)), u.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), u.x), u.y);
}

void vertex()
{
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
		vec4(normalize(cross(vec3(0.0, 1.0, 0.0), INV_VIEW_MATRIX[2].xyz)), 0.0),
		vec4(0.0, 1.0, 0.0, 0.0),
		vec4(normalize(cross(INV_VIEW_MATRIX[0].xyz, vec3(0.0, 1.0, 0.0))), 0.0),
		MODEL_MATRIX[3]);
	MODELVIEW_NORMAL_MATRIX = mat3(MODELVIEW_MATRIX);
	VERTEX *= INSTANCE_CUSTOM.x;
	VERTEX.y += 0.5 * INSTANCE_CUSTOM.x;   // base de la llama sobre el suelo
	v_phase = INSTANCE_CUSTOM.y;
	v_heat = INSTANCE_CUSTOM.z;
}

void fragment()
{
	vec2 uv = UV;
	float t = TIME * 1.6 + v_phase * 10.0;
	float n = noise(vec2(uv.x * 4.0 + v_phase * 7.0, uv.y * 3.0 + t)) * 0.6 + noise(vec2(uv.x * 9.0, uv.y * 7.0 + t * 1.7)) * 0.4;
	float y = 1.0 - uv.y;                      // 0 abajo, 1 arriba
	float width = (1.0 - y) * 0.48 + 0.04;
	float dx = abs(uv.x - 0.5 + (n - 0.5) * 0.25 * y);
	float shape = smoothstep(width, width * 0.35, dx) * smoothstep(1.0, 0.55 - 0.25 * n, y + n * 0.25);
	float core = smoothstep(width * 0.5, 0.0, dx) * (1.0 - y);
	vec3 col = mix(vec3(1.0, 0.25, 0.02), vec3(1.0, 0.75, 0.2), core);
	col = mix(col, vec3(1.0, 0.95, 0.7), core * core);
	float flick = 0.75 + 0.25 * sin(TIME * 13.0 + v_phase * 31.0);
	ALBEDO = col * (0.6 + 0.8 * v_heat) * flick;
	ALPHA = clamp(shape, 0.0, 1.0);
}
