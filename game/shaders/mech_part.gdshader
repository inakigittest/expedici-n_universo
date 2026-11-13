// Capa de una pieza de mecha pre-renderizada (ver scripts/mechs/MechRig.cs).
//  * Billboard: el quad mira siempre a la cámara (ortográfica, mismo pitch que el pre-render).
//  * Profundidad por píxel: cada píxel escribe la profundidad real que tenía en el render 3D, así las piezas se tapan
//    entre sí píxel a píxel (un ala puede ir delante del hombro y detrás del torso a la vez) y el terreno tapa bien.
//  * scale agranda el mecha entero sin modelo nuevo (líderes enemigos); outline dibuja un contorno de 1 píxel de
//    pantalla de sprite alrededor de la silueta, empujado hacia atrás para que solo se vea por fuera del mecha.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque, shadows_disabled, fog_disabled;

uniform sampler2D albedo_tex : source_color, filter_nearest;
uniform sampler2D depth_tex : filter_nearest;   // R:G = 16 bits de profundidad relativa en [-range/2, range/2]
uniform vec4 region;            // celda en píxeles: x, y, ancho, alto
uniform vec2 sheet_size;        // tamaño de la hoja en píxeles
uniform vec2 offset_px;         // desplazamiento de la celda (píxeles, Y arriba), como Sprite3D.offset
uniform float pixel_size = 0.0155;
uniform float scale = 1.0;        // tamaño del mecha (1 = normal)
// por instancia (elementos del terreno que comparten material): tamaño y destello de impacto
instance uniform float inst_scale = 1.0;
instance uniform float inst_flash = 0.0;
// recorte transparente (ver building.gdshader): lo que tapa al jugador se cala con un tramado
global uniform vec3 player_pos;
global uniform float cutaway_radius;
const float CUT_BAYER[16] = float[](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);
uniform float depth_range = 4.0;
uniform float layer_depth = 0.0;  // desplazamiento de profundidad de la pieza por su socket (+ = más lejos)
uniform float depth_bias = 0.3;   // acerca todo el mecha a la cámara para no hundir los pies en el suelo
uniform vec4 tint : source_color = vec4(1.0);
uniform vec4 glow_color : source_color = vec4(1.0, 0.32, 0.08, 1.0);
uniform float glow = 0.0;         // 0..1: calor
uniform float flash = 0.0;        // 0..1: destello blanco de impacto
uniform vec4 outline_color : source_color = vec4(1.0, 0.1, 0.05, 1.0);
uniform float outline = 0.0;      // 0..1: intensidad del contorno (0 = sin contorno)
// Pintura (pilotos, tools/pilot25d.py): los píxeles con color clave (r = 255, g = 0, b = 255 - 8k; k = 8·material +
// tono) toman el color paint[7·material + tono]: 7 tonos de traje, 7 de ribete y 5 del visor.
uniform bool use_paint = false;
uniform vec4 paint[21] : source_color;

const float MARGIN = 1.0;         // píxeles de sprite añadidos alrededor de la celda para el contorno

void vertex() {
	vec2 size = region.zw + 2.0 * MARGIN;
	vec2 p = (VERTEX.xy * size + offset_px) * pixel_size * scale * inst_scale;
	VERTEX = vec3(p, 0.0);
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
	MODELVIEW_NORMAL_MATRIX = mat3(MODELVIEW_MATRIX);
}

// texel de la celda (px dentro de la región); fuera de la región = transparente
vec4 cell_texel(vec2 px, sampler2D tex) {
	if (px.x < 0.0 || px.y < 0.0 || px.x >= region.z || px.y >= region.w) {
		return vec4(0.0);
	}
	return texture(tex, (region.xy + px) / sheet_size);
}

vec3 linear_to_srgb(vec3 c) {
	return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));
}

// color clave → color de la pintura (si no es un color clave, se queda igual)
vec3 apply_paint(vec3 lin) {
	vec3 c = linear_to_srgb(lin);
	if (c.r < 0.99 || c.g > 0.01) {
		return lin;
	}
	int k = int(round((1.0 - c.b) * 255.0 / 8.0));
	int m = k / 8;
	int t = k - m * 8;
	if (m > 2 || t > 6) {
		return lin;
	}
	return paint[m * 7 + t].rgb;
}

float rel_depth(vec4 dz) {
	return (dz.r * 255.0 * 256.0 + dz.g * 255.0) / 65535.0 * depth_range - depth_range * 0.5;
}

void fragment() {
	if (cutaway_radius > 0.0) {
		vec3 pv = (VIEW_MATRIX * vec4(player_pos, 1.0)).xyz;
		float dc = length(VERTEX.xy - pv.xy) / cutaway_radius;
		if (VERTEX.z > pv.z + 0.6 && dc < 1.0) {
			ivec2 cb = ivec2(mod(FRAGCOORD.xy, 4.0));
			if (CUT_BAYER[cb.y * 4 + cb.x] / 16.0 > smoothstep(0.55, 1.0, dc) * 0.85 + 0.1) {
				discard;
			}
		}
	}
	vec2 px = UV * (region.zw + 2.0 * MARGIN) - MARGIN;
	vec4 c = cell_texel(px, albedo_tex);
	float rel;
	vec3 col;
	if (c.a < 0.5) {
		if (outline <= 0.0) {
			discard;
		}
		// contorno: algún vecino opaco → este píxel es borde; toma la profundidad del vecino, más atrás
		vec2 n = vec2(-1.0);
		if (cell_texel(px + vec2(1.0, 0.0), albedo_tex).a >= 0.5) n = px + vec2(1.0, 0.0);
		else if (cell_texel(px - vec2(1.0, 0.0), albedo_tex).a >= 0.5) n = px - vec2(1.0, 0.0);
		else if (cell_texel(px + vec2(0.0, 1.0), albedo_tex).a >= 0.5) n = px + vec2(0.0, 1.0);
		else if (cell_texel(px - vec2(0.0, 1.0), albedo_tex).a >= 0.5) n = px - vec2(0.0, 1.0);
		if (n.x < 0.0) {
			discard;
		}
		rel = rel_depth(cell_texel(n, depth_tex)) + 0.8;
		col = outline_color.rgb * (0.6 + 0.6 * outline);
	} else {
		rel = rel_depth(cell_texel(px, depth_tex));
		vec3 base = use_paint ? apply_paint(c.rgb) : c.rgb;
		col = mix(base * tint.rgb, glow_color.rgb, glow);
		col = mix(col, vec3(1.0), max(flash, inst_flash));
	}
	vec3 v = VERTEX;
	v.z -= (rel + layer_depth - depth_bias) * scale * inst_scale;   // en vista la cámara mira hacia -Z: más lejos = z más negativa
	vec4 clip = PROJECTION_MATRIX * vec4(v, 1.0);
	DEPTH = clip.z / clip.w;
	ALBEDO = col;
}
