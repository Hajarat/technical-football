"""Builds the footballer character models with MPFB2 (MakeHuman for Blender) and exports them as glTF binary.

Run headless from the repository root (see game/assets/characters/SOURCES.md for the one-time MPFB2 and asset-pack install):

    /Applications/Blender.app/Contents/MacOS/Blender -b --python tools/characters/build_characters.py -- [--out game/assets/characters] [--preview <dir>]

Shirts, hair, eyes and the keeper gloves are CC0 MakeHuman assets; the shorts, socks and football boots are generated here from the body's own
surface (see author_kit). Output: player.glb (short sleeves) and keeper.glb (long sleeves, gloves).
"""
import math
import os
import sys

import addon_utils
import bmesh
import bpy
from mathutils import Matrix, Vector
from mathutils.geometry import intersect_point_line

ARGS = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []


def arg(name, default=None):
    return ARGS[ARGS.index(name) + 1] if name in ARGS else default


OUT_DIR = os.path.abspath(arg("--out", "game/assets/characters"))
PREVIEW_DIR = arg("--preview")
HEIGHT_M = 1.80
RIG = "mixamo"

# Role name -> .mhclo file basename (all CC0, see SOURCES.md). The keeper swaps the shirt and adds gloves. Shorts, socks and boots are authored (author_kit).
PLAYER_ASSETS = {
    "shirt": "elvs_crude_t-shirt_male.mhclo",
}
KEEPER_ASSETS = dict(PLAYER_ASSETS, shirt="toigo_fisherman_sweater.mhclo", gloves="toigo_gloves_short.mhclo")
BODYPARTS = {"eyes": "low-poly.mhclo", "eyebrows": "eyebrow001.mhclo", "hair": "short02.mhclo"}
# Athletic adult male: MPFB macro sliders (0..1).
MACRO = {"gender": 1.0, "age": 0.5, "muscle": 0.62, "weight": 0.42, "proportions": 0.55, "height": 0.62, "cupsize": 0.5, "firmness": 0.5,
         "race": {"asian": 0.1, "caucasian": 0.8, "african": 0.1}}


# Non-macro MPFB targets applied after the macro sliders: name -> weight (0..1). Lengthens the upper arm and forearm.
EXTRA_TARGETS = {"measure-upperarm-length-incr": 1.0, "measure-lowerarm-length-incr": 0.5, "measure-shoulder-dist-incr": 1.0}


def apply_extra_targets(base):
    from bl_ext.user_default.mpfb.services.targetservice import TargetService
    for name, weight in EXTRA_TARGETS.items():
        TargetService.load_target(base, TargetService.target_full_path(name), weight=weight, name=name)


def enable_mpfb():
    for mod in ("bl_ext.user_default.mpfb", "mpfb"):
        try:
            bpy.ops.preferences.addon_enable(module=mod)
            if mod in bpy.context.preferences.addons or addon_utils.check(mod)[1]:
                return mod
        except Exception:
            continue
    raise SystemExit("MPFB2 is not installed: see game/assets/characters/SOURCES.md")


def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def find_asset(subdir, name):
    from bl_ext.user_default.mpfb.services.assetservice import AssetService
    path = AssetService.find_asset_absolute_path(name, asset_subdir=subdir)
    if not path:
        raise SystemExit(f"MakeHuman asset not found: {subdir}/{name} (install the asset packs listed in SOURCES.md)")
    return path


def make_material(name, color, roughness=0.8, image=None):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    m.diffuse_color = color if not image else (0.9, 0.9, 0.9, 1)
    bsdf = m.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = color
    bsdf.inputs["Roughness"].default_value = roughness
    if image:
        tex = m.node_tree.nodes.new("ShaderNodeTexImage")
        tex.image = bpy.data.images.load(image)
        m.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    return m


def select_only(obj):
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj


def bake_shape_keys(obj):
    if obj.data.shape_keys is None:
        return
    select_only(obj)
    bpy.ops.object.shape_key_remove(all=True, apply_mix=True)


