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
- **F** — frozen from a generator: neither `zstd` nor `lzip` is installed in the WSL
  image, so these were compressed by SharpCompress 0.50.4's own compressors from the
  tar inside `tree.tar.gz` (E-30: both round-trip).
- **V** — vendored from SharpCompress (MIT, Copyright (c) 2014 Adam Hathcock),
  `tests/TestArchives/Archives/` at commit `c083c6efd843a844b0c8f7878787360e815be781`,
  renamed as shown. SharpCompress's licence travels in THIRD-PARTY-NOTICES.txt.

Test password for every encrypted **M** fixture: `TEST`. The **V** encrypted RAR
fixtures use SharpCompress's own test password, `test`. Stage A only refuses them.

| Fixture | Source | Made by / upstream | Bytes | SHA-256 |
|---|---|---|---|---|
| `7z-arm64.7z` | M | `7z a -t7z -mf=ARM64` | 8979 | `c450b35282cebefc48a0e478cca36ad92bf44390464d73756da9268beaaf902a` |
| `7z-attribs.7z` | M | `7z a -t7z, readme.txt marked +H +S +R first` | 8975 | `7416668f5f65048d543cf3f38fe45965a0e76ec0b51e21d9e22a7db3eb7388bb` |
| `7z-bcj2.7z` | M | `7z a -t7z -m0=BCJ2 -m1=LZMA:d25 -m2=LZMA:d19 -m3=LZMA:d19 -mb0:1 -mb0s1:2 -mb0s2:3` | 9006 | `d62d1aff2376639d3a731889541d6acb9a14b0f43f587842972c9718505ad75d` |
| `7z-bzip2.7z` | M | `7z a -t7z -m0=BZip2` | 10071 | `fa47653b5694b692429509cdb72697fb5994e2ec6c7e17e65ddde760db3d3f6d` |
| `7z-delta.7z` | M | `7z a -t7z -mf=Delta:4` | 9644 | `cf0dfc91dbad58be4d4a1088a1155e24aa5404b638197e58a29a242a54c78f10` |
| `7z-mhe.7z` | M | `7z a -t7z -pTEST -mhe=on` | 9039 | `1ba5880bc699ddd57c1b2f781060985e0dff8aa079ce742b5d14e6dca1848c08` |
| `7z-p.7z` | M | `7z a -t7z -pTEST` | 9009 | `75ea74b03896f04af3b98b035be98ae798a8db8b6373ec67e3f8982d21088ea4` |
| `7z-ppmd.7z` | M | `7z a -t7z -m0=PPMd` | 9765 | `95b9e99c5eb148ac133538325d33db0f3b288f0323b0d02521e681764016c94b` |
| `7z-solid-lzma2.7z` | M | `7z a -t7z -ms=on -m0=LZMA2` | 8974 | `4be3de4e1fa871e377e0f6c0b79e4d9de602467d026f4ef59867bd06397dc3a9` |
| `bare.txt.gz` | M | `gzip -9 -n -c readme.txt` | 44 | `b45a061fe93fede65b29a0df0242532029f7d205a382bd311513c003d6284412` |
| `bare.txt.xz` | M | `7z a -txz of readme.txt copied to bare.txt` | 76 | `1a5bdf68f61b7b7fd14ac0e530c8052e666026a0bfc9a175b630a034ea35e11e` |
| `concat.txt.xz` | M | `Python 3.11 lzma.compress(a) + lzma.compress(b): two xz streams, a = 'first stream line NNNN' x400, b = 'second stream line NNNN' x300` | 776 | `539c675c00d355e6b64bec5156a348956b74226e5d340589ff83bd1c0e627349` |
| `latin1-gnu.tar` | T | `tar --format=gnu -cf latin1-gnu.tar $(printf 'caf\351') small.txt` | 10240 | `ec98cc12d38ebb9561793637abcc70bd601c463b1a235cb587cc1f18902ded5e` |
| `multi.txt.lz` | F | two members, each LZipStream.Create(file, Compress): 'first member line NNNN' x400, then 'second member line NNNN' x300 (the texts are repeated in make-expected.py) | 742 | `873a1672cb2e7ae5585267e9206edfbe1545b623a8377837f8d8b89268b8aaed` |
| `paxglobal.tar` | T | `tar --format=pax --pax-option=comment=globalhello -cf paxglobal.tar small.txt` | 10240 | `e85dcd301f73d22890f3fb2ccb2a7a7187434afd7c05f258d583a73e339d4452` |
| `rar4-hp.rar` | V | [`Rar.encrypted_filesAndHeader.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar.encrypted_filesAndHeader.rar) | 60172 | `3944a0cf36758d042c238dacae56a4fa494b6f790c4f0dd719d9a47b7c845202` |
| `rar4-p.rar` | V | [`Rar.encrypted_filesOnly.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar.encrypted_filesOnly.rar) | 60069 | `52c0d575e01750ae643c65866b5e06e67773a0aed5edea6b2b04e7de14a784a2` |
| `rar4-solid.rar` | V | [`Rar.solid.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar.solid.rar) | 60001 | `e7e62f24f22f195d21437be09667c8956837d99ba48d7270090251a8e32babc5` |
| `rar4.rar` | V | [`Rar.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar.rar) | 60029 | `60db161de57dc59aa12e0c45b1b70d78904da3d104e74748972ac38643f12802` |
| `rar5-hp.rar` | V | [`Rar5.encrypted_filesAndHeader.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar5.encrypted_filesAndHeader.rar) | 60686 | `63fc4b5576f7482311e950155607e171696588881bdd277c8c099efd2f98b6e2` |
| `rar5-p.rar` | V | [`Rar5.encrypted_filesOnly.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar5.encrypted_filesOnly.rar) | 60336 | `e5fb06af812633b246430f029a051cc462d00180a0df455a798644deccdc7d07` |
| `rar5-solid.rar` | V | [`Rar5.solid.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar5.solid.rar) | 60046 | `3043d68c5a81f46adcbb5393632d243944ead44eb383a677ac82e68afdde27cf` |
| `rar5-volume1.rar` | V | [`Rar5.multi.part01.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar5.multi.part01.rar) | 10240 | `ad9b6ac157e77ba2cc6b8be7eeba52a2c047a0ef2d06fa6ffa0c1d1ccce3a651` |
| `rar5.rar` | V | [`Rar5.rar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Rar5.rar) | 60066 | `3319de3e8a91a58d08d8a83f48ebae7e6b022c542b4a58775bab64f4063988a9` |
| `sc-tar.tar` | V | [`Tar.tar`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Tar.tar) | 105472 | `787b8bf8217efd7f71e5367ac4b4673dc23ce805ad1fd93ba8eb8383a4cada77` |
| `sc-tar.tar.lz` | V | [`Tar.tar.lz`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Tar.tar.lz) | 59244 | `a5f7aa3c93f192ac553d7cccbde9af138dc935bc1d5eb048f1521a5f7c482d27` |
| `sc-tar.tar.zst` | V | [`Tar.tar.zst`](https://raw.githubusercontent.com/adamhathcock/sharpcompress/c083c6efd843a844b0c8f7878787360e815be781/tests/TestArchives/Archives/Tar.tar.zst) | 64512 | `90ecf9d8975f846148bc47f79e009de6db9323bf07d72dc11ca812d1faaba29f` |
| `sparse-gnu.tar` | T | `tar --format=gnu -S -cf sparse-gnu.tar big  (big = truncate -s 50M, then "tail" appended)` | 10240 | `44e0cadf8560e5cdcebcd72c2b1781dadea196f088066bc9200296f51853c8e8` |
| `sparse-pax01.tar` | T | `tar --format=pax -S --sparse-version=0.1 -cf sparse-pax01.tar big` | 10240 | `69b52108eb1f35c8f8328e5f9131c2822a9ca929e8696874063843e4f9b9dc14` |
| `tree.tar.bz2` | M | `the same tree.tar; bzip2 -9` | 10001 | `5984c3e1bd79794edaef39e260e81b3e5d14f98c701a797ab1d7da979439e100` |
| `tree.tar.gz` | M | `tar --format=gnu --owner=0 --group=0 --numeric-owner --sort=name -cf tree.tar ...; gzip -9 -n` | 10733 | `c895671b1b2a06570caa3c61778310d51b0f4894e3cbc8b3d16b053b1bfa4604` |
| `tree.tar.lz` | F | SharpCompress.Compressors.LZMA.LZipStream.Create(file, Compress) over the tar inside tree.tar.gz | 9061 | `2a84e6aa689251d619ca8242b325e26713a1afaa655eac7d1a71a5a33ba0b11c` |
| `tree.tar.xz` | M | `7z a -txz of a GNU tar (--format=gnu --owner=0 --group=0 --numeric-owner) of the tree` | 9072 | `229edf26f54c95b2e56a6cae75aaca57ea218d1ca4ecb7a328b1c9800de79e0d` |
| `tree.tar.zst` | F | SharpCompress.Compressors.ZStandard.CompressionStream(file, level 19) over the tar inside tree.tar.gz | 8910 | `238bb87a4deb95e542ded916bb78261ae0cddf35e2ca32c008da9c04c915de6d` |
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
