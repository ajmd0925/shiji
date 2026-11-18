# Optional asset maintenance: requires Pillow; not used at runtime or in normal builds.
from pathlib import Path
from PIL import Image, ImageFilter, ImageDraw, ImageFont
out=Path(__file__).resolve().parent / 'assets'
source=Image.open(out / 'dragon-girl-v3.png').convert('RGBA')
sizes=[72,90,96,108,120,126,144,150,160,162,168,180,192,200,210,216,240,252,270,280,288,300,320,336,360,384,400,420,480,560,640]
atlas=Image.new('RGBA',(2048,3072))
x=y=row=0
for size in sizes:
 if x+size>2048: x=0; y+=row; row=0
 assert y+size<=3072
 im=source.resize((size,size),Image.Resampling.LANCZOS)
 # Luminance sharpening leaves hue and alpha intact and avoids semi-transparent edges.
 rgb=im.convert('RGB').convert('YCbCr')
 l,cb,cr=rgb.split()
 sharp=l.filter(ImageFilter.UnsharpMask(radius=0.55,percent=38,threshold=3))
 interior=im.getchannel('A').filter(ImageFilter.MinFilter(3)).point(lambda a: 255 if a==255 else 0)
 l=Image.composite(sharp,l,interior)
 im2=Image.merge('YCbCr',(l,cb,cr)).convert('RGB').convert('RGBA'); im2.putalpha(im.getchannel('A'))
 atlas.paste(im2,(x,y))
 x+=size; row=max(row,size)
atlas.save(out/'dragon-girl-crisp-atlas.png')
print('Pre-rendered alpha-safe Lanczos atlas',atlas.size,'used height',y+row)