def body_height(base):
    """Height of the skin (vertex group "body", helper geometry excluded) in the evaluated mesh, in metres."""
    dg = bpy.context.evaluated_depsgraph_get()
    ev = base.evaluated_get(dg)
    mesh = ev.to_mesh()
    gi = base.vertex_groups["body"].index
    zs = [(base.matrix_world @ v.co).z for v in mesh.vertices if any(g.group == gi and g.weight > 0.5 for g in v.groups)]
    ev.to_mesh_clear()
    return max(zs) - min(zs)


def glove_bone(name, x):
    """Gloves may only follow the forearm and hand bones of their own side (left bones for x > 0)."""
    short = name.split(":")[-1]
    side = "Left" if x > 0 else "Right"
    return short.startswith(side + "Hand") or short == side + "ForeArm"


def inflate(obj, amount, hem_z=1.13, hem_extra=0.016):
    """Push a garment's vertices out along their normals so the layer underneath (shorts under a shirt hem) does not poke through; below hem_z
    the shirt hangs looser, by up to hem_extra more at its lowest edge."""
    for v in obj.data.vertices:
        loose = hem_extra * min(1.0, max(0.0, (hem_z - v.co.z) / 0.12)) if v.co.z < hem_z and abs(v.co.x) < 0.3 else 0.0
        v.co += v.normal * (amount + loose)


def clean_weights(rig, obj, allowed=None, max_dist=0.5):
    """MPFB interpolates a garment's weights from the body, and the glove's wrist vertices pick up half a weight from the clavicle, which stretches
    them into spikes when the arm moves. Drop influences the garment must not have (allowed(bone_name, x)) and any whose bone is farther than
    max_dist from the vertex (keeping the best remaining one if nothing is left), then normalise so every vertex sums to one."""
    bones = {b.name: (rig.matrix_world @ b.head_local, rig.matrix_world @ b.tail_local) for b in rig.data.bones}
    names = {vg.index: vg.name for vg in obj.vertex_groups if vg.name in bones}
    dropped = 0
    for v in obj.data.vertices:
        co = obj.matrix_world @ v.co
        infl = [(g.group, g.weight, (co - intersect_point_line(co, *bones[names[g.group]])[0]).length) for g in v.groups if g.group in names and g.weight > 0]
        if not infl:
            continue
        ok = [i for i in infl if i[2] <= max_dist and (allowed is None or allowed(names[i[0]], co.x))]
        keep = ok or [max(infl, key=lambda i: i[1])]
        dropped += len(infl) - len(keep)
        total = sum(w for _, w, _ in keep)
        for gi, w, _ in infl:
            if (gi, w) not in [(k[0], k[1]) for k in keep]:
                obj.vertex_groups[gi].remove([v.index])
        for gi, w, _ in keep:
            obj.vertex_groups[gi].add([v.index], w / total, "REPLACE")
    print(f"  weights cleaned on {obj.name}: {dropped} stray influences dropped")


# ---------------------------------------------------------------------------------------------------------------------
# Authored kit: shorts, socks and boots are cut from the body's own surface, pushed outward and keep the body's skin weights (they are
# copies of its faces, so every vertex already carries the right bone weights and the garment deforms exactly like the skin underneath).

WAIST_Z, HEM_Z = 1.03, 0.63          # shorts waistband (navel line) and hem (about 12 cm above the knee), metres, model at 1.80 m
SOCK_TOP_Z = 0.465                   # just below the knee (the knee joint is at 0.52)
SOLE_TOP_Z, SOLE_BOTTOM_Z = 0.009, -0.004
BOOT_UPPER_OFFSET, SOCK_OFFSET = 0.0075, 0.0035


def cut_region(base, name, keep_face, seed=None):
    """A copy of the skin keeping the faces for which keep_face(centre, normal) holds (and, if seed is given, only those connected to the
    face nearest to that point). The copy keeps the parent, the armature modifier and every vertex group of the skin."""
    ob = base.copy()
    ob.data = base.data.copy()
    ob.name = name
    base.users_collection[0].objects.link(ob)
    bm = bmesh.new()
    bm.from_mesh(ob.data)
    bm.faces.ensure_lookup_table()
    keep = {f for f in bm.faces if keep_face(f.calc_center_median(), f.normal)}
    if seed is not None:
        start = min(keep, key=lambda f: (f.calc_center_median() - Vector(seed)).length)
        seen, todo = {start}, [start]
        while todo:
            f = todo.pop()
            for e in f.edges:
                for g in e.link_faces:
                    if g in keep and g not in seen:
                        seen.add(g)
                        todo.append(g)
        keep = seen
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f not in keep], context="FACES")
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
    bm.to_mesh(ob.data)
    bm.free()
    return ob


