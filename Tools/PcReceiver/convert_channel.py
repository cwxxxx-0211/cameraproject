import os
from PIL import Image
import glob

# ================= 配置区 =================
# 填入你实际存放 masks 图片的文件夹路径
MASK_DIR = "/home/changweixian/cameraimage/nerf_processed6/masks" 
# ==========================================

def main():
    print(f"正在检查 {MASK_DIR} 中的 Mask 图片并进行二值化处理...")
    
    # 找到所有的 png 和 jpg 图片
    search_pattern_png = os.path.join(MASK_DIR, "*.png")
    search_pattern_jpg = os.path.join(MASK_DIR, "*.jpg")
    mask_files = glob.glob(search_pattern_png) + glob.glob(search_pattern_jpg)
    
    if not mask_files:
        print("错误：没有找到任何 Mask 图片，请检查路径是否正确！")
        return

    converted_count = 0
    for file_path in mask_files:
        try:
            with Image.open(file_path) as img:
                # 1. 无论图片原先是什么模式，统一先转换为单通道灰度图 ('L')
                gray_img = img.convert('L')
                
                # 2. 真正的二值化核心逻辑
                # point() 方法会遍历每一个像素点 p
                # 只要像素值大于 0（即只要不是纯黑的背景），就强行拉满到 255（纯白）
                binary_img = gray_img.point(lambda p: 255 if p > 0 else 0)
                
                # 覆盖保存原文件
                binary_img.save(file_path)
                converted_count += 1
                
        except Exception as e:
            print(f"处理 {file_path} 时出错: {e}")

    print(f"✅ 转换完成！共处理并二值化了 {converted_count} 张图片。")
    print("现在你的 Mask 已经是纯粹的剪影了，可以重新运行 ns-train 训练命令了！")

if __name__ == "__main__":
    main()