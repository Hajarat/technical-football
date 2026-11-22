# Arena textures and sky

Every file is CC0 (public domain). Downloaded 2026-11-22. Textures are the 1K JPG downloads, re-encoded as JPEG (quality 55 to 70, normals lowest) with `sips` to stay under 1 MB each; the normal maps are the OpenGL (GL) variants. Nothing else is shipped from these packs.

| Asset | URL | Licence | Downloaded | Files here |
| --- | --- | --- | --- | --- |
| Grass 001 (ambientCG) | https://ambientcg.com/a/Grass001 | CC0 | 2026-11-22 | `grass_color.jpg`, `grass_normal.jpg`, `grass_roughness.jpg` |
| Painted Metal 003 (ambientCG) | https://ambientcg.com/a/PaintedMetal003 | CC0 | 2026-11-22 | `boards_normal.jpg`, `boards_roughness.jpg` (the colour map is not used: the boards are a flat painted colour) |
| Concrete 034 (ambientCG) | https://ambientcg.com/a/Concrete034 | CC0 | 2026-11-22 | `surround_color.jpg`, `surround_normal.jpg`, `surround_roughness.jpg` |
| Kloofendal Overcast (Pure Sky) 1K HDRI (Poly Haven) | https://polyhaven.com/a/kloofendal_overcast_puresky | CC0 | 2026-11-22 | `../hdri/sky_overcast_1k.exr` |

The HDRI was downloaded as `kloofendal_overcast_puresky_1k.hdr` (1.17 MB) and re-saved as a half-float EXR with Godot (`Image.SaveExr`, same 1024x512 pixels) to fit the 1 MB limit.
