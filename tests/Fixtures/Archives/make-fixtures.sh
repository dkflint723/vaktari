#!/bin/sh
# Rebuilds the "M" fixtures in this folder (see PROVENANCE.md).
#
#   sh make-fixtures.sh 7z    Git Bash on Windows, with 7-Zip 26.03 x64 as 7z
#   sh make-fixtures.sh tar   WSL Fedora 44, GNU tar 1.35, gzip and bzip2
#
# The CONTENT is deterministic, and that is all expected.tsv relies on: it
# pins the files an archive extracts to and their SHA-256, never the bytes of
# the archive itself, which carry timestamps. Rebuilding therefore changes
# the archives and leaves expected.tsv true.
#
# tree.tar.zst and tree.tar.lz are not made here: neither zstd nor lzip is installed in
# the WSL image, so both are frozen from SharpCompress's own compressors (F in
# PROVENANCE.md). The xz pair is made by 7-Zip in the 7z step for the same
# reason.
set -e

here=$(cd "$(dirname "$0")" && pwd)
work=${TMPDIR:-/tmp}/vaktari-fixtures-$$
py=${PYTHON:-python3}
command -v "$py" >/dev/null 2>&1 || py=python

rm -rf "$work"
mkdir -p "$work/src"

# readme.txt, docs/a.txt, docs/b.bin (8 KiB of a seed-1 xorshift32, low byte
# of each step, which no coder can shrink), docs/c.txt (20 KiB of numbered
# lines, which every coder shrinks, so a zip really uses the method it names
# rather than falling back to Store) and an empty folder.
"$py" - "$work/src" <<'EOF'
import os, sys
root = sys.argv[1]
os.makedirs(os.path.join(root, "docs"))
os.makedirs(os.path.join(root, "empty"))
with open(os.path.join(root, "readme.txt"), "wb") as f:
    f.write(b"Vaktari archive fixture\n")
with open(os.path.join(root, "docs", "a.txt"), "wb") as f:
    f.write(b"alpha\n")
x = 1
out = bytearray()
for _ in range(8 * 1024):
    x ^= (x << 13) & 0xFFFFFFFF
    x ^= x >> 17
    x ^= (x << 5) & 0xFFFFFFFF
    out.append(x & 0xFF)
with open(os.path.join(root, "docs", "b.bin"), "wb") as f:
    f.write(bytes(out))
with open(os.path.join(root, "docs", "c.txt"), "wb") as f:
    f.write(b"".join(b"line %05d of the fixture text\n" % i for i in range(1, 691)))
EOF

case "$1" in
7z)
    cd "$work/src"
    rm -f "$here"/7z-*.7z "$here"/zip-*.zip
    z() { MSYS2_ARG_CONV_EXCL="-m;-p" 7z a -bso0 -bsp0 "$@"; }
    z -t7z -ms=on -m0=LZMA2 "$here/7z-solid-lzma2.7z" readme.txt docs empty
    z -t7z -m0=BCJ2 -m1=LZMA:d25 -m2=LZMA:d19 -m3=LZMA:d19 -mb0:1 -mb0s1:2 -mb0s2:3 "$here/7z-bcj2.7z" readme.txt docs empty
    z -t7z -m0=PPMd "$here/7z-ppmd.7z" readme.txt docs empty
    z -t7z -m0=BZip2 "$here/7z-bzip2.7z" readme.txt docs empty
    z -t7z -mf=Delta:4 "$here/7z-delta.7z" readme.txt docs empty
    z -t7z -mf=ARM64 "$here/7z-arm64.7z" readme.txt docs empty
    z -t7z -pTEST "$here/7z-p.7z" readme.txt docs empty
    z -t7z -pTEST -mhe=on "$here/7z-mhe.7z" readme.txt docs empty
    z -tzip -mm=Deflate64 "$here/zip-deflate64.zip" readme.txt docs empty
    z -tzip -mm=BZip2 "$here/zip-bzip2.zip" readme.txt docs empty
    z -tzip -mm=LZMA "$here/zip-lzma.zip" readme.txt docs empty
    z -tzip -mm=PPMd "$here/zip-ppmd.zip" readme.txt docs empty
    z -tzip -pTEST -mem=ZipCrypto "$here/zip-zipcrypto.zip" readme.txt docs empty
    z -tzip -pTEST -mem=AES256 "$here/zip-aes256.zip" readme.txt docs empty
    # The attribute fixture: 7-Zip records Hidden, System and ReadOnly, and
    # Extract all must set none of them.
    attrib.exe +H +S +R "$(cygpath -w "$work/src/readme.txt")"
    z -t7z "$here/7z-attribs.7z" readme.txt docs empty
    attrib.exe -H -S -R "$(cygpath -w "$work/src/readme.txt")"
    # The xz pair: a tar of the tree, and one bare file.
    tar --format=gnu --owner=0 --group=0 --numeric-owner -cf "$work/tree.tar" readme.txt docs empty
    rm -f "$here/tree.tar.xz" "$here/bare.txt.xz"
    7z a -txz -bso0 -bsp0 "$here/tree.tar.xz" "$work/tree.tar"
    cp readme.txt "$work/bare.txt"
    7z a -txz -bso0 -bsp0 "$here/bare.txt.xz" "$work/bare.txt"
    ;;
tar)
    cd "$work/src"
    t() { tar --format=gnu --owner=0 --group=0 --numeric-owner --sort=name "$@"; }
    t -cf "$work/tree.tar" readme.txt docs empty
    gzip -9 -n -c "$work/tree.tar" > "$here/tree.tar.gz"
    bzip2 -9 -c "$work/tree.tar" > "$here/tree.tar.bz2"
    gzip -9 -n -c readme.txt > "$here/bare.txt.gz"
    ;;
*)
    echo "usage: sh make-fixtures.sh 7z|tar" >&2
    exit 2
    ;;
esac

rm -rf "$work"
