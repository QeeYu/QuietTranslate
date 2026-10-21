"""Encode synthetic DocsCapture frames as a GIF. Development-only dependency: Pillow."""
import argparse
import json
from pathlib import Path

from PIL import Image


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('frames', type=Path, help='frames.json emitted by the test host')
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    manifest = json.loads(args.frames.read_text(encoding='utf-8'))
    if manifest.get('realCloudCalls') is not False or manifest.get('mockService') is not True:
        raise ValueError('Documentation must use synthetic responses')
    frames = []
    for item in manifest['frames']:
        with Image.open(args.frames.parent / item['file']) as source:
            frames.append(source.convert('RGB'))
    # One palette for the entire loop avoids colour flicker around text and selection.
    atlas = Image.new('RGB', (max(f.width for f in frames), sum(f.height for f in frames)), 'white')
    top = 0
    for frame in frames:
        atlas.paste(frame, (0, top))
        top += frame.height
    palette = atlas.quantize(colors=256)
    encoded = [frame.quantize(palette=palette, dither=Image.Dither.NONE) for frame in frames]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    encoded[0].save(args.output, save_all=True, append_images=encoded[1:],
                    duration=[item['duration'] for item in manifest['frames']], loop=0, disposal=2)
    with Image.open(args.output) as result:
        if result.n_frames != len(frames):
            raise ValueError('GIF frame count differs from capture manifest')
        print(f'{args.output.name}: {result.n_frames} frames, {result.width}x{result.height}')


if __name__ == '__main__':
    main()