def smooth(bm, verts, iterations, factor=0.5):
    for _ in range(iterations):
        bmesh.ops.smooth_vert(bm, verts=verts, factor=factor, use_axis_x=True, use_axis_y=True, use_axis_z=True)


def edit_mesh(ob, fn):
    bm = bmesh.new()
    bm.from_mesh(ob.data)
    fn(bm)
    bm.normal_update()
    bm.to_mesh(ob.data)
    bm.free()
    ob.data.update()


def author_shorts(base):
    """Loose football shorts: the pelvis and both thighs between the navel line and mid-thigh, pushed out 1 cm at the waist to 3.5 cm at the hem."""
    ob = cut_region(base, "shorts", lambda c, n: HEM_Z <= c.z <= WAIST_Z, seed=(0, 0, 0.95))

    def fn(bm):
        for v in bm.verts:   # straight waistband and hem
            if v.co.z > WAIST_Z - 0.03:
                v.co.z = WAIST_Z
            elif v.co.z < HEM_Z + 0.03:
                v.co.z = HEM_Z
        bm.normal_update()
        for v in bm.verts:   # outward offset, flaring toward the hem
            t = min(1.0, max(0.0, (WAIST_Z - v.co.z) / (WAIST_Z - HEM_Z)))
            v.co += v.normal * (0.008 + 0.012 * min(1.0, t * 1.6) + 0.015 * t * t)
        smooth(bm, list(bm.verts), 3, 0.4)
    edit_mesh(ob, fn)
    return ob


def author_socks(base):
    """Knee-high socks: the foot and shin up to just below the knee, 3.5 mm outside the skin."""
    ob = cut_region(base, "socks", lambda c, n: c.z <= SOCK_TOP_Z)

    def fn(bm):
        for v in bm.verts:
            if v.co.z > SOCK_TOP_Z - 0.03:
                v.co.z = SOCK_TOP_Z
        bm.normal_update()
        for v in bm.verts:
            v.co += v.normal * SOCK_OFFSET
    edit_mesh(ob, fn)
    return ob


