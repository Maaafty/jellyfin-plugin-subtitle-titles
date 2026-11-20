#!/usr/bin/env python3
"""Build the linux/amd64 Jellyfin package, source companion and catalog manifest."""
import argparse
import hashlib
from concurrent.futures import ThreadPoolExecutor
import json
from datetime import datetime, timezone
import shlex
import shutil
import ssl
from pathlib import Path
from subprocess import run
from tempfile import TemporaryDirectory
from urllib.parse import quote, urlsplit
from zipfile import ZIP_DEFLATED, ZipFile
import tarfile
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
GUID = 'ee4b9a4b-740c-49f4-b4ea-72fd212fc51e'
BEST_REVISION = 'e12c65a915945e4c28e237a9b52bc4a8f39a0cec'
BEST_ASSETS = {
    'chi_sim.traineddata': '4fef2d1306c8e87616d4d3e4c6c67faf5d44be3342290cf8f2f0f6e3aa7e735b',
    'chi_tra.traineddata': '1aa60488574cafa69486d919284f079ca9b68fcc7f6ad8dc1ff1b318dfd97028',
    'eng.traineddata': '8280aed0782fe27257a68ea10fe7ef324ca0f8d85bd2fd145d1c2b560bcb66ba',
    'LICENSE': 'a6cba85bc92e0cff7a450b1d873c0eaa2e9fc96bf472df0247a26bec77bf3ff9',
}


def file_digest(path, algorithm):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, algorithm).hexdigest()


def catalog(archive, download_base, source_name):
    version = ET.parse(HERE / 'SubtitleTitles.csproj').findtext('.//Version') + '.0'
    return [{
        'guid': GUID, 'name': 'mReel Media Inspector', 'overview': 'mReel companion plugin for Jellyfin',
        'description': 'Identify and optionally rename subtitle tracks, and detect Dolby Vision on Blu-ray ISO for mReel without changing media files. Linux x86-64 only.',
        'owner': 'Mafty', 'category': 'Subtitles',
        'versions': [{'version': version, 'targetAbi': '10.11.11.0',
                      'sourceUrl': f'{download_base}/{quote(archive.name)}',
                      'checksum': file_digest(archive, lambda: hashlib.md5(usedforsecurity=False)),
                      'timestamp': datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
                      'changelog': f'Self-contained Linux x86-64 installer and dashboard. Corresponding dependency sources: {download_base}/{quote(source_name)}'}],
    }]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('download_base', help='Base URL that will serve the ZIP and corresponding source archive')
    parser.add_argument('--output', type=Path, default=ROOT / 'build/jellyfin-repository')
    parser.add_argument('--manifest-only', action='store_true', help='Point the existing, already-tested ZIP at a different host')
    args = parser.parse_args()
    base = args.download_base.rstrip('/')
    url = urlsplit(base)
    if url.scheme not in ('http', 'https') or not url.hostname or url.username or url.query or url.fragment:
        parser.error('download_base must be an HTTP(S) URL without credentials, query or fragment')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    version = ET.parse(HERE / 'SubtitleTitles.csproj').findtext('.//Version')
    name = f'mafty-subtitle-titles-{version}-linux-x64'
    archive = output / f'{name}.zip'
    source = output / f'{name}-dependency-sources.tar'
    if args.manifest_only:
        if not archive.is_file() or not source.is_file():
            parser.error('build the ZIP and source companion first')
        write_manifest(output, archive, source, base)
        return
    if archive.exists() or source.exists():
        parser.error('versioned archives are immutable; increment the version or build into a new --output directory')
    with TemporaryDirectory(prefix='package-', dir=output) as tmp:
        stage = Path(tmp)
        shutil.copyfile(builder_ca_bundle(), stage / 'builder-ca.pem')
        ffmpeg_source = stage / 'ffmpeg-7.1.3.tar.xz'
        run(['curl', '-fsSL', '--retry', '2', '--max-time', '60', '-o', str(ffmpeg_source),
             'https://ffmpeg.org/releases/ffmpeg-7.1.3.tar.xz'], check=True)
        if hashlib.sha256(ffmpeg_source.read_bytes()).hexdigest() != 'f0bf043299db9e3caacb435a712fc541fbb07df613c4b893e8b77e67baf3adbe':
            raise ValueError('FFmpeg source checksum mismatch')
        run(['dotnet', 'build', str(HERE / 'SubtitleTitles.csproj'), '-c', 'Release', '--artifacts-path', str(ROOT / 'build/jellyfin'), '-p:NuGetAudit=false'], check=True)
        run(['docker', 'run', '--rm', '--platform', 'linux/amd64', '-v', f'{stage}:/out', '-v',
             f'{HERE / "package-runtime.sh"}:/package-runtime.sh:ro', '-v', f'{HERE / "bitmap-ocr.cpp"}:/bitmap-ocr.cpp:ro',
             '-v', f'{stage / "builder-ca.pem"}:/builder-ca.pem:ro',
             'debian:bookworm-slim', '/bin/bash', '/package-runtime.sh'], check=True)
        model = stage / 'runtime/linux-x64/lid.176.ftz'
        run(['curl', '-fsSL', '--retry', '2', '--max-time', '60', '-o', str(model),
             'https://dl.fbaipublicfiles.com/fasttext/supervised-models/lid.176.ftz'], check=True)
        if hashlib.sha256(model.read_bytes()).hexdigest() != '8f3472cfe8738a7b6099e8e999c3cbfae0dcd15696aac7d7738a8039db603e83':
            raise ValueError('fastText language model checksum mismatch')
        (stage / 'runtime/linux-x64/licenses/fasttext-model.txt').write_text(
            'fastText language identification model (lid.176.ftz), Meta / Facebook Research\n'
            'https://fasttext.cc/docs/en/language-identification.html\n'
            'Creative Commons Attribution-ShareAlike 3.0: https://creativecommons.org/licenses/by-sa/3.0/\n',
            encoding='utf-8')
        install_best_models(stage / 'runtime/linux-x64')
        dll = ROOT / 'build/jellyfin/bin/SubtitleTitles/release/Jellyfin.Plugin.MaftySubtitleTitles.dll'
        build_zip(stage, stage / archive.name, dll)
        build_sources(stage, stage / source.name)
        publish_package(stage, output, archive.name, source.name, base)
    print(archive, source, output / 'manifest.json', sep='\n')


