#!/usr/bin/env python3
"""verify_unitypackage.py <file.unitypackage | file_UPM.zip> [reference.unitypackage]

.unitypackage: checks the gzip FNAME, the GNU tar magic and header fields, the per-GUID entry set and order,
the newline-free pathname, GUID format and uniqueness, the two trailing zero blocks, and (with a reference
export) byte-compares the header field layout with the reference's entries.
.zip (UPM): checks that there is exactly one top-level folder holding a package.json whose "name" matches it,
that every file and folder inside has a .meta next to it, and that every .meta has a well-formed unique GUID.
"""
import gzip
import io
import json
import re
import struct
import sys
import zipfile

FIELDS = [("name", 0, 100), ("mode", 100, 108), ("uid", 108, 116), ("gid", 116, 124), ("size", 124, 136),
          ("mtime", 136, 148), ("chksum", 148, 156), ("typeflag", 156, 157), ("linkname", 157, 257),
          ("magic", 257, 265), ("uname", 265, 297), ("gname", 297, 329), ("devmajor", 329, 337),
          ("devminor", 337, 345), ("prefix", 345, 500), ("pad", 500, 512)]
GUID_RE = re.compile(r"[0-9a-f]{32}")


def gzip_fname(raw):
    assert raw[:2] == b"\x1f\x8b" and raw[2] == 8, "not gzip"
    assert raw[3] & 8, "gzip FNAME flag missing"
    i = 10
    if raw[3] & 4:
        i += 2 + struct.unpack("<H", raw[10:12])[0]
    return raw[i:raw.index(b"\0", i)]


def walk(tar):
    off = 0
    members = []
    while off + 512 <= len(tar):
        h = tar[off:off + 512]
        if h == b"\0" * 512:
            break
        name = h[:100].rstrip(b"\0").decode("utf-8")
        size = int(h[124:136].rstrip(b"\0 ") or b"0", 8)
        chk = int(h[148:156].rstrip(b"\0 "), 8)
        calc = sum(h[:148]) + 8 * 32 + sum(h[156:])
        assert chk == calc, "bad checksum at %d (%s)" % (off, name)
        assert h[257:265] == b"ustar  \0", "magic %r at %s" % (h[257:265], name)
        assert h[100:108] == b"0000777\0", "mode %r at %s" % (h[100:108], name)
        assert h[108:116] == b"0000000\0" and h[116:124] == b"0000000\0", "uid/gid at %s" % name
        assert h[329:345] == b"\0" * 16, "devmajor/minor not NUL at %s" % name
        data = tar[off + 512: off + 512 + size]
        members.append((name, h[156:157], size, data, h))
        off += 512 + (size + 511) // 512 * 512
    return members, off


