"""Build and privacy-check the light Windows release and current source (Python standard library only)."""
from pathlib import Path
import argparse
import hashlib
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
RUNTIME_FILES = (
    'QuietTranslate.exe', 'QuietTranslate.dll', 'QuietTranslate.deps.json',
    'QuietTranslate.runtimeconfig.json', 'Microsoft.Windows.SDK.NET.dll',
    'WinRT.Runtime.dll', 'NAudio.Core.dll', 'NAudio.Wasapi.dll',
)


def run(*args):
    subprocess.run(args, cwd=ROOT, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--output', type=Path, default=ROOT / 'dist')
    parser.add_argument('--no-restore', action='store_true')
    args = parser.parse_args()
    version = ET.parse(ROOT / 'QuietTranslate.csproj').findtext('.//Version')
    if not version or any(c not in '0123456789.' for c in version):
        raise ValueError('Invalid project version')
    output = args.output.resolve() / ('v' + version)
    output.mkdir(parents=True, exist_ok=True)
    guard = str(ROOT / 'tools/check_release_privacy.py')
    # Reject private source files before creating archives. Never copy the whole checkout.
    run(sys.executable, guard, '--history')
    (ROOT / 'artifacts').mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='release-', dir=ROOT / 'artifacts') as staging:
        staging = Path(staging)
        runtime = staging / 'runtime'
        command = [args.dotnet, 'publish', 'QuietTranslate.csproj', '-c', 'Release', '-r', 'win-x64',
                   '--self-contained', 'false', '-o', str(runtime), '-p:DebugType=None', '-p:DebugSymbols=false',
                   '-p:ContinuousIntegrationBuild=true', '-p:PathMap=' + str(ROOT) + '=/_/QuietTranslate']
        if args.no_restore:
            command.append('--no-restore')
        run(*command)
        light = staging / f'QuietTranslate-{version}-Light-x64.zip'
        source = staging / f'QuietTranslate-{version}-Source.zip'
        with zipfile.ZipFile(light, 'w', zipfile.ZIP_DEFLATED) as archive:
            for name in RUNTIME_FILES:
                archive.write(runtime / name, 'QuietTranslate/' + name)
            for name in ('README.md', 'THIRD-PARTY-NOTICES.md', '更新说明.md'):
                archive.write(ROOT / name, 'QuietTranslate/' + name)
            for folder, patterns in [('docs', ('*.md', 'images/*.png', 'images/*.gif')), ('Assets', ('*.svg',))]:
                for pattern in patterns:
                    for path in sorted((ROOT / folder).glob(pattern)):
                        archive.write(path, 'QuietTranslate/' + path.relative_to(ROOT).as_posix())
            archive.write(ROOT / 'local-ocr/本机OCR教程.md', 'QuietTranslate/local-ocr/本机OCR教程.md')
            archive.writestr('QuietTranslate/使用说明.md',
                f'# 轻译 {version} · Windows x64 轻量版\n\n'
                '需要 .NET 8 Desktop Runtime x64。先从旧版托盘选择“退出轻译”，解压后运行 QuietTranslate.exe。\n\n'
                '- 划词后点击“译”，或 Ctrl+Alt+Q；截图 OCR：Ctrl+Alt+W。\n'
                '- 输入栏：Alt+T 开关，Esc 关闭；总暂停：Pause。\n'
                '- 实时字幕：Ctrl+Alt+S 开关，保存设置不会自动采集。快捷键均可修改。\n'
                '- 密钥保存在 Windows 凭据管理器；语音记录关闭功能即删除。\n\n'
                '配置与操作见 [完整说明](docs/使用说明.md) 和 [语音字幕](docs/实时语音字幕.md)。\n'
                '本机 OCR 是可选独立服务，需下载源码包后按教程构建；主程序无需部署此服务也可使用云 OCR。\n')
        names = subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z'], cwd=ROOT).decode('utf-8').split('\0')
        excluded = {'.git', 'bin', 'obj', 'work', 'artifacts', 'dist', 'validation', 'livehistory'}
        with zipfile.ZipFile(source, 'w', zipfile.ZIP_DEFLATED) as archive:
            for name in sorted(set(names) - {''}):
                path = ROOT / name
                if path.is_file() and not excluded.intersection(part.lower() for part in Path(name).parts):
                    archive.write(path, 'QuietTranslate/' + name)
        run(sys.executable, guard, '--package', str(light), '--package', str(source))
        sums = []
        for archive in (light, source):
            sums.append(hashlib.sha256(archive.read_bytes()).hexdigest() + '  ' + archive.name)
        for archive in (light, source):
            archive.replace(output / archive.name)
        (output / 'SHA256SUMS.txt').write_text('\n'.join(sums) + '\n', encoding='utf-8')
    print('Release ready: ' + str(output))


if __name__ == '__main__':
    main()