def builder_ca_bundle():
    configured = ssl.get_default_verify_paths().cafile
    candidates = [configured, '/etc/ssl/certs/ca-certificates.crt', '/etc/ssl/cert.pem',
                  '/etc/pki/tls/certs/ca-bundle.crt']
    for candidate in candidates:
        if not candidate:
            continue
        path = Path(candidate)
        if path.is_file():
            return path
    raise FileNotFoundError('No CA bundle found; set SSL_CERT_FILE to a trusted PEM bundle for the Docker builder')


def publish_package(stage, output, archive_name, source_name, base):
    manifest = stage / 'manifest.json'
    manifest.write_text(json.dumps(catalog(stage / archive_name, base, source_name), indent=2) + '\n')
    # All files are complete before publishing. Never overwrite versioned assets that
    # an existing manifest could reference; swap the manifest last, atomically.
    published = []
    try:
        for name in (archive_name, source_name):
            destination = output / name
            destination.hardlink_to(stage / name)
            published.append(destination)
        manifest.replace(output / manifest.name)
    except OSError:
        for destination in reversed(published):
            destination.unlink()
        raise


def install_best_models(runtime):
    models = runtime / 'tessdata-best'
    models.mkdir()

    def fetch(asset):
        filename, digest = asset
        path = runtime / 'licenses/tessdata-best.txt' if filename == 'LICENSE' else models / filename
        url = f'https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/{BEST_REVISION}/{filename}'
        run(['curl', '-fsSL', '--retry', '2', '--max-time', '60', '-o', str(path), url], check=True)
        with path.open('rb') as file:
            if hashlib.file_digest(file, 'sha256').hexdigest() != digest:
                raise ValueError(f'tessdata_best checksum mismatch: {filename}')

    with ThreadPoolExecutor(max_workers=len(BEST_ASSETS)) as pool:
        list(pool.map(fetch, BEST_ASSETS.items()))
    (runtime / 'licenses/tessdata-best-origin.txt').write_text(
        f'Tesseract OCR tessdata_best (unmodified Apache-2.0 models)\n'
        f'https://github.com/tesseract-ocr/tessdata_best/tree/{BEST_REVISION}\n', encoding='utf-8')


def build_zip(stage, archive, dll):
    with ZipFile(archive, 'w', ZIP_DEFLATED, compresslevel=6) as zipfile:
        zipfile.write(dll, dll.name)
        for file in sorted((stage / 'runtime').rglob('*')):
            if file.is_file():
                zipfile.write(file, file.relative_to(stage))


def build_sources(stage, source):
    entries = {}
    for line in (stage / 'source-uris').read_text().splitlines():
        if not line.startswith("'"):
            continue
        url, filename, size, checksum = shlex.split(line)
        if Path(filename).name != filename or urlsplit(url).hostname != 'deb.debian.org':
            raise ValueError(f'Unexpected Debian source URI: {line}')
        algorithm, digest = checksum.split(':', 1)
        if algorithm != 'SHA256':
            raise ValueError(f'Missing Debian SHA-256: {filename}')
        entries[filename] = (url, int(size), digest)
    if not entries:
        raise ValueError('No Debian source archives found')
    directory = stage / 'source-packages'
    directory.mkdir(exist_ok=True)

    def fetch(entry):
        filename, (url, size, checksum) = entry
        path = directory / filename
        if not path.is_file() or path.stat().st_size != size:
            run(['curl', '-fsSL', '--retry', '2', '--max-time', '210', '-o', str(path), url], check=True)
        with path.open('rb') as file:
            if path.stat().st_size != size or hashlib.file_digest(file, 'sha256').hexdigest() != checksum:
                raise ValueError(f'Debian source checksum mismatch: {filename}')
        return path

    with ThreadPoolExecutor(max_workers=8) as pool:
        paths = list(pool.map(fetch, sorted(entries.items())))
    with tarfile.open(source, 'w') as tar:
        for file in paths:
            tar.add(file, arcname=file.name)
        tar.add(stage / 'runtime/linux-x64/licenses/packages.tsv', arcname='packages.tsv')
        tar.add(stage / 'ffmpeg-7.1.3.tar.xz', arcname='ffmpeg-7.1.3.tar.xz')
        tar.add(HERE / 'package-runtime.sh', arcname='package-runtime.sh')
        tar.add(HERE / 'bitmap-ocr.cpp', arcname='bitmap-ocr.cpp')


def write_manifest(output, archive, source, base):
    with TemporaryDirectory(prefix='manifest-', dir=output) as tmp:
        manifest = Path(tmp) / 'manifest.json'
        manifest.write_text(json.dumps(catalog(archive, base, source.name), indent=2) + '\n')
        manifest.replace(output / manifest.name)
    print(archive, source, output / 'manifest.json', sep='\n')


if __name__ == '__main__':
    main()
