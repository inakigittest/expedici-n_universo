// Capa de una pieza de mecha pre-renderizada (ver scripts/mechs/MechRig.cs).
//  * Billboard: el quad mira siempre a la cámara (ortográfica, mismo pitch que el pre-render).
//  * Profundidad por píxel: cada píxel escribe la profundidad real que tenía en el render 3D, así las piezas se tapan
//    entre sí píxel a píxel (un ala puede ir delante del hombro y detrás del torso a la vez) y el terreno tapa bien.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque, shadows_disabled, fog_disabled;

uniform sampler2D albedo_tex : source_color, filter_nearest;
uniform sampler2D depth_tex : filter_nearest;   // R:G = 16 bits de profundidad relativa en [-range/2, range/2]
uniform vec4 region;            // celda en píxeles: x, y, ancho, alto
uniform vec2 sheet_size;        // tamaño de la hoja en píxeles
uniform vec2 offset_px;         // desplazamiento de la celda (píxeles, Y arriba), como Sprite3D.offset
uniform float pixel_size = 0.0155;
uniform float depth_range = 4.0;
uniform float layer_depth = 0.0;  // desplazamiento de profundidad de la pieza por su socket (+ = más lejos)
uniform float depth_bias = 0.3;   // acerca todo el mecha a la cámara para no hundir los pies en el suelo
uniform vec4 tint : source_color = vec4(1.0);
uniform vec4 glow_color : source_color = vec4(1.0, 0.32, 0.08, 1.0);
uniform float glow = 0.0;         // 0..1: calor / impacto

void vertex() {
	vec2 p = (VERTEX.xy * region.zw + offset_px) * pixel_size;
	VERTEX = vec3(p, 0.0);
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
	MODELVIEW_NORMAL_MATRIX = mat3(MODELVIEW_MATRIX);
}

void fragment() {
	vec2 uv = (region.xy + UV * region.zw) / sheet_size;
	vec4 c = texture(albedo_tex, uv);
	if (c.a < 0.5) {
		discard;
	}
	vec4 dz = texture(depth_tex, uv);
	float rel = (dz.r * 255.0 * 256.0 + dz.g * 255.0) / 65535.0 * depth_range - depth_range * 0.5;
	vec3 v = VERTEX;
	v.z -= rel + layer_depth - depth_bias;   // en vista la cámara mira hacia -Z: más lejos = z más negativa
	vec4 clip = PROJECTION_MATRIX * vec4(v, 1.0);
	DEPTH = clip.z / clip.w;
	ALBEDO = mix(c.rgb * tint.rgb, glow_color.rgb, glow);
}
