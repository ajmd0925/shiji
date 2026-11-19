"""Build smooth, localized animation from user-supplied transparent poses (Pillow + NumPy, maintenance only)."""
import argparse,json
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw,ImageFilter
parser=argparse.ArgumentParser();parser.add_argument('--sources',type=Path,default=Path(__file__).parent/'assets'/'animation-source');parser.add_argument('--output',type=Path,default=Path(__file__).parent/'assets');parser.add_argument('--preview',type=Path)
args=parser.parse_args();args.output.mkdir(parents=True,exist_ok=True)
N=512
frames={name:Image.open(args.sources/(name+'.png')).convert('RGBA') for name in ['idle-0','idle-1','idle-2','wave-0','wave-1','wave-2']}
base=frames['wave-0']
def pm(im):
 a=np.asarray(im,dtype=np.float32)/255;a[:,:,:3]*=a[:,:,3:];return a
def unpm(a):
 b=a.copy();b[:,:,:3]/=np.maximum(b[:,:,3:],1/255);b=np.clip(b*255,0,255).round().astype('uint8');b[b[:,:,3]==0]=0;return Image.fromarray(b)
def mask_poly(points,blur=5):
 m=Image.new('L',(N,N));ImageDraw.Draw(m).polygon(points,fill=255);return np.asarray(m.filter(ImageFilter.GaussianBlur(blur)),dtype=np.float32)[:,:,None]/255
arm_mask=mask_poly([(143,231),(177,219),(197,242),(225,262),(231,319),(220,362),(167,374),(143,333)],4)
# Common body, face, wings and hem never change when switching action states.
base_pm=pm(base)
y,x=np.mgrid[:N,:N].astype('float32')
def warp(im,delta,amount):
 xx=x+delta[:,:,0]*amount; yy=y+delta[:,:,1]*amount
 xx=np.clip(xx,0,N-1.001);yy=np.clip(yy,0,N-1.001)
 x0=xx.astype('int32');y0=yy.astype('int32');fx=(xx-x0)[:,:,None];fy=(yy-y0)[:,:,None]
 return (im[y0,x0]*(1-fx)+im[y0,x0+1]*fx)*(1-fy)+(im[y0+1,x0]*(1-fx)+im[y0+1,x0+1]*fx)*fy
# Landmark guided inverse-distance interpolation: moving hand/wrist, anchored shoulder and ROI perimeter.
def field(points_a,points_b):
 out=np.zeros((N,N,2),'float32');weights=np.zeros((N,N),'float32')
 for (ax,ay),(bx,by) in zip(points_a,points_b):
  w=1/((x-ax)**2+(y-ay)**2+45)**1.8
  out[:,:,0]+=w*(bx-ax);out[:,:,1]+=w*(by-ay);weights+=w
 return out/weights[:,:,None]
anchors=[(130,210),(210,215),(238,240),(240,320),(230,385),(140,385),(126,300),(223,270)]
pose_points=[[(163,296),(176,304),(185,336),(204,291)],[(167,240),(176,258),(187,305),(204,279)],[(163,239),(174,254),(188,303),(204,279)]]
def arm_between(a,b,t):
 if t==0: part=pm(frames['wave-'+str(a)])
 elif t==1:part=pm(frames['wave-'+str(b)])
 else:
  pa=pose_points[a]+anchors;pb=pose_points[b]+anchors
  # Backward sampling is evaluated around interpolated landmark locations.
  middle=[(u[0]+(v[0]-u[0])*t,u[1]+(v[1]-u[1])*t) for u,v in zip(pa,pb)]
  da=field(middle,pa);db=field(middle,pb)
  part=warp(pm(frames['wave-'+str(a)]),da,1)*(1-t)+warp(pm(frames['wave-'+str(b)]),db,1)*t
 return unpm(base_pm*(1-arm_mask)+part*arm_mask)
def blink_pose(index):
 result=base.copy();src=frames['idle-'+str(index)]
 # Map each eye separately; keep the canonical face/hair outline intact.
 for box,dst in [((190,192,249,241),(193,173,251,222)),((260,185,312,235),(264,160,315,210))]:
  patch=src.crop(box).resize((dst[2]-dst[0],dst[3]-dst[1]),Image.Resampling.LANCZOS)
  m=Image.new('L',patch.size);d=ImageDraw.Draw(m);d.ellipse((4,5,patch.width-4,patch.height-5),fill=255);m=m.filter(ImageFilter.GaussianBlur(2))
  result.paste(patch,dst[:2],m)
 return result
# Build 30 fps blink: short closure, longer opening, no full-body crossfade.
closed=blink_pose(2); half=blink_pose(1)
blink=[]
for i in range(16):
 t=i/15
 if t<.3: a,b,u=base,half,t/.3
 elif t<.45:a,b,u=half,closed,(t-.3)/.15
 elif t<.58:a,b,u=closed,closed,0
 elif t<.78:a,b,u=closed,half,(t-.58)/.2
 else:a,b,u=half,base,(t-.78)/.22
 u=u*u*(3-2*u);blink.append(Image.blend(a,b,u))
wave=[]
# Slow arm raise, two gentle waves, then smooth return; no changing face between states.
for a,b,count in [(0,1,15),(1,2,8),(2,1,8),(1,2,8),(2,1,8),(1,0,17)]:
 for i in range(count):
  t=i/(count-1);t=t*t*(3-2*t);wave.append(arm_between(a,b,t))
all_frames=[base]+blink+wave
# 320 square source cells cover all normal pet sizes through 200% DPI.
S=320;cols=9;atlas=Image.new('RGBA',(cols*S,((len(all_frames)+cols-1)//cols)*S))
small=[]
for i,f in enumerate(all_frames):
 f=f.resize((S,S),Image.Resampling.LANCZOS);f=f.filter(ImageFilter.UnsharpMask(radius=.55,percent=45,threshold=3));a=np.array(f);a[a[:,:,3]==0]=0;f=Image.fromarray(a);small.append(f);atlas.paste(f,((i%cols)*S,(i//cols)*S))
atlas.save(args.output/'dragon-girl-motion.png',optimize=True)
meta={'cell':S,'columns':cols,'frames':len(small),'fps':30,'blinkStart':1,'blinkCount':len(blink),'waveStart':1+len(blink),'waveCount':len(wave),'method':'localized landmark interpolation of user art; canonical frozen body and face; no AI-generated new poses'}
(args.output/'dragon-girl-motion.json').write_text(json.dumps(meta,indent=2)+'\n',encoding='utf8')
if args.preview:
 args.preview.mkdir(parents=True,exist_ok=True)
 seq=[base]*40+blink+[base]*18+wave+[base]*30
 for size in [160,256]:
  out=[]
  for i,f in enumerate(seq):
   f=f.resize((size,size),Image.Resampling.LANCZOS);bg=Image.new('RGB',(size*2,size),'#f5f1fb');bg.paste('#252331',(size,0,size*2,size));bg.paste(f,(0,0),f);bg.paste(f,(size,0),f);out.append(bg)
  out[0].save(args.preview/f'smooth-motion-{size}.webp',save_all=True,append_images=out[1:],duration=33,loop=0,quality=90)
 sheet=Image.new('RGB',(256*6,256*2),'#f5f1fb')
 for j,i in enumerate([0,4,8,12,15,16,17,24,31,39,55,80]):
  f=all_frames[i].resize((256,256),Image.Resampling.LANCZOS);sheet.paste(f,((j%6)*256,(j//6)*256),f)
 sheet.save(args.preview/'smooth-contact.png')
print(json.dumps(meta))
