"""Writes expected.tsv from an INDEPENDENT extractor.

The oracle is 7-Zip 26.03 for zip, 7z and RAR, and Python's own gzip, bz2,
lzma and tarfile for the tar family - never Vaktari: a self-test whose
expectations came from the code under test would pass whatever that code did.
Run from Git Bash on Windows with 7z on PATH:

    python make-expected.py > expected.tsv

What the oracle cannot say is written by hand below and was checked against
each fixture's own listing (7z l, tar tv) when it was added: the summary
counts, the refusal sentences, and the landing rule (one top-level folder
lands as itself; anything else lands in a folder named after the archive).
tree.tar.zst and tree.tar.lz are frozen from the tar inside tree.tar.gz (see
PROVENANCE.md), so their rows are that tar's, under their own landing folder.
sc-tar.tar.lz and sc-tar.tar.zst are SharpCompress's own test archives: the
first decodes byte for byte to sc-tar.tar and the second to a tar holding the
same members with the same contents (both checked with SharpCompress's
decoders when they were vendored), so their rows are sc-tar.tar's.
multi.txt.lz is two lzip members written from the two texts below.
"""
import bz2, gzip, hashlib, io, lzma, os, shutil, subprocess, sys, tarfile, tempfile

sys.stdout.reconfigure(encoding="utf-8", newline="\n")
HERE = os.path.dirname(os.path.abspath(__file__))
SEVEN = shutil.which("7z") or r"C:\Program Files\7-Zip\7z.exe"
SKIP = {"expected.tsv", "make-fixtures.sh", "make-expected.py", "PROVENANCE.md"}

PASSWORD = "{} is password-protected \u2014 Vaktari cannot extract it yet"
REFUSED = {
    "7z-p.7z": PASSWORD, "7z-mhe.7z": PASSWORD,
    "zip-zipcrypto.zip": PASSWORD, "zip-aes256.zip": PASSWORD,
    "rar4-p.rar": PASSWORD, "rar4-hp.rar": PASSWORD,
    "rar5-p.rar": PASSWORD, "rar5-hp.rar": PASSWORD,
    "rar5-volume1.rar": "{} is one part of a split archive — Vaktari cannot extract split archives",
    # Its only entry is a PAX sparse member, left out; nothing is landed.
    "sparse-pax01.tar": "nothing in {} could be written: 1 left out (1 special file)",
    "sparse-gnu.tar": "{} holds a sparse file, which Vaktari cannot extract \u2014 nothing was extracted",
}

# files, folders, renamed, unsafe, links, special, mac, unwritable
TREE = (4, 2, 0, 0, 0, 0, 0, 0)       # readme.txt docs/{a.txt,b.bin,c.txt} empty/
RAR = (3, 3, 0, 0, 0, 0, 0, 0)        # тест.txt exe/test.exe jpg/test.jpg Empty/
WINZIP = (3, 2, 0, 0, 0, 0, 0, 0)     # тест.txt exe/test.exe jpg/test.jpg
ONE = (1, 0, 0, 0, 0, 0, 0, 0)
SUMMARY = {
    "7z-solid-lzma2.7z": TREE, "7z-bcj2.7z": TREE, "7z-ppmd.7z": TREE, "7z-bzip2.7z": TREE,
    "7z-delta.7z": TREE, "7z-arm64.7z": TREE, "7z-attribs.7z": TREE,
    "zip-deflate64.zip": TREE, "zip-bzip2.zip": TREE, "zip-lzma.zip": TREE, "zip-ppmd.zip": TREE,
    "zip-zstd.zip": WINZIP, "zip-xz.zip": WINZIP,
    "tree.tar.gz": TREE, "tree.tar.bz2": TREE, "tree.tar.xz": TREE, "tree.tar.zst": TREE, "tree.tar.lz": TREE,
    "bare.txt.gz": ONE, "bare.txt.xz": ONE,
    "rar4.rar": RAR, "rar5.rar": RAR, "rar4-solid.rar": RAR, "rar5-solid.rar": RAR,
    "v7.tar": ONE, "paxglobal.tar": ONE,
    # The name that is not UTF-8 arrives with U+FFFD, counted as renamed.
    "latin1-gnu.tar": (2, 0, 1, 0, 0, 0, 0, 0),
    "sc-tar.tar": (3, 3, 1, 0, 0, 0, 0, 0), "sc-tar.tar.lz": (3, 3, 1, 0, 0, 0, 0, 0),
    "sc-tar.tar.zst": (3, 3, 1, 0, 0, 0, 0, 0),
    "concat.txt.xz": ONE, "multi.txt.lz": ONE,
}