def author_boots(base):
    """Low-cut football boots: the foot up to just under the ankle bone, a 7.5 mm upper with a slightly pointed toe, a thin sole (material slot 1)
    and six conical studs under each boot."""
    def top(y):   # collar height: just below the ankle bone at the heel and sides, rising over the instep
        return 0.078 + min(0.03, max(0.0, (-0.016 - y) * 0.5))
    ob = cut_region(base, "boots", lambda c, n: c.z < 0.14 and c.z <= top(c.y))
    sole_groups = {}

    def fn(bm):
        for v in bm.verts:   # an even collar line instead of the jagged edge the face selection leaves
            v.tag = v.is_boundary and v.co.z > 0.04
            if v.tag:
                v.co.z = top(v.co.y)
        bm.normal_update()
        for v in bm.verts:
            v.co += v.normal * BOOT_UPPER_OFFSET
        # the upper is the convex hull of each offset foot (no separate toes, smooth instep), open at the collar
        for side in (1, -1):
            verts = [v for v in bm.verts if v.co.x * side > 0.0]
            bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.verts[0] in verts], context="FACES_ONLY")
            hull = bmesh.ops.convex_hull(bm, input=verts, use_existing_faces=False)
            bmesh.ops.delete(bm, geom=hull["geom_interior"] + hull["geom_unused"], context="VERTS")
            bmesh.ops.delete(bm, geom=[f for f in hull["geom"] if isinstance(f, bmesh.types.BMFace) and all(v.tag for v in f.verts)], context="FACES")
        bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
        bmesh.ops.bisect_plane(bm, geom=list(bm.verts) + list(bm.edges) + list(bm.faces), plane_co=(0, 0, SOLE_TOP_Z), plane_no=(0, 0, 1))
        bm.normal_update()
        for v in bm.verts:   # flat sole
            if v.co.z < 0.0 and v.normal.z < -0.3:
                v.co.z = SOLE_BOTTOM_Z
        for f in bm.faces:
            f.material_index = 1 if f.calc_center_median().z < SOLE_TOP_Z + 0.002 else 0
            f.smooth = True
        # toe box: pull the last 4 cm of each boot forward a little for a pointed toe
        for side in (1, -1):
            mine = [v for v in bm.verts if v.co.x * side > 0.0]
            ymin = min(v.co.y for v in mine)
            for v in mine:
                k = max(0.0, 1.0 - (v.co.y - ymin) / 0.06)
                v.co.y -= 0.014 * k * k
        # studs: 2 at the heel, 4 under the ball of the foot, on each boot
        dl = bm.verts.layers.deform.verify()
        for side, bone in ((1, "Left"), (-1, "Right")):
            mine = [v for v in bm.verts if v.co.x * side > 0.0]
            ymin, ymax = min(v.co.y for v in mine), max(v.co.y for v in mine)
            xmid = sum(v.co.x for v in mine) / len(mine)
            gi = ob.vertex_groups["mixamorig:" + bone + "Foot"].index
            length = ymax - ymin
            for fy, dx in ((0.17, 0.016), (0.17, -0.016), (0.62, 0.022), (0.62, -0.022), (0.80, 0.016), (0.80, -0.016)):
                cx, cy = xmid + dx * side, ymax - fy * length
                m = Matrix.Translation((cx, cy, SOLE_BOTTOM_Z - 0.0055)) @ Matrix.Rotation(math.pi, 4, "X")
                res = bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=7, radius1=0.0075, radius2=0.0042, depth=0.011, matrix=m)
                for v in res["verts"]:
                    v[dl][gi] = 1.0
                for f in {f for v in res["verts"] for f in v.link_faces}:
                    f.material_index = 1
    edit_mesh(ob, fn)
    return ob


def author_kit(base, parts):
    parts["shorts"] = author_shorts(base)
    parts["socks"] = author_socks(base)
    parts["boots"] = author_boots(base)


def build(assets):
    from bl_ext.user_default.mpfb.services.humanservice import HumanService

    macro = dict(MACRO)
    lo, hi = 0.3, 1.0
    for _ in range(9):   # bisect the height slider until the skin is HEIGHT_M tall
        reset_scene()
        macro["height"] = 0.5 * (lo + hi)
        base = HumanService.create_human(mask_helpers=True, detailed_helpers=True, extra_vertex_groups=True, feet_on_ground=True, scale=0.1, macro_detail_dict=macro)
        apply_extra_targets(base)
        h = body_height(base)
        if abs(h - HEIGHT_M) < 0.004:
            break
        if h > HEIGHT_M:
            hi = macro["height"]
        else:
            lo = macro["height"]
    print(f"height slider {macro['height']:.3f} -> body {h:.3f} m")
    rig = HumanService.add_builtin_rig(base, RIG, import_weights=True)
    parts = {"skin": base}
    for kind, fname in BODYPARTS.items():
        parts[kind] = HumanService.add_mhclo_asset(find_asset(kind, fname), base, asset_type=kind, subdiv_levels=0, material_type="NONE")
    for role, fname in assets.items():
        parts[role] = HumanService.add_mhclo_asset(find_asset("clothes", fname), base, asset_type="clothes", subdiv_levels=0, material_type="NONE")
    return base, rig, parts


