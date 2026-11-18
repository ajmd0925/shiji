from pathlib import Path
from PIL import Image, ImageDraw
out=Path(__file__).resolve().parent / 'assets'
source=Image.open(out / 'dragon-girl-v3.png').convert('RGBA')
# Crop to the head so the silhouette and purple eyes remain readable at tray size.
avatar=source.crop((175,12,598,435))
canvas=Image.new('RGBA',(256,256))
draw=ImageDraw.Draw(canvas)
draw.rounded_rectangle((5,5,251,251),radius=66,fill='#eee5fc',outline='#bda5e5',width=5)
avatar.thumbnail((244,244),Image.Resampling.LANCZOS)
canvas.alpha_composite(avatar,((256-avatar.width)//2,8))
canvas.save(out/'shiji-icon.png')
canvas.save(out/'shiji.ico',sizes=[(16,16),(20,20),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
