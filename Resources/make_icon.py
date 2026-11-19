from PIL import Image, ImageDraw
import os

def make_gradient_triangle(size, color_top, color_bot):
    """Draw a gradient triangle at given size"""
    img = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    scale = size / 32.0
    pts = [(int(2 * scale),  int(3 * scale)),
           (int(30 * scale), int(3 * scale)),
           (int(16 * scale), int(29 * scale))]

    for y in range(size):
        t = y / size
        r = int(color_top[0] + t * (color_bot[0] - color_top[0]))
        g = int(color_top[1] + t * (color_bot[1] - color_top[1]))
        b = int(color_top[2] + t * (color_bot[2] - color_top[2]))
        draw.line([(0, y), (size, y)], fill=(r, g, b, 255))

    mask = Image.new('L', (size, size), 0)
    mdraw = ImageDraw.Draw(mask)
    mdraw.polygon(pts, fill=255)

    result = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    result.paste(img, (0, 0), mask)
    return result

# 监听中：紫色 #6366F1 → #818CF8
listening_top = (99, 102, 241)
listening_bot = (129, 140, 248)

# 暂停：红色 #EF4444 → #F87171
paused_top = (239, 68, 68)
paused_bot = (248, 113, 113)

sizes = [16, 32, 48, 256]
out_dir = r'D:\OneDrive\Desktop\工具库\肥仔\Vector\Resources'

# 紫色监听图标
listening_imgs = [make_gradient_triangle(s, listening_top, listening_bot) for s in sizes]
listening_imgs[0].save(os.path.join(out_dir, 'app.ico'), format='ICO',
                       sizes=[(s, s) for s in sizes])

# 红色暂停图标
paused_imgs = [make_gradient_triangle(s, paused_top, paused_bot) for s in sizes]
paused_imgs[0].save(os.path.join(out_dir, 'app_paused.ico'), format='ICO',
                     sizes=[(s, s) for s in sizes])

# 预览
listening_imgs[1].save(os.path.join(out_dir, 'app_listening_preview.png'))
paused_imgs[1].save(os.path.join(out_dir, 'app_paused_preview.png'))

print('生成完成:')
for name in ['app.ico', 'app_paused.ico', 'app_listening_preview.png', 'app_paused_preview.png']:
    size = os.path.getsize(os.path.join(out_dir, name))
    print(f'  {name}: {size} bytes')