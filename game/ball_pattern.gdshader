shader_type spatial;

// A classic ball: 12 dark pentagons on white, found by the angle to the icosahedron's twelve vertices (object space, so the
// pattern turns with the mesh).
uniform vec3 dark : source_color = vec3(0.07, 0.07, 0.08);
uniform vec3 light : source_color = vec3(0.96, 0.96, 0.94);
uniform float pentagon_cos = 0.925;

varying vec3 dir;

void vertex() {
	dir = VERTEX;
}

void fragment() {
	const float phi = 1.61803399;
	vec3 d = normalize(dir);
	float best = -1.0;
	vec3 vs[12] = vec3[12](
		vec3(0.0, 1.0, phi), vec3(0.0, 1.0, -phi), vec3(0.0, -1.0, phi), vec3(0.0, -1.0, -phi),
		vec3(1.0, phi, 0.0), vec3(1.0, -phi, 0.0), vec3(-1.0, phi, 0.0), vec3(-1.0, -phi, 0.0),
		vec3(phi, 0.0, 1.0), vec3(-phi, 0.0, 1.0), vec3(phi, 0.0, -1.0), vec3(-phi, 0.0, -1.0));
	for (int i = 0; i < 12; i++) best = max(best, dot(d, normalize(vs[i])));
	float fw = max(fwidth(best), 1e-4);
	float m = smoothstep(pentagon_cos - fw, pentagon_cos + fw, best);
	ALBEDO = mix(light, dark, m);
	ROUGHNESS = 0.55;
}
