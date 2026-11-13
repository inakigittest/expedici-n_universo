// Edificios de la ciudad simulada (scripts/view/CityView.cs). Adaptado de building.gdshader del proyecto de mechas.
//  * COLOR = color de la pieza; UV2.x = material (0 normal · 1 franja de ventanas · 2 luz/letrero); UV2.y = semilla.
//  * Ventanas: las franjas se dividen en paños de ~4 m; de NOCHE una parte se enciende (luz cálida o fría).
//  * Letreros y luces: emisivos (más de noche).
//  * RECORTE TRANSPARENTE alrededor del vehículo: lo que queda entre la cámara y él se cala con un tramado.
shader_type spatial;
render_mode cull_back;

uniform float night = 0.0;
uniform vec3 player_pos = vec3(0.0);
uniform float cutaway_radius = 0.0;
uniform float units_per_meter = 0.1;

varying vec3 wpos;

const float BAYER[16] = float[](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);

float hash(vec3 p) {
	return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453);
}

void vertex() {
	wpos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	if (cutaway_radius > 0.0) {
		ivec2 b = ivec2(mod(FRAGCOORD.xy, 4.0));
		float bayer = BAYER[b.y * 4 + b.x] / 16.0;
		vec3 pv = (VIEW_MATRIX * vec4(player_pos, 1.0)).xyz;
		float d = length(VERTEX.xy - pv.xy) / cutaway_radius;
		if (VERTEX.z > pv.z + 0.3 && d < 1.0) {
			float keep = smoothstep(0.5, 1.0, d);
			if (bayer > keep * 0.85 + 0.1) {
				discard;
			}
		}
	}
	vec3 col = COLOR.rgb;
	vec3 emit = vec3(0.0);
	float mat = UV2.x;
	if (mat > 0.5 && mat < 1.5) {
		float m = 1.0 / units_per_meter;
		vec3 p = wpos * m;
		float pane = floor((p.x + p.z) / 4.0);
		float level = floor(p.y / 3.0);
		float h = hash(vec3(pane, level, UV2.y));
		float frame = step(0.82, fract((p.x + p.z) / 4.0));
		col = mix(col, col * 0.55, frame);
		if (night > 0.0 && h < 0.42 && frame < 0.5) {
			vec3 warm = mix(vec3(1.0, 0.78, 0.45), vec3(0.82, 0.9, 1.0), step(0.72, hash(vec3(level, UV2.y, 3.0))));
			emit = warm * (1.2 + 1.2 * h) * night;
			col = warm * 0.5;
		}
	} else if (mat > 1.5) {
		emit = col * (0.35 + 1.4 * night);
	}
	ALBEDO = col;
	EMISSION = emit;
	ROUGHNESS = 0.9;
}
