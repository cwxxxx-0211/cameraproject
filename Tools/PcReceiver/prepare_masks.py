import argparse
import json
import shutil
from pathlib import Path

from PIL import Image


IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png"}
MASK_EXTENSIONS = {".jpg", ".jpeg", ".png"}


def list_images(directory, extensions):
    return sorted(
        path
        for path in directory.iterdir()
        if path.is_file() and path.suffix.lower() in extensions
    )


def copy_and_rename_masks(images_dir, original_masks_dir, masks_dir):
    image_files = list_images(images_dir, IMAGE_EXTENSIONS)
    mask_files = list_images(original_masks_dir, MASK_EXTENSIONS)

    if not image_files:
        raise RuntimeError(f"No images found in {images_dir}")
    if not mask_files:
        raise RuntimeError(f"No masks found in {original_masks_dir}")
    if len(image_files) != len(mask_files):
        raise RuntimeError(
            f"Image count ({len(image_files)}) and mask count ({len(mask_files)}) do not match"
        )

    masks_dir.mkdir(parents=True, exist_ok=True)
    copied_masks = []
    for image_path, mask_path in zip(image_files, mask_files):
        target_path = masks_dir / f"{image_path.stem}.png"
        shutil.copy2(mask_path, target_path)
        copied_masks.append(target_path)

    return image_files, copied_masks


def convert_masks_to_binary(masks):
    converted_count = 0
    for mask_path in masks:
        with Image.open(mask_path) as image:
            gray_image = image.convert("L")
            binary_image = gray_image.point(lambda pixel: 255 if pixel > 0 else 0)
            binary_image.save(mask_path)
            converted_count += 1
    return converted_count


def update_transforms_json(dataset_dir, masks_dir):
    transforms_path = dataset_dir / "transforms.json"
    if not transforms_path.exists():
        raise RuntimeError(f"transforms.json not found in dataset directory: {transforms_path}")

    with transforms_path.open("r", encoding="utf-8") as file:
        data = json.load(file)

    frames = data.get("frames")
    if not isinstance(frames, list):
        raise RuntimeError(f"Invalid transforms.json: missing frames list in {transforms_path}")

    masks_dir_name = masks_dir.name
    updated_count = 0
    for frame in frames:
        file_path = frame.get("file_path")
        if not file_path:
            continue

        image_stem = Path(file_path).stem
        frame["mask_path"] = f"{masks_dir_name}/{image_stem}.png"
        updated_count += 1

    with transforms_path.open("w", encoding="utf-8") as file:
        json.dump(data, file, indent=4)
        file.write("\n")

    return transforms_path, updated_count


def prepare_masks(dataset_dir, original_masks_dir):
    dataset_dir = dataset_dir.resolve()
    original_masks_dir = original_masks_dir.resolve()
    images_dir = dataset_dir / "images"
    masks_dir = dataset_dir / "masks"

    if not dataset_dir.is_dir():
        raise RuntimeError(f"dataset_dir is not a directory: {dataset_dir}")
    if not images_dir.is_dir():
        raise RuntimeError(f"images directory not found in dataset directory: {images_dir}")
    if not original_masks_dir.is_dir():
        raise RuntimeError(f"original_masks_dir is not a directory: {original_masks_dir}")

    image_files, copied_masks = copy_and_rename_masks(images_dir, original_masks_dir, masks_dir)
    converted_count = convert_masks_to_binary(copied_masks)
    transforms_path, updated_count = update_transforms_json(dataset_dir, masks_dir)

    return {
        "images": len(image_files),
        "masks": len(copied_masks),
        "converted": converted_count,
        "updated_frames": updated_count,
        "masks_dir": masks_dir,
        "transforms_path": transforms_path,
    }


def main():
    parser = argparse.ArgumentParser(
        description=(
            "Rename masks to match images, binarize them, and add mask_path to transforms.json."
        )
    )
    parser.add_argument(
        "dataset_dir",
        type=Path,
        help="Dataset directory containing images/ and transforms.json",
    )
    parser.add_argument(
        "original_masks_dir",
        type=Path,
        help="Directory containing original unordered mask images",
    )
    args = parser.parse_args()

    result = prepare_masks(args.dataset_dir, args.original_masks_dir)
    print(f"Images matched: {result['images']}")
    print(f"Masks written: {result['masks']} -> {result['masks_dir']}")
    print(f"Masks binarized: {result['converted']}")
    print(f"Frames updated: {result['updated_frames']} -> {result['transforms_path']}")


if __name__ == "__main__":
    main()