def verify_unitypackage(path, reference=None):
    raw = open(path, "rb").read()
    fname = gzip_fname(raw)
    print("gzip FNAME:", fname)
    assert fname == b"archtemp.tar"
    tar = gzip.decompress(raw)
    members, end = walk(tar)
    assert tar[end:] == b"\0" * 1024, "trailing bytes after last member: %d (expected exactly 1024 zero)" % (len(tar) - end)
    print("tar %d bytes, %d entries, exactly 2 trailing zero blocks" % (len(tar), len(members)))
    guids = []
    per = {}
    for name, t, size, data, h in members:
        g, _, rest = name.partition("/")
        assert GUID_RE.fullmatch(g), "bad guid %r" % g
        if g not in per:
            guids.append(g)
            per[g] = []
        per[g].append((rest, t, data))
    assert len(guids) == len(set(guids)) and guids == sorted(guids), "guids not unique+sorted"
    nfile = nfold = 0
    paths = []
    for g in guids:
        parts = per[g]
        names = [p[0] for p in parts]
        assert parts[0] == ("", b"5", b""), "first entry for %s is not the directory: %r" % (g, names)
        if "asset" in names:
            assert names == ["", "asset", "asset.meta", "pathname"], names
            nfile += 1
        else:
            assert names == ["", "asset.meta", "pathname"], names
            nfold += 1
        by_name = dict((n, d) for n, t, d in parts)
        pn = by_name["pathname"]
        assert b"\n" not in pn and pn.startswith(b"Assets/"), pn
        meta = by_name["asset.meta"].decode()
        assert ("guid: " + g) in meta and meta.startswith("fileFormatVersion: 2\n"), meta
        assert ("folderAsset: yes" in meta) == ("asset" not in names)
        paths.append(pn.decode())
    assert len(paths) == len(set(paths))
    print("%d GUIDs: %d files, %d folders; dir-first order, meta guid match, newline-free pathname" % (len(guids), nfile, nfold))
    for p in sorted(paths):
        print("  ", p)
    if reference:
        ref_raw = open(reference, "rb").read()
        rm, _ = walk(gzip.decompress(ref_raw))
        print("\nheader-field diff vs reference (dir entry, file entry):")

        def cmp(a, b, label):
            diffs = []
            for f, s, e in FIELDS:
                x, y = a[s:e], b[s:e]
                if f in ("name", "size", "mtime", "chksum"):
                    continue          # values differ by construction
                if x != y:
                    diffs.append("%s: DIFF %r vs %r" % (f, x, y))
            print("  %s -> %s" % (label, "; ".join(diffs) or "identical apart from name/size/mtime/checksum values"))
        mine = {t: h for n, t, s, d, h in members}
        refs = {t: h for n, t, s, d, h in rm}
        cmp(mine[b"5"], refs[b"5"], "dir entry")
        cmp(mine[b"0"], refs[b"0"], "file entry")
        print("  gzip header bytes: mine %s ref %s (FNAME %r / %r)" % (
            raw[:4].hex() + ".." + raw[8:10].hex(), ref_raw[:4].hex() + ".." + ref_raw[8:10].hex(), fname, gzip_fname(ref_raw)))


def verify_upm_zip(path):
    with zipfile.ZipFile(path) as z:
        names = z.namelist()
        tops = sorted({n.split("/", 1)[0] for n in names})
        assert len(tops) == 1, "expected one top-level folder, got %r" % tops
        top = tops[0]
        pkg = json.loads(z.read(top + "/package.json").decode("utf-8"))
        assert pkg["name"] == top, "folder %r does not match package name %r" % (top, pkg["name"])
        folders = {n[:-1] for n in names if n.endswith("/")}
        files = {n for n in names if not n.endswith("/")}
        assets = {n for n in files if not n.endswith(".meta")} | (folders - {top})
        missing = sorted(a for a in assets if a + ".meta" not in files)
        assert not missing, "assets without .meta: %r" % missing
        orphan = sorted(m for m in files if m.endswith(".meta") and m[:-5] not in assets)
        assert not orphan, ".meta without asset: %r" % orphan
        guids = {}
        for m in sorted(files):
            if not m.endswith(".meta"):
                continue
            text = z.read(m).decode("utf-8")
            g = [l[6:].strip() for l in text.split("\n") if l.startswith("guid: ")]
            assert len(g) == 1 and GUID_RE.fullmatch(g[0]), "bad guid in %s" % m
            assert g[0] not in guids, "duplicate guid %s in %s and %s" % (g[0], m, guids[g[0]])
            assert ("folderAsset: yes" in text) == (m[:-5] in folders), "folderAsset flag wrong in %s" % m
            guids[g[0]] = m
        print("%s: %s %s, %d assets (%d folders), every asset has a .meta, %d unique GUIDs" % (
            path, pkg["name"], pkg.get("version", "?"), len(assets), len(folders) - 1, len(guids)))
        for a in sorted(assets):
            print("  ", a)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    path = sys.argv[1]
    if path.lower().endswith(".zip"):
        verify_upm_zip(path)
    else:
        verify_unitypackage(path, sys.argv[2] if len(sys.argv) > 2 else None)
    print("OK")


if __name__ == "__main__":
    main()
