import os
import glob
import shutil

# 1. 填入你的真实路径
images_dir = "/home/changweixian/cameraimage/nerf_processed6/images"  # 原图所在文件夹
original_masks_dir = "/home/changweixian/cameraimage/clean_masks2"         # 名字乱七八糟的旧掩膜文件夹
target_masks_dir = "/home/changweixian/cameraimage/nerf_processed6/masks" # 最终存放正确掩膜的新文件夹

# 创建必须严格命名为 masks 的文件夹
os.makedirs(target_masks_dir, exist_ok=True)

# 2. 获取并排序所有的图片和掩膜（按文件名的字母顺序排，确保一一对应）
# 假设你的原图是 png 或 jpg
image_files = sorted(glob.glob(os.path.join(images_dir, "*.png")) + glob.glob(os.path.join(images_dir, "*.jpg")))
mask_files = sorted(glob.glob(os.path.join(original_masks_dir, "*.png")))

print(f"检测到原图: {len(image_files)} 张")
print(f"检测到掩膜: {len(mask_files)} 张")

# 3. 严格的安全校验
if len(image_files) != len(mask_files):
    print("❌ 警告：原图数量和掩膜数量不一致！请检查是否漏抠了某些图，程序已停止。")
    exit()

# 4. 执行对齐复制
for img_path, mask_path in zip(image_files, mask_files):
    # 提取原图的纯名字，例如 "frame_00001.png"
    correct_name = os.path.basename(img_path)
    
    # 拼接新路径
    new_mask_path = os.path.join(target_masks_dir, correct_name)
    
    # 把掩膜复制过去，并强行改名为原图的名字
    shutil.copy(mask_path, new_mask_path)

print(f"🎉 批量重命名完成！所有掩膜已安全放入 {target_masks_dir} 目录中。")