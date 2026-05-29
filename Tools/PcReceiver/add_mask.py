import json
import os
from pathlib import Path

# ================= 配置区 =================
# 你的原始 transforms.json 路径
INPUT_JSON = "/home/changweixian/cameraimage/nerf_processed6/transforms.json" 
# 生成的包含 mask 的新 JSON 路径
OUTPUT_JSON = "/home/changweixian/cameraimage/nerf_processed6/transforms_with_masks.json" 

# 你的 mask 文件夹名称
MASK_DIR = "masks" 
# 你的 mask 图片的扩展名 (通常是 .png 或 .jpg)
MASK_EXT = ".png"  
# ==========================================

def main():
    print(f"正在读取 {INPUT_JSON}...")
    try:
        with open(INPUT_JSON, 'r', encoding='utf-8') as f:
            data = json.load(f)
    except FileNotFoundError:
        print("错误：找不到输入的 JSON 文件，请检查路径。")
        return

    if "frames" not in data:
        print("错误：JSON 文件中没有找到 'frames' 列表。")
        return

    count = 0
    for frame in data["frames"]:
        if "file_path" in frame:
            # 获取原始图像路径，例如: "images/0001.jpg"
            img_path = Path(frame["file_path"])
            
            # 获取去除了后缀的文件名，例如: "0001"
            base_name = img_path.stem 
            
            # 组合成新的 mask 路径，例如: "masks/0001.png"
            mask_path = f"{MASK_DIR}/{base_name}{MASK_EXT}"
            
            # 添加到字典中
            frame["mask_path"] = mask_path
            count += 1

    # 保存新的 JSON 文件
    with open(OUTPUT_JSON, 'w', encoding='utf-8') as f:
        json.dump(data, f, indent=4)

    print(f"✅ 成功！已为 {count} 帧添加了 mask_path。")
    print(f"📁 新文件已保存至: {OUTPUT_JSON}")
    print("提示：在训练前，请将原文件重命名备份，然后把新文件改名为 transforms.json。")

if __name__ == "__main__":
    main()