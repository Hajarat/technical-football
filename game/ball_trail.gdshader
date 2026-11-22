shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, blend_mix, shadows_disabled;

// The shot trail's ribbon: u runs head (0) to tail (1), v across (-1..1). Vertex colour alpha is the opacity at that point.
// A soft scrolling noise roughens the edge so it reads as smoke.
uniform vec3 smoke : source_color = vec3(0.86, 0.87, 0.88);

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float vnoise(vec2 p) {
	vec2 i = floor(p), f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

void fragment() {
	float n = vnoise(vec2(UV.x * 7.0 + TIME * 1.5, UV.y * 2.5 + TIME * 0.6));
	float edge = 1.0 - smoothstep(0.35, 1.0, abs(UV.y) + (n - 0.5) * 0.7);
	ALBEDO = smoke;
	ALPHA = COLOR.a * edge * (0.7 + 0.5 * n);
}
