shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, blend_mix, shadows_disabled;

// The cage: a diamond-mesh net (u metres along the wall, v metres up) and the posts and rail that hold it (solid). Vertex colour
// holds the wall's outward normal (x in r, z in b, 0..1). Where the wall faces the camera (the near side) it lies between the
// camera and the pitch, so it fades to a faint veil and never blocks play.
uniform bool solid = false;
uniform vec3 colour : source_color = vec3(0.12, 0.13, 0.15);
uniform float opacity = 0.75;
uniform float cell_m = 0.10;
uniform float strand_m = 0.012;
uniform float near_side_opacity = 0.06;

varying vec3 out_normal;
varying vec3 world_pos;

void vertex() {
	out_normal = vec3(COLOR.r * 2.0 - 1.0, 0.0, COLOR.b * 2.0 - 1.0);
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

float strand(float a, float fw, float half_w) {
	float d = abs(fract(a + 0.5) - 0.5);
	return 1.0 - smoothstep(half_w - fw * 0.75, half_w + fw * 0.75, d);
}

void fragment() {
	float cover = 1.0;
	if (!solid) {
		float c = cell_m * 1.41421356;
		vec2 a = vec2(UV.x + UV.y, UV.x - UV.y) / c;
		vec2 fw = max(fwidth(a), vec2(1e-4));
		float half_w = strand_m / cell_m * 0.5;
		// at least a pixel wide, then at a distance the strands melt into an even veil instead of shimmering
		float w = max(half_w, 0.5 * max(fw.x, fw.y));
		float exact = max(strand(a.x, fw.x, w), strand(a.y, fw.y, w));
		float avg = clamp(4.0 * half_w, 0.0, 1.0);
		cover = mix(exact, avg, smoothstep(0.2, 0.55, max(fw.x, fw.y)));
	}
	vec3 to_cam = INV_VIEW_MATRIX[3].xyz - world_pos;
	float len = length(out_normal);
	float facing = len < 0.1 ? -1.0 : dot(out_normal / len, normalize(vec3(to_cam.x, 0.0, to_cam.z)));
	float near = smoothstep(-0.05, 0.3, facing);
	ALBEDO = colour;
	ALPHA = cover * opacity * mix(1.0, near_side_opacity, near);
}
