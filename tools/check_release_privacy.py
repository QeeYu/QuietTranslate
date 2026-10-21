"""Check tracked source/history and optional release ZIPs without printing matched values.

Run: python tools/check_release_privacy.py --history --package path/to/release.zip
This is a release guard, not a guarantee that every possible secret can be detected.
"""
from pathlib import Path
import argparse
import hashlib
import io
import json
import re
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
RULES = {
    'github-token': rb'gh[pousr]_[A-Za-z0-9]{24,}|github_pat_[A-Za-z0-9_]{30,}',
    'ai-key': rb'\bsk-(?:proj-|ant-|or-v1-)?[A-Za-z0-9_-]{24,}',
    'google-key': rb'AIza[0-9A-Za-z_-]{30,}',
    'cloud-access-id': rb'\b(?:AKIA|ASIA)[A-Z0-9]{16}\b|\bAKID[A-Za-z0-9]{24,}',
    'private-key': rb'-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----',
    'slack-token': rb'xox[baprs]-[A-Za-z0-9-]{20,}',
    'literal-credential': rb'''(?i)(?:api[_-]?key|secret[_-]?key|access[_-]?token|authorization)["']?\s*[:=]\s*["'](?:Bearer )?([A-Za-z0-9+/=_-]{24,})["']''',
    'personal-profile-path': rb'(?i)[A-Z]:[\\/]Users[\\/](?!Public\b|Default\b|<)[A-Za-z0-9_.-]+[\\/]',
}
RULES = {name: re.compile(pattern) for name, pattern in RULES.items()}
FORBIDDEN = {'settings.json', 'hosts.yml', 'id_rsa', 'id_ed25519', 'cleanup.pending'}
# Official NuGet NAudio 2.2.1 binaries embed their author's build-machine PDB
# paths. Keep them intact; exempt only that rule for these exact binary hashes.
# Credential rules still inspect every byte, including trusted dependencies.
UPSTREAM_BUILD_PATH_HASHES = {
    'naudio.core.dll': 'fcf493fc47a2f478a65303886b975fbdbf714cbb1f2d79f7fce97e4bb16b01a8',
    'naudio.wasapi.dll': '618ef0e49d64e7a66dfe64bbf6ae81705b9d9683d8a9f321e5c3024d666bdf82',
}

def git(*args):
    return subprocess.check_output(['git', '-c', 'core.quotepath=false', *args], cwd=ROOT)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--history', action='store_true')
    parser.add_argument('--package', action='append', default=[])
    parser.add_argument('--report')
    args = parser.parse_args()
    findings = []; counts = {'workingFiles': 0, 'historyBlobs': 0, 'archiveEntries': 0, 'verifiedUpstreamBuildPaths': 0}

    def inspect(name, data, area):
        parts = Path(name).parts; base = Path(name).name.lower()
        private_json = base.endswith('.json') and base.startswith(('settings.', 'credentials', 'secrets'))
        if base in FORBIDDEN or private_json or base == '.env' or base.startswith('.env.') or Path(base).suffix in ('.pfx', '.p12', '.pem', '.key', '.qth', '.dmp', '.log'):
            findings.append({'area': area, 'file': name, 'rule': 'private-file'})
        if any(part.lower() in ('appdata', '.ssh', '.aws', '.azure', 'livehistory') for part in parts):
            findings.append({'area': area, 'file': name, 'rule': 'private-directory'})
        # Check UTF-8/plain bytes and ASCII literals stored as UTF-16 in managed binaries.
        for rule, pattern in RULES.items():
            if rule == 'personal-profile-path' and UPSTREAM_BUILD_PATH_HASHES.get(base) == hashlib.sha256(data).hexdigest():
                counts['verifiedUpstreamBuildPaths'] += 1
                continue
            for candidate in (data, data.replace(b'\x00', b'')):
                matches = list(pattern.finditer(candidate))
                if rule == 'literal-credential':
                    matches = [m for m in matches if not m.group(1).lower().startswith((b'synthetic', b'example', b'placeholder', b'dummy', b'test-'))]
                if matches:
                    findings.append({'area': area, 'file': name, 'rule': rule}); break

    names = git('ls-files', '--cached', '--others', '--exclude-standard', '-z').decode('utf-8').split('\0')
    for name in sorted(set(names) - {''}):
        path = ROOT / name
        if path.is_file(): counts['workingFiles'] += 1; inspect(name, path.read_bytes(), 'working-tree')
    if args.history:
        objects = {}
        for line in git('rev-list', '--objects', '--all').splitlines():
            oid, _, path = line.partition(b' ')
            if path: objects[oid] = path.decode('utf-8', errors='replace')
        batch = subprocess.run(['git', 'cat-file', '--batch'], input=b'\n'.join(objects) + b'\n', stdout=subprocess.PIPE, check=True, cwd=ROOT)
        stream = io.BytesIO(batch.stdout)
        while header := stream.readline():
            oid, kind, size = header.split(); data = stream.read(int(size)); stream.read(1)
            if kind == b'blob': counts['historyBlobs'] += 1; inspect(objects[oid], data, 'history:' + oid.decode()[:12])
    for archive in args.package:
        with zipfile.ZipFile(archive) as z:
            if z.testzip() is not None: raise ValueError('Invalid archive: ' + Path(archive).name)
            for item in z.infolist():
                if not item.is_dir(): counts['archiveEntries'] += 1; inspect(item.filename, z.read(item), 'archive:' + Path(archive).name)
    result = {'passed': not findings, 'counts': counts, 'findings': findings, 'secretValuesIncluded': False,
              'limits': 'Pattern scan; does not OCR media or guarantee detection of every provider format. UI media must use synthetic content.'}
    if args.report: Path(args.report).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if not findings else 1

if __name__ == '__main__': raise SystemExit(main())
