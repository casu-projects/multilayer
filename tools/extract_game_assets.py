#!/usr/bin/env python3
import argparse
import json
import math
import struct
from pathlib import Path

import UnityPy
from PIL import Image, ImageOps
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator


def read_header(raw):
    file_id = struct.unpack_from("<i", raw, 16)[0]
    path_id = struct.unpack_from("<q", raw, 20)[0]
    length = struct.unpack_from("<I", raw, 28)[0]
    name = raw[32:32 + length].decode("utf-8", "replace") if 0 <= length < 512 else ""
    return file_id, path_id, name


def resolve_pptr(obj, ptr):
    file_id = ptr.get("m_FileID", 0)
    path_id = ptr.get("m_PathID", 0)
    if not path_id:
        return None
    if file_id == 0:
        target_file = obj.assets_file
    else:
        target_file = obj.assets_file.externals[file_id - 1].file
    for candidate in target_file.objects.values():
        if candidate.path_id == path_id:
            return candidate
    return None


def sprite_image(sprite):
    try:
        return sprite.image
    except Exception:
        return None


def atlas(images, columns, cell_width, cell_height, path):
    rows = max(1, math.ceil(len(images) / columns))
    canvas = Image.new("RGBA", (columns * cell_width, rows * cell_height), (0, 0, 0, 0))
    for index, image in enumerate(images):
        x = (index % columns) * cell_width
        y = (index // columns) * cell_height
        canvas.paste(image, (x, y))
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path)
    return canvas.size


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--game-root", default="assets/casualties-unknown")
    parser.add_argument("--out", default="multilayer/server/map/wwwroot/assets/game")
    args = parser.parse_args()

    game_root = Path(args.game_root).resolve()
    out = Path(args.out).resolve()
    out.mkdir(parents=True, exist_ok=True)

    env = UnityPy.load(str(game_root))
    objects = [obj for obj in env.objects]
    if not objects:
        raise RuntimeError("no Unity objects found")

    unity_version = objects[0].assets_file.unity_version
    generator = TypeTreeGenerator(unity_version)
    generator.load_local_game(str(game_root))

    def nodes(name):
        return generator.get_nodes_up("Assembly-CSharp", name)

    def read_wrapped(obj, tree):
        return obj.read_typetree(tree, wrap=True)

    scripts = {}
    for obj in objects:
        if obj.type.name != "MonoScript":
            continue
        try:
            script = obj.read()
            scripts[obj.path_id] = getattr(script, "m_ClassName", "")
        except Exception:
            pass

    tile_scripts = {pid for pid, name in scripts.items() if name in ("Tile", "RuleTile", "AnimatedTile")}
    world_script = next((pid for pid, name in scripts.items() if name == "WorldGeneration"), None)

    tile_objects = []
    for obj in objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            raw = obj.get_raw_data()
            _, script_id, _ = read_header(raw)
        except Exception:
            continue
        if script_id not in tile_scripts:
            continue
        try:
            _, _, tile_name = read_header(raw)
        except Exception:
            continue
        name_length = struct.unpack_from("<I", raw, 28)[0]
        sprite_offset = (32 + name_length + 3) & ~3
        sprite_file = struct.unpack_from("<i", raw, sprite_offset)[0]
        sprite_id = struct.unpack_from("<q", raw, sprite_offset + 4)[0]
        sprite_obj = None
        if sprite_file == 0:
            for candidate in obj.assets_file.objects.values():
                if candidate.path_id == sprite_id:
                    sprite_obj = candidate
                    break
        if sprite_obj is None:
            for candidate in objects:
                if candidate.path_id == sprite_id and candidate.type.name == "Sprite":
                    sprite_obj = candidate
                    break
        tile_objects.append((obj.path_id, tile_name, sprite_obj))

    tile_ids = {path_id for path_id, _, _ in tile_objects}
    block_sequence = []
    if world_script is not None:
        for obj in objects:
            if obj.type.name != "MonoBehaviour":
                continue
            try:
                raw = obj.get_raw_data()
                _, script_id, _ = read_header(raw)
            except Exception:
                continue
            if script_id != world_script:
                continue
            best = []
            for start in range(32, max(32, len(raw) - 12), 4):
                file_id = struct.unpack_from("<i", raw, start)[0]
                path_id = struct.unpack_from("<q", raw, start + 4)[0]
                if path_id not in tile_ids:
                    continue
                run = []
                offset = start
                while offset + 12 <= len(raw):
                    run_file = struct.unpack_from("<i", raw, offset)[0]
                    run_id = struct.unpack_from("<q", raw, offset + 4)[0]
                    if run_id in tile_ids and run_file in (0, 1, 2, 3):
                        run.append(run_id)
                        offset += 12
                    else:
                        break
                if len(run) > len(best):
                    best = run
            if len(best) > len(block_sequence):
                block_sequence = best
            break

    if len(block_sequence) < len(tile_objects) - 1:
        block_sequence = [path_id for path_id, _, _ in tile_objects]

    tiles_by_id = {path_id: (name, sprite_obj) for path_id, name, sprite_obj in tile_objects}
    block_entries = []
    block_images = []
    for index, path_id in enumerate(block_sequence):
        block_id = index + 1
        tile_name, sprite_obj = tiles_by_id[path_id]
        image = None
        sprite_name = ""
        if sprite_obj is not None:
            try:
                sprite = sprite_obj.read()
                sprite_name = getattr(sprite, "m_Name", "") or ""
                image = sprite_image(sprite)
            except Exception:
                image = None
        if image is None:
            continue
        block_entries.append({
            "block": block_id,
            "name": tile_name,
            "sprite": sprite_name,
            "image": image,
        })
        block_images.append(image)

    tile_width = max((entry["image"].width for entry in block_entries), default=1)
    tile_height = max((entry["image"].height for entry in block_entries), default=1)
    blocks_path = out / "blocks" / "atlas.png"
    atlas_size = atlas([entry["image"] for entry in block_entries], 8, tile_width, tile_height, blocks_path)
    for index, entry in enumerate(block_entries):
        entry["x"] = (index % 8) * tile_width
        entry["y"] = (index // 8) * tile_height
        entry["w"] = entry["image"].width
        entry["h"] = entry["image"].height
        del entry["image"]

    backgrounds = {
        "tint": "#808080",
        "blocksPerTile": 64,
        "entries": [],
    }
    background_names = [
        ("steelBackground", -1),
        ("rockBackground", 0),
        ("soilBackground", 1),
        ("sandBackground", 2),
        ("wastelandBackground", 3),
        ("grassBackground", 4),
        ("fungalBackground", 5),
    ]
    for name, depth in background_names:
        for obj in objects:
            if obj.type.name != "Sprite":
                continue
            try:
                sprite = obj.read()
            except Exception:
                continue
            if getattr(sprite, "m_Name", "") != name:
                continue
            image = sprite_image(sprite)
            if image is None:
                continue
            relative = f"backgrounds/{name}.png"
            path = out / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            image.save(path)
            backgrounds["entries"].append({
                "depth": depth,
                "name": name,
                "file": relative,
                "width": image.width,
                "height": image.height,
            })
            break

    head_prefixes = ("experimentHead", "experimentEye", "eyeGoneExpWound", "headMiddle", "headoutline")
    head_entries = []
    seen_heads = set()
    for obj in objects:
        if obj.type.name != "Sprite":
            continue
        try:
            sprite = obj.read()
        except Exception:
            continue
        name = getattr(sprite, "m_Name", "") or ""
        if not name or name in seen_heads:
            continue
        if not name.startswith(head_prefixes):
            continue
        image = sprite_image(sprite)
        if image is None:
            continue
        seen_heads.add(name)
        head_entries.append({"name": name, "image": image})
    head_entries.sort(key=lambda entry: entry["name"])
    head_width = max((entry["image"].width for entry in head_entries), default=1)
    head_height = max((entry["image"].height for entry in head_entries), default=1)
    heads_path = out / "heads" / "atlas.png"
    atlas([entry["image"] for entry in head_entries], 8, head_width, head_height, heads_path)
    for index, entry in enumerate(head_entries):
        entry["x"] = (index % 8) * head_width
        entry["y"] = (index // 8) * head_height
        entry["w"] = entry["image"].width
        entry["h"] = entry["image"].height
        del entry["image"]

    item_tree = nodes("Item")
    item_entries = []
    seen_items = set()
    for obj in objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            item = read_wrapped(obj, item_tree)
        except Exception:
            continue
        item_id = getattr(item, "id", "")
        if not item_id or item_id in seen_items:
            continue
        game_object = getattr(item, "m_GameObject", None)
        if game_object is None:
            continue
        try:
            go = game_object.read()
        except Exception:
            continue
        image = None
        sprite_name = ""
        for component in getattr(go, "m_Component", []):
            try:
                parsed = component.component.read()
            except Exception:
                continue
            if type(parsed).__name__ != "SpriteRenderer":
                continue
            sprite_ptr = getattr(parsed, "m_Sprite", None)
            if sprite_ptr is None:
                continue
            try:
                sprite = sprite_ptr.read()
                sprite_name = getattr(sprite, "m_Name", "") or ""
                image = sprite_image(sprite)
            except Exception:
                image = None
            if image is not None:
                break
        if image is None:
            continue
        seen_items.add(item_id)
        item_entries.append({"id": item_id, "sprite": sprite_name, "image": image})
    item_entries.sort(key=lambda entry: entry["id"])
    item_width = max((entry["image"].width for entry in item_entries), default=1)
    item_height = max((entry["image"].height for entry in item_entries), default=1)
    items_path = out / "items" / "atlas.png"
    atlas([entry["image"] for entry in item_entries], 16, item_width, item_height, items_path)
    for index, entry in enumerate(item_entries):
        entry["x"] = (index % 16) * item_width
        entry["y"] = (index // 16) * item_height
        entry["w"] = entry["image"].width
        entry["h"] = entry["image"].height
        del entry["image"]

    object_entries = []
    native_entries = []
    seen_objects = set()
    object_cell = 48
    object_columns = 16
    native_dir = out / "objects" / "native"
    native_dir.mkdir(parents=True, exist_ok=True)
    for index, obj in enumerate(objects):
        if obj.type.name != "Sprite":
            continue
        try:
            sprite = obj.read()
        except Exception:
            continue
        name = getattr(sprite, "m_Name", "") or ""
        if not name or name in seen_objects:
            continue
        image = sprite_image(sprite)
        if image is None:
            continue
        seen_objects.add(name)
        thumb = ImageOps.contain(image, (object_cell, object_cell), Image.Resampling.NEAREST)
        cell = Image.new("RGBA", (object_cell, object_cell), (0, 0, 0, 0))
        cell.paste(thumb, ((object_cell - thumb.width) // 2, (object_cell - thumb.height) // 2))
        object_entries.append({
            "name": name,
            "image": cell,
            "sourceWidth": image.width,
            "sourceHeight": image.height,
        })
        native_name = f"{index:04d}.png"
        image.save(native_dir / native_name)
        native_entries.append({
            "name": name,
            "file": f"objects/native/{native_name}",
            "width": image.width,
            "height": image.height,
            "pixelsPerUnit": getattr(sprite, "m_PixelsToUnits", 1.0) or 1.0,
        })
    object_entries.sort(key=lambda entry: entry["name"])
    object_rows = max(1, math.ceil(len(object_entries) / object_columns))
    object_atlas = Image.new("RGBA", (object_columns * object_cell, object_rows * object_cell), (0, 0, 0, 0))
    for index, entry in enumerate(object_entries):
        x = (index % object_columns) * object_cell
        y = (index // object_columns) * object_cell
        object_atlas.paste(entry["image"], (x, y))
        entry["x"] = x
        entry["y"] = y
        entry["w"] = object_cell
        entry["h"] = object_cell
        del entry["image"]
    (out / "objects").mkdir(parents=True, exist_ok=True)
    object_atlas.save(out / "objects" / "icons.png")
    (out / "objects" / "native.json").write_text(json.dumps(native_entries, ensure_ascii=False, indent=2), encoding="utf-8")

    manifest = {
        "generatedAt": __import__("datetime").datetime.utcnow().isoformat() + "Z",
        "unityVersion": unity_version,
        "tileSize": tile_width,
        "tileHeight": tile_height,
        "blocks": {
            "atlas": "blocks/atlas.png",
            "width": atlas_size[0],
            "height": atlas_size[1],
            "cellWidth": tile_width,
            "cellHeight": tile_height,
            "entries": block_entries,
        },
        "backgrounds": backgrounds,
        "heads": {
            "atlas": "heads/atlas.png",
            "cellWidth": head_width,
            "cellHeight": head_height,
            "entries": head_entries,
        },
        "items": {
            "atlas": "items/atlas.png",
            "cellWidth": item_width,
            "cellHeight": item_height,
            "entries": item_entries,
        },
        "objects": {
            "atlas": "objects/icons.png",
            "cellWidth": object_cell,
            "cellHeight": object_cell,
            "native": "objects/native.json",
            "entries": object_entries,
        },
    }
    (out / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"wrote {out}")
    print(f"blocks={len(block_entries)} backgrounds={len(backgrounds['entries'])} heads={len(head_entries)} items={len(item_entries)} objects={len(object_entries)}")


if __name__ == "__main__":
    main()
