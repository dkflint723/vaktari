# Archive fixtures: where each one came from

Every file here is extracted by `ArchiveSelfTest` (the `--self-test-archives`
switch, run by CI against the published binary on both platforms and by the
Fedora package against the installed one) and compared with `expected.tsv`.
`expected.tsv` is written by `make-expected.py` from an independent extractor —
7-Zip 26.03 and Python's own gzip/bz2/lzma/tarfile — never from Vaktari.

Sources:

- **M** — made here. `make-fixtures.sh 7z` (Git Bash, 7-Zip 26.03 x64, Windows 11) or
  `make-fixtures.sh tar` (WSL Fedora 44, GNU tar 1.35, gzip, bzip2), from a tree the
  script writes deterministically: `readme.txt`, `docs/a.txt`, `docs/b.bin` (8 KiB of a
  seed-1 xorshift32), `docs/c.txt` (20 KiB of numbered lines) and an empty `empty/`.
  The archives carry timestamps and are not byte-reproducible; their contents are.
- **T** — the tar oddities, made with GNU tar 1.35 under WSL by the refutation probe
  `archive-refute2a/mk.sh` (2026-09-26); its commands are repeated below.
- **V** — vendored from SharpCompress (MIT, Copyright (c) 2014 Adam Hathcock),
  `tests/TestArchives/Archives/` at commit `c083c6efd843a844b0c8f7878787360e815be781`,
  renamed as shown. SharpCompress's licence travels in THIRD-PARTY-NOTICES.txt.

Test password for every encrypted **M** fixture: `TEST`. Stage A only refuses them.

**The 7z, RAR, xz, bzip2, zstd and lzip fixtures (27 files, including every F one)
were removed when Extract all narrowed to zip and tar.gz;** git history at 0.11.3
has them and their rows.

| Fixture | Source | Made by / upstream | Bytes | SHA-256 |
|---|---|---|---|---|
| `bare.txt.gz` | M | `gzip -9 -n -c readme.txt` | 44 | `b45a061fe93fede65b29a0df0242532029f7d205a382bd311513c003d6284412` |
| `latin1-gnu.tar` | T | `tar --format=gnu -cf latin1-gnu.tar $(printf 'caf\351') small.txt` | 10240 | `ec98cc12d38ebb9561793637abcc70bd601c463b1a235cb587cc1f18902ded5e` |
| `paxglobal.tar` | T | `tar --format=pax --pax-option=comment=globalhello -cf paxglobal.tar small.txt` | 10240 | `e85dcd301f73d22890f3fb2ccb2a7a7187434afd7c05f258d583a73e339d4452` |
| `sc-tar.tar` | V | [`Tar.tar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Tar.tar) | 105472 | `787b8bf8217efd7f71e5367ac4b4673dc23ce805ad1fd93ba8eb8383a4cada77` |
| `sparse-gnu.tar` | T | `tar --format=gnu -S -cf sparse-gnu.tar big  (big = truncate -s 50M, then "tail" appended)` | 10240 | `44e0cadf8560e5cdcebcd72c2b1781dadea196f088066bc9200296f51853c8e8` |
| `sparse-pax01.tar` | T | `tar --format=pax -S --sparse-version=0.1 -cf sparse-pax01.tar big` | 10240 | `69b52108eb1f35c8f8328e5f9131c2822a9ca929e8696874063843e4f9b9dc14` |
| `tree.tar.gz` | M | `tar --format=gnu --owner=0 --group=0 --numeric-owner --sort=name -cf tree.tar ...; gzip -9 -n` | 10733 | `c895671b1b2a06570caa3c61778310d51b0f4894e3cbc8b3d16b053b1bfa4604` |
| `v7.tar` | T | `tar --format=v7 -cf v7.tar small.txt` | 10240 | `ba3b46a20faa210b1429f3dff3ed635359e3bcb0206855be7be60c3fc9265026` |
| `zip-aes256.zip` | M | `7z a -tzip -pTEST -mem=AES256` | 10336 | `3927b665f9ac7c0d9e7b2f80e568bc92c16b5ae26c1d40dc495d16dd50dee887` |
| `zip-bzip2.zip` | M | `7z a -tzip -mm=BZip2` | 9757 | `86fcd7cd280a722558f0d053967561ceeaaea708104a56cff449ad3b2049ff3c` |
| `zip-deflate64.zip` | M | `7z a -tzip -mm=Deflate64` | 10136 | `cf32cda6dbba60307bff4c0485843c9914d0eb0d91a2eb0738b2182edb145c34` |
| `zip-lzma.zip` | M | `7z a -tzip -mm=LZMA` | 9450 | `8884d498da08d1a4a1346539ea20f59af61af92051eb452d314123b5ccdcd36e` |
| `zip-ppmd.zip` | M | `7z a -tzip -mm=PPMd` | 9884 | `1a0919ee73852391f22295589913b19003bc48a770dff397515f59c402452e3e` |
| `zip-xz.zip` | V | [`WinZip27_XZ.zipx`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/WinZip27_XZ.zipx) | 59382 | `4a88881c8e5aa3c45d67991969023af3572c910602248061ce3b049080c6c069` |
| `zip-zipcrypto.zip` | M | `7z a -tzip -pTEST -mem=ZipCrypto` | 10184 | `c3352326869e09b59f2a1f41a0e3a81d166ddb65400816e1d14a2b24edb97eb0` |
| `zip-zstd.zip` | V | [`WinZip27_ZSTD.zipx`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/WinZip27_ZSTD.zipx) | 61482 | `d2bd5d90448b15a9c7450edbc7e5afae66d75482f5a48c74918c74898d8d75ea` |

The upstream SHA-256 of each **V** file equals the committed one: nothing was
modified on the way in, only renamed.
