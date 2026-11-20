#!/bin/bash
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive LC_ALL=C
out=/out/runtime/linux-x64
mkdir -p "$out"/{bin,lib,opencc,tessdata,licenses}
# Bookworm's libraries run on Bookworm or newer; keep the target's own glibc.
. /etc/os-release
[[ $VERSION_CODENAME == bookworm && $(dpkg --print-architecture) == amd64 ]]
# Debian signs the package indices. Enable their source counterparts for binary redistribution.
cat >/etc/apt/sources.list.d/debian.sources <<'EOF'
Types: deb deb-src
URIs: https://deb.debian.org/debian
Suites: bookworm bookworm-updates
Components: main
Signed-By: /usr/share/keyrings/debian-archive-keyring.gpg

Types: deb deb-src
URIs: https://deb.debian.org/debian-security
Suites: bookworm-security
Components: main
Signed-By: /usr/share/keyrings/debian-archive-keyring.gpg
EOF
cat >/etc/apt/apt.conf.d/99-mafty-https <<'EOF'
Acquire::https::CaInfo "/builder-ca.pem";
Acquire::Retries "2";
EOF
apt-get -qq update
apt-get -yqq install --no-install-recommends ca-certificates opencc libbluray-dev libtesseract-dev tesseract-ocr-chi-sim tesseract-ocr-chi-tra tesseract-ocr-eng fasttext g++ make patch pkg-config xz-utils zlib1g-dev
install -m 755 /usr/bin/{opencc,fasttext} "$out/bin/"

# Build only local-file demuxers and bitmap decoders; no video decoding or external protocols.
tar -xJf /out/ffmpeg-7.1.3.tar.xz -C /tmp
ffmpeg=/tmp/ffmpeg-7.1.3
(
    cd "$ffmpeg"
    # FFmpeg 7.1.3's VobSub demuxer opens .sub through a fresh context that
    # ignores the caller's AVIO callbacks. Use the parent's callbacks and own
    # the companion explicitly: avformat_close_input would call avio_close.
    patch --batch --fuzz=0 -p1 <<'EOF'
--- a/libavformat/mpeg.c
+++ b/libavformat/mpeg.c
@@ -722,6 +722,7 @@
 typedef struct VobSubDemuxContext {
     const AVClass *class;
     AVFormatContext *sub_ctx;
+    AVIOContext *sub_pb;
     FFDemuxSubtitlesQueue q[32];
     char *sub_name;
 } VobSubDemuxContext;
@@ -741,7 +742,7 @@
     for (i = 0; i < s->nb_streams; i++)
         ff_subtitles_queue_clean(&vobsub->q[i]);
     avformat_close_input(&vobsub->sub_ctx);
-    return 0;
+    return ff_format_io_close(s, &vobsub->sub_pb);
 }
 
 static int vobsub_read_header(AVFormatContext *s)
@@ -786,6 +787,12 @@
 
     if ((ret = ff_copy_whiteblacklists(vobsub->sub_ctx, s)) < 0)
         return ret;
+
+    ret = s->io_open(s, &vobsub->sub_pb, vobsub->sub_name, AVIO_FLAG_READ, NULL);
+    if (ret < 0)
+        return ret;
+    vobsub->sub_ctx->pb = vobsub->sub_pb;
+    vobsub->sub_ctx->flags |= AVFMT_FLAG_CUSTOM_IO;
 
     ret = avformat_open_input(&vobsub->sub_ctx, vobsub->sub_name, iformat, NULL);
     if (ret < 0) {
EOF
    ./configure --prefix=/tmp/mafty-ffmpeg --disable-everything --disable-autodetect --disable-programs \
        --disable-doc --disable-network --disable-x86asm --disable-static --enable-shared \
        --disable-avdevice --disable-avfilter --disable-swscale --disable-swresample \
        --enable-protocol=file --enable-demuxer=matroska,mov,avi,mpegts,mpegps,sup,vobsub \
        --enable-zlib --enable-parser=dvbsub --enable-decoder=pgssub,dvdsub,dvbsub,xsub >/tmp/mafty-ffmpeg-configure
    make -s -j6 install
)
PKG_CONFIG_PATH=/tmp/mafty-ffmpeg/lib/pkgconfig g++ -std=c++20 -O2 -Wall -Wextra -Werror /bitmap-ocr.cpp \
    -o "$out/bin/bitmap-ocr" $(PKG_CONFIG_PATH=/tmp/mafty-ffmpeg/lib/pkgconfig pkg-config --cflags --libs libavformat libavcodec libavutil libbluray tesseract)
for name in avformat avcodec avutil; do
    for library in /tmp/mafty-ffmpeg/lib/lib"$name".so.*; do
        [[ $(basename "$library") =~ \.so\.[0-9]+$ ]] && cp -L "$library" "$out/lib/"
    done
done
cp "$ffmpeg/COPYING.LGPLv2.1" "$out/licenses/ffmpeg.txt"
cp -a /usr/share/opencc/. "$out/opencc/"
# Only the installed models used by this plugin are redistributed.
tessdata=$(dirname "$(find /usr/share/tesseract-ocr -name eng.traineddata -print -quit)")
cp "$tessdata"/{eng,chi_sim,chi_tra}.traineddata "$out/tessdata/"

packages=$(mktemp)
printf '%s\n' opencc libopencc-data tesseract-ocr-chi-sim tesseract-ocr-chi-tra tesseract-ocr-eng fasttext >"$packages"
LD_LIBRARY_PATH=/tmp/mafty-ffmpeg/lib ldd /usr/bin/opencc /usr/bin/fasttext "$out/bin/bitmap-ocr" > /tmp/mafty-ldd
if grep -F 'not found' /tmp/mafty-ldd; then exit 1; fi
awk '/=> \// {print $1, $3}' /tmp/mafty-ldd | sort -u | while read -r soname file; do
    # Dynamic loader and libc must come from the same runtime.
    [[ "$file" != /tmp/mafty-ffmpeg/* ]] || continue
    case "$soname" in libc.so.*|libm.so.*|libmvec.so.*|libdl.so.*|libpthread.so.*|librt.so.*|libresolv.so.*|libutil.so.*|ld-linux-*) continue ;; esac
    cp -L "$file" "$out/lib/$soname"
    owner=$(dpkg-query -S "$file" 2>/dev/null || dpkg-query -S "$(realpath "$file")" 2>/dev/null || dpkg-query -S "${file#/usr}" 2>/dev/null)
    printf '%s\n' "${owner%%: /*}" >>"$packages"
done
# Make exact corresponding sources available alongside the archive, not in Jellyfin's install ZIP.
: > "$out/licenses/packages.tsv"
while read -r package; do
    binary=${package%:amd64}
    cp -L "/usr/share/doc/$binary/copyright" "$out/licenses/$binary.txt"
    dpkg-query -W -f='${binary:Package}\t${Version}\t${source:Package}\t${source:Version}\n' "$package" >> "$out/licenses/packages.tsv"
done < <(sort -u "$packages")
mapfile -t sources < <(cut -f3,4 "$out/licenses/packages.tsv" | sort -u | tr '\t' '=')
apt-get -qq source --print-uris "${sources[@]}" > /out/source-uris
