shader_type spatial;
render_mode cull_disabled;

// The arena's grass: a tiled CC0 set (colour, normal, roughness) in world metres, a second sample at another scale and angle so
// the tiling is not visible from the high camera, and mowing stripes: bands along X that alternate in brightness.
uniform sampler2D albedo_tex : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap, repeat_enable;
uniform sampler2D rough_tex : hint_roughness_r, filter_linear_mipmap, repeat_enable;
uniform vec3 tint : source_color = vec3(1.0);
uniform float tile_m = 2.0;
uniform float stripe_m = 5.0;
uniform float stripe_amount = 0.06;
uniform float normal_strength = 0.6;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec2 uv = world_pos.xz / tile_m;
	vec2 uv2 = vec2(uv.x * 0.62 - uv.y * 0.41, uv.x * 0.41 + uv.y * 0.62) * 0.37 + 0.31;
	vec3 c = 0.5 * (texture(albedo_tex, uv).rgb + texture(albedo_tex, uv2).rgb);
	float band = mod(floor(world_pos.x / stripe_m), 2.0);
	float k = 1.0 + (band < 0.5 ? stripe_amount : -stripe_amount);
	ALBEDO = c * tint * k;
	ROUGHNESS = mix(texture(rough_tex, uv).r, 1.0, 0.3);
	vec3 n = texture(normal_tex, uv).xyz * 2.0 - 1.0;
	vec3 wn = normalize(vec3(n.x * normal_strength, max(n.z, 0.2), n.y * normal_strength));
	NORMAL = normalize((VIEW_MATRIX * vec4(wn, 0.0)).xyz);
}
