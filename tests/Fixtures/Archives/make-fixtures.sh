#!/bin/sh
# Rebuilds the "M" fixtures in this folder (see PROVENANCE.md).
#
#   sh make-fixtures.sh 7z    Git Bash on Windows, with 7-Zip 26.03 x64 as 7z
#                             (it makes the zips; the 7z fixtures went when
#                             Extract all narrowed to zip and tar.gz)
#   sh make-fixtures.sh tar   WSL Fedora 44, GNU tar 1.35 and gzip
#
# The CONTENT is deterministic, and that is all expected.tsv relies on: it
# pins the files an archive extracts to and their SHA-256, never the bytes of
# the archive itself, which carry timestamps. Rebuilding therefore changes
# the archives and leaves expected.tsv true.
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
    rm -f "$here"/zip-*.zip
    z() { MSYS2_ARG_CONV_EXCL="-m;-p" 7z a -bso0 -bsp0 "$@"; }
    z -tzip -mm=Deflate64 "$here/zip-deflate64.zip" readme.txt docs empty
    z -tzip -mm=BZip2 "$here/zip-bzip2.zip" readme.txt docs empty
    z -tzip -mm=LZMA "$here/zip-lzma.zip" readme.txt docs empty
    z -tzip -mm=PPMd "$here/zip-ppmd.zip" readme.txt docs empty
    z -tzip -pTEST -mem=ZipCrypto "$here/zip-zipcrypto.zip" readme.txt docs empty
    z -tzip -pTEST -mem=AES256 "$here/zip-aes256.zip" readme.txt docs empty
    ;;
tar)
    cd "$work/src"
    t() { tar --format=gnu --owner=0 --group=0 --numeric-owner --sort=name "$@"; }
    t -cf "$work/tree.tar" readme.txt docs empty
    gzip -9 -n -c "$work/tree.tar" > "$here/tree.tar.gz"
    gzip -9 -n -c readme.txt > "$here/bare.txt.gz"
    ;;
*)
    echo "usage: sh make-fixtures.sh 7z|tar" >&2
    exit 2
    ;;
esac

rm -rf "$work"