SUFFIXES = [".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.lz", ".7z", ".zip", ".rar", ".tar", ".gz", ".xz", ".lz"]

# The two members of multi.txt.lz, as the probe that froze it wrote them.
MULTI_LZ = (''.join('first member line %04d\n' % i for i in range(1, 401))
            + ''.join('second member line %04d\n' % i for i in range(1, 301))).encode()


def stem(name):
    for s in SUFFIXES:
        if name.lower().endswith(s):
            return name[: -len(s)] or "Archive"
    return name


def seven(fixture):
    tmp = tempfile.mkdtemp()
    try:
        subprocess.run([SEVEN, "x", "-y", "-bso0", "-bsp0", "-o" + tmp, os.path.join(HERE, fixture)], check=True)
        out = {}
        for dp, dn, fn in os.walk(tmp):
            for f in fn:
                p = os.path.join(dp, f)
                out[os.path.relpath(p, tmp).replace("\\", "/")] = open(p, "rb").read()
        return out
    finally:
        # 7z-attribs.7z lands a read-only file, which rmtree cannot delete.
        shutil.rmtree(tmp, onerror=lambda f, p, e: (os.chmod(p, 0o666), f(p)))


def untar(data):
    out = {}
    with tarfile.open(fileobj=io.BytesIO(data), errors="surrogateescape") as t:
        for m in t.getmembers():
            if m.isfile() and not m.issparse():
                # The BCL reader decodes names as UTF-8 and puts U+FFFD where
                # the bytes are not (a Known limit), and that is what lands.
                name = m.name.encode("utf-8", "surrogateescape").decode("utf-8", "replace")
                while name.startswith("./"):
                    name = name[2:]
                out[name] = t.extractfile(m).read()
    return out


def raw_tar(fixture):
    raw = open(os.path.join(HERE, fixture), "rb").read()
    if fixture.endswith(".gz"):
        return gzip.decompress(raw)
    if fixture.endswith(".bz2"):
        return bz2.decompress(raw)
    if fixture.endswith(".xz"):
        return lzma.decompress(raw)
    return raw


def files_of(fixture):
    if fixture == "multi.txt.lz":
        return {"multi.txt": MULTI_LZ}
    if fixture.startswith("bare.txt.") or fixture == "concat.txt.xz":
        # lzma.decompress reads every stream of a concatenated .xz.
        data = gzip.decompress(open(os.path.join(HERE, fixture), "rb").read()) if fixture.endswith(".gz") \
            else lzma.decompress(open(os.path.join(HERE, fixture), "rb").read())
        return {stem(fixture): data}
    if fixture in ("tree.tar.zst", "tree.tar.lz"):
        inner = untar(raw_tar("tree.tar.gz"))
    elif fixture in ("sc-tar.tar.zst", "sc-tar.tar.lz"):
        inner = untar(raw_tar("sc-tar.tar"))
    elif ".tar" in fixture:
        inner = {} if fixture == "sparse-pax01.tar" else untar(raw_tar(fixture))
    else:
        inner = seven(fixture)
    # Every fixture here holds more than one thing at its top level, so each
    # lands in a folder named after the archive; a single-folder archive is
    # pinned by the unit tests instead.
    return {stem(fixture) + "/" + p: b for p, b in inner.items()}


print("fixture\tkind\tpath\tsize\tsha256\tfiles\tfolders\trenamed\tunsafe\tlinks\tspecial\tmac\tunwritable\tsentence")
for fixture in sorted(os.listdir(HERE)):
    if fixture in SKIP:
        continue
    if fixture in REFUSED:
        print("\t".join([fixture, "refused"] + [""] * 11 + [REFUSED[fixture].format(fixture)]))
        continue
    files = files_of(fixture)
    for p in sorted(files):
        b = files[p]
        print("\t".join([fixture, "file", p, str(len(b)), hashlib.sha256(b).hexdigest()] + [""] * 9))
    print("\t".join([fixture, "summary", "", "", ""] + [str(n) for n in SUMMARY[fixture]] + [""]))
