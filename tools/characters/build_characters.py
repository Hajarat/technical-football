"""Builds the footballer character models with MPFB2 (MakeHuman for Blender) and exports them as glTF binary.

Run headless from the repository root (see game/assets/characters/SOURCES.md for the one-time MPFB2 and asset-pack install):

    /Applications/Blender.app/Contents/MacOS/Blender -b --python tools/characters/build_characters.py -- [--out game/assets/characters] [--preview <dir>]

Only CC0 MakeHuman system assets are used. Output: player.glb (short sleeves) and keeper.glb (long sleeves, gloves).
"""
import math
import os
import sys

import addon_utils
import bpy
from mathutils import Vector
from mathutils.geometry import intersect_point_line

ARGS = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []


def arg(name, default=None):
    return ARGS[ARGS.index(name) + 1] if name in ARGS else default


OUT_DIR = os.path.abspath(arg("--out", "game/assets/characters"))
PREVIEW_DIR = arg("--preview")
HEIGHT_M = 1.80
RIG = "mixamo"

# Role name -> .mhclo file basename (all CC0, see SOURCES.md). The keeper swaps the shirt and adds gloves.
PLAYER_ASSETS = {
    "shirt": "elvs_crude_t-shirt_male.mhclo",
    "shorts": "cortu_jeans_shorts.mhclo",
    "socks": "joepal_crude_high_socks.mhclo",
    "boots": "toigo_ankle_boots_male.mhclo",
}
KEEPER_ASSETS = dict(PLAYER_ASSETS, shirt="toigo_fisherman_sweater.mhclo", gloves="toigo_gloves_short.mhclo")
BODYPARTS = {"eyes": "low-poly.mhclo", "eyebrows": "eyebrow001.mhclo", "hair": "short02.mhclo"}
# Athletic adult male: MPFB macro sliders (0..1).
MACRO = {"gender": 1.0, "age": 0.5, "muscle": 0.62, "weight": 0.42, "proportions": 0.55, "height": 0.62, "cupsize": 0.5, "firmness": 0.5,
         "race": {"asian": 0.1, "caucasian": 0.8, "african": 0.1}}


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


def inflate(obj, amount):
    """Push a garment's vertices out along their normals so the layer underneath (shorts under a shirt hem) does not poke through."""
    for v in obj.data.vertices:
        v.co += v.normal * amount


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


def build(assets):
    from bl_ext.user_default.mpfb.services.humanservice import HumanService

    macro = dict(MACRO)
    lo, hi = 0.3, 1.0
    for _ in range(9):   # bisect the height slider until the skin is HEIGHT_M tall
        reset_scene()
        macro["height"] = 0.5 * (lo + hi)
        base = HumanService.create_human(mask_helpers=True, detailed_helpers=True, extra_vertex_groups=True, feet_on_ground=True, scale=0.1, macro_detail_dict=macro)
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
    eye_png = os.path.join(os.path.dirname(find_asset("eyes", "low-poly.mhclo")), "..", "materials", "brown_eye.png")
    mats = {
        "skin": make_material("skin", (0.8, 0.6, 0.46, 1), 0.55), "hair": make_material("hair", (0.13, 0.09, 0.06, 1), 0.7),
        "shirt": make_material("shirt", (1, 1, 1, 1), 0.85), "shorts": make_material("shorts", (1, 1, 1, 1), 0.85),
        "socks": make_material("socks", (1, 1, 1, 1), 0.9), "boots": make_material("boots", (1, 1, 1, 1), 0.45),
        "gloves": make_material("gloves", (1, 1, 1, 1), 0.55), "eyes": make_material("eyes", (1, 1, 1, 1), 0.3, os.path.normpath(eye_png)),
    }
    role_mat = {"eyebrows": "hair"}
    for role, ob in parts.items():
        ob.data.materials.clear()
        ob.data.materials.append(mats[role_mat.get(role, role)])
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
    """Flat Workbench renders (front, side, back) of the figure in rest pose, for a quick look."""
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "MATERIAL"
    tint = {"shirt": (0.85, 0.1, 0.1, 1), "shorts": (0.9, 0.9, 0.9, 1), "socks": (0.85, 0.1, 0.1, 1), "boots": (0.1, 0.1, 0.1, 1), "gloves": (0.1, 0.9, 0.9, 1)}
    for m in bpy.data.materials:
        if m.name.split(".")[0] in tint:
            m.diffuse_color = tint[m.name.split(".")[0]]
    scene.render.resolution_x, scene.render.resolution_y = 600, 900
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    scene.collection.objects.link(cam)
    scene.camera = cam
    cam.data.type = "ORTHO"
    cam.data.ortho_scale = 2.1
    for name, loc, rot in (("front", (0, -4, 0.9), (math.radians(90), 0, 0)), ("side", (4, 0, 0.9), (math.radians(90), 0, math.radians(90))), ("back", (0, 4, 0.9), (math.radians(90), 0, math.radians(180)))):
        cam.location, cam.rotation_euler = loc, rot
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