def finish(base, rig, parts):
    """Bake macro shape keys, drop the helper geometry, give every part one flat material named after its role (the game tints by name)."""
    for ob in parts.values():
        bake_shape_keys(ob)
    for role, ob in parts.items():
        if role not in ("skin", "eyes", "eyebrows", "hair"):
            clean_weights(rig, ob, glove_bone if role == "gloves" else None)
    inflate(parts["shirt"], 0.004)
    select_only(base)
    for mod in list(base.modifiers):
        if mod.type == "MASK":
            bpy.ops.object.modifier_apply(modifier=mod.name)
    author_kit(base, parts)
    eye_png = os.path.join(os.path.dirname(find_asset("eyes", "low-poly.mhclo")), "..", "materials", "brown_eye.png")
    mats = {
        "skin": make_material("skin", (0.8, 0.6, 0.46, 1), 0.55), "hair": make_material("hair", (0.13, 0.09, 0.06, 1), 0.7),
        "shirt": make_material("shirt", (1, 1, 1, 1), 0.85), "shorts": make_material("shorts", (1, 1, 1, 1), 0.85),
        "socks": make_material("socks", (1, 1, 1, 1), 0.9), "boots": make_material("boots", (1, 1, 1, 1), 0.45), "sole": make_material("sole", (0.08, 0.08, 0.09, 1), 0.6),
        "gloves": make_material("gloves", (1, 1, 1, 1), 0.55), "eyes": make_material("eyes", (1, 1, 1, 1), 0.3, os.path.normpath(eye_png)),
    }
    role_mat = {"eyebrows": "hair"}
    for role, ob in parts.items():
        ob.data.materials.clear()
        ob.data.materials.append(mats[role_mat.get(role, role)])
        if role == "boots":
            ob.data.materials.append(mats["sole"])
        if role in ("shorts", "socks"):
            ob.data.materials[0].use_backface_culling = False
    rig.data.pose_position = "REST"


def export(rig, parts, path):
    bpy.ops.object.select_all(action="DESELECT")
    rig.select_set(True)
    for ob in parts.values():
        ob.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.export_scene.gltf(
        filepath=path, export_format="GLB", use_selection=True, export_apply=False, export_yup=True, export_skins=True, export_animations=False,
        export_materials="EXPORT", export_image_format="JPEG", export_jpeg_quality=85, export_normals=True, export_texcoords=True, export_cameras=False, export_lights=False,
        export_extras=False, export_draco_mesh_compression_enable=False)
    print(f"wrote {path} ({os.path.getsize(path) / 1e6:.2f} MB)")


def preview(path):
    """Flat Workbench renders of the figure in rest pose (front, side, back, a close side view of the boot, front and back three-quarter views of the lower body)."""
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "MATERIAL"
    tint = {"shirt": (0.85, 0.1, 0.1, 1), "shorts": (0.9, 0.9, 0.9, 1), "socks": (0.85, 0.1, 0.1, 1), "boots": (0.1, 0.1, 0.1, 1), "sole": (0.9, 0.9, 0.2, 1), "gloves": (0.1, 0.9, 0.9, 1)}
    for m in bpy.data.materials:
        if m.name.split(".")[0] in tint:
            m.diffuse_color = tint[m.name.split(".")[0]]
    scene.render.resolution_x, scene.render.resolution_y = 600, 900
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    scene.collection.objects.link(cam)
    scene.camera = cam
    cam.data.type = "ORTHO"
    # name, camera position, look-at point, ortho scale (the body is centred on x = 0; the left foot is at x = 0.2)
    views = (("front", (0, -4, 0.9), (0, 0, 0.9), 2.1), ("side", (4, 0, 0.9), (0, 0, 0.9), 2.1), ("back", (0, 4, 0.9), (0, 0, 0.9), 2.1),
             ("foot", (4, 0, 0.12), (0, 0, 0.12), 0.5), ("foot34", (-3, -3, 0.4), (0.2, 0, 0.12), 0.7), ("legs_back34", (-2.5, 2.5, 0.8), (0, 0, 0.6), 1.4))
    for name, loc, target, ortho in views:
        cam.location = Vector(loc)
        cam.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()
        cam.data.ortho_scale = ortho
        scene.render.filepath = os.path.join(path, name + ".png")
        bpy.ops.render.render(write_still=True)


def main():
    enable_mpfb()
    os.makedirs(OUT_DIR, exist_ok=True)
    for label, assets in (("player", PLAYER_ASSETS), ("keeper", KEEPER_ASSETS)):
        base, rig, parts = build(assets)
        finish(base, rig, parts)
        if PREVIEW_DIR:
            os.makedirs(os.path.join(PREVIEW_DIR, label), exist_ok=True)
            preview(os.path.join(PREVIEW_DIR, label))
        export(rig, parts, os.path.join(OUT_DIR, label + ".glb"))


if __name__ == "__main__":
    main()
