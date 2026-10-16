from pathlib import Path
import argparse
from PIL import Image, ImageDraw, ImageFont

parser=argparse.ArgumentParser()
parser.add_argument('--output',type=Path,default=Path(__file__).resolve().parent)
parser.add_argument('--preview',action='store_true',help='Also create a Windows-font contact sheet')
args=parser.parse_args();out=args.output;out.mkdir(parents=True,exist_ok=True)
S=4;N=256*S

class Wing:
    def __init__(self):
        self.im=Image.new('RGBA',(N,N));self.svg=[];self.defs=[]
    def path(self,cmds,fill):
        pts=[];current=(0,0);parts=[]
        for cmd,*v in cmds:
            parts.append(cmd+' '.join(str(n) for n in v))
            if cmd in ('M','L'):
                current=tuple(v);pts.append(current)
            elif cmd=='C':
                a=current;b=v[:2];c=v[2:4];e=v[4:6]
                for i in range(1,65):
                    t=i/64;u=1-t
                    pts.append(tuple(u**3*a[j]+3*u*u*t*b[j]+3*u*t*t*c[j]+t**3*e[j] for j in range(2)))
                current=tuple(e)
        mask=Image.new('L',(N,N));ImageDraw.Draw(mask).polygon([(round(x*S),round(y*S)) for x,y in pts],fill=255)
        if isinstance(fill,tuple):
            start,end=fill;layer=Image.new('RGBA',(N,N));d=ImageDraw.Draw(layer)
            rgb=lambda s:tuple(int(s[i:i+2],16) for i in (1,3,5))
            a,b=rgb(start),rgb(end)
            for y in range(N):
                t=y/(N-1);d.line((0,y,N,y),fill=tuple(round(x+(z-x)*t) for x,z in zip(a,b))+(255,))
            id=f'g{len(self.defs)}';self.defs.append(f'<linearGradient id="{id}" x1="0" y1="0" x2="0" y2="256" gradientUnits="userSpaceOnUse"><stop stop-color="{start}"/><stop offset="1" stop-color="{end}"/></linearGradient>');color=f'url(#{id})'
        else:
            layer=Image.new('RGBA',(N,N),fill);color=fill
        self.im.paste(layer,(0,0),mask)
        self.svg.append(f'<path d="{" ".join(parts)}" fill="{color}"/>')
    def save(self,name):
        im=self.im.resize((256,256),Image.Resampling.LANCZOS)
        im.save(out/(name+'.png'))
        im.save(out/(name+'.ico'),sizes=[(16,16),(20,20),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
        (out/(name+'.svg')).write_text('<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256"><defs>'+''.join(self.defs)+'</defs>'+''.join(self.svg)+'</svg>',encoding='utf-8')
        return im

def wing(paused):
    c=Wing()
    if not paused:
        # Speech bubble rising into a wing. The open white vein is the selection stroke.
        c.path([('M',28,136),('C',28,81,73,47,134,47),('C',179,47,211,37,234,20),('C',234,107,214,163,162,187),('C',139,198,114,199,91,190),('L',43,231),('L',50,177),('C',35,167,28,154,28,136),('Z',)],('#8996F1','#4A50AC'))
        c.path([('M',69,159),('C',107,124,163,110,203,63),('C',175,118,136,140,91,160),('L',67,187),('Z',)],'#F7FBFF')
        # The underside folds back into the bubble instead of an unrelated decoration.
        c.path([('M',107,159),('C',127,154,145,145,161,135),('C',167,144,171,152,173,161),('C',153,177,130,183,106,179),('C',110,173,111,166,107,159),('Z',)],('#C8FFF0','#79D4CC'))
    else:
        # Same wing tucked in: rounder resting silhouette and one closed eye.
        c.path([('M',28,139),('C',28,86,70,51,126,51),('C',160,51,184,68,199,82),('C',211,93,222,92,232,85),('C',228,137,205,169,165,187),('C',141,198,115,199,91,190),('L',43,231),('L',50,177),('C',35,167,28,155,28,139),('Z',)],('#9795D6','#605BA1'))
        # Feather vein curls into a tucked wing, keeping the active mark's lower diagonal.
        c.path([('M',69,159),('C',92,133,123,117,152,122),('C',171,125,180,139,174,151),('C',170,159,160,160,153,155),('C',163,141,150,136,137,141),('C',120,146,105,154,91,160),('L',67,187),('Z',)],'#F7F7FF')
        c.path([('M',107,159),('C',126,153,139,148,150,145),('C',144,160,153,171,169,169),('C',152,179,130,183,106,179),('C',110,173,111,166,107,159),('Z',)],('#E0E7FA','#B8BDD9'))
        # A curved closed eye, integrated into the folded tip rather than a status badge.
        c.path([('M',168,92),('C',177,102,190,108,204,104),('C',194,118,176,114,168,101),('Z',)],'#F7F7FF')
    return c.save('QuietTranslate-Paused' if paused else 'QuietTranslate')

normal,paused=wing(False),wing(True)
if args.preview:
    font=r'C:\Windows\Fonts\msyh.ttc'
    board=Image.new('RGB',(1180,1000),'#F1F2F8');d=ImageDraw.Draw(board)
    def text(x,y,s,size=20,color='#28334E'):
        d.text((x,y),s,font=ImageFont.truetype(font,size),fill=color)
    text(58,37,'轻翼 · 会收翅休息的翻译气泡',32)
    text(60,91,'正常时展翼，暂停时收翼休息。',19,'#788095')
    for i,(im,title,subtitle) in enumerate(((normal,'展翼 · 已就绪','羽翼向上舒展，羽脉化成一笔划词'),(paused,'收翼 · 已暂停','羽尖回落、羽脉卷起，闭眼安静休息'))):
        x=40+i*570
        d.rounded_rectangle((x,155,x+530,654),radius=28,fill='white')
        board.paste(im,(x+137,189),im)
        text(x+145,476,title,25)
        text(x+59,531,subtitle,19,'#788095')
        text(x+107,579,'同一片轻翼，不同的姿态',18,'#9A9FB0')
    text(60,697,'托盘实际尺寸  /  每组左侧正常，右侧暂停',19,'#788095')
    for dark,x in ((False,40),(True,610)):
        y=745
        d.rounded_rectangle((x,y,x+530,y+169),radius=20,fill='#E6E9F2' if not dark else '#252939')
        for j,size in enumerate((16,24,32)):
            px=x+65+j*160
            for state,im in enumerate((normal,paused)):
                small=im.resize((size,size),Image.Resampling.LANCZOS)
                board.paste(small,(px+state*48+(32-size)//2,y+38+(32-size)//2),small)
            text(px+12,y+103,f'{size} px',16,'#758198' if not dark else '#A6B1C7')
    text(60,949,'透明底 · 冰薄荷折面 × 鸢尾紫羽翼 · 小尺寸保留主要轮廓与开合变化',17,'#788095')
    board.save(out/'轻翼-正常与休息.png')
print('Created wing assets and state preview.')
