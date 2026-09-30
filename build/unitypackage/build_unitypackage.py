#!/usr/bin/env python3
"""Build the MeshDeletionTool distribution files from this repository without Unity.

Outputs (in --out, default build/out/):
  MeshDeletionTool_v<ver>.unitypackage        package.json, README.md, CHANGELOG.md, LICENSE, Runtime/, Editor/
  MeshDeletionTool_Tests_v<ver>.unitypackage  Tests/ (EditMode tests; import on top of the main package)
  MeshDeletionTool_UPM_v<ver>.zip             <package name>/ with the same files plus Tests/ and every .meta
  guids_<name>.json                           asset path -> GUID, for reference

usage: build_unitypackage.py [--repo DIR] [--out DIR] [--version V] [--only main,tests,upm] [--write-metas]

GUIDs: the GUID of every asset is read from its committed .meta file next to it in the repository. When an asset
has no .meta yet, the GUID is derived deterministically as md5("MeshDeletionTool:Assets/MeshDeletionTool/<path>"),
and --write-metas writes such missing .meta files into the repository (existing ones are never touched), so the
repository, the unitypackages and the UPM zip always share one set of GUIDs.

unitypackage layout (byte-structurally the same as a Unity 2022.3 export): gzip(FNAME=archtemp.tar) of a GNU tar
(magic 'ustar  \\0'); per GUID, sorted: <guid>/ (dir), <guid>/asset (files only), <guid>/asset.meta,
<guid>/pathname (no trailing newline); mode 0777, uid/gid 0, devmajor/devminor all NUL, two trailing zero blocks.
.cs -> MonoImporter, .compute -> ComputeShaderImporter, .asmdef -> AssemblyDefinitionImporter, folders ->
DefaultImporter (folderAsset), files without extension -> DefaultImporter, everything else -> TextScriptImporter.
"""
import argparse
import gzip
import hashlib
import io
import json
import os
import sys
import time
import zipfile

ASSETS_ROOT = "Assets/MeshDeletionTool"   # where the unitypackages unpack; also the GUID derivation prefix
GUID_SALT = "MeshDeletionTool:"
DOC_FILES = ["package.json", "README.md", "CHANGELOG.md", "LICENSE"]
MAIN_DIRS = ["Runtime", "Editor"]
TEST_DIRS = ["Tests"]
MTIME = int(time.time())


# ---------------------------------------------------------------- repository scan

def rel_files(repo, top):
    """All non-.meta files under repo/top as repo-relative paths with '/' separators, sorted."""
    out = []
    for root, dirs, files in os.walk(os.path.join(repo, top)):
        dirs.sort()
        for f in sorted(files):
            if f.endswith(".meta"):
                continue
            out.append(os.path.relpath(os.path.join(root, f), repo).replace(os.sep, "/"))
    return out


def with_folders(paths):
    """Add every folder that contains one of the paths (repo-relative, '' excluded)."""
    entries = set(paths)
    for p in paths:
        d = os.path.dirname(p)
        while d:
            entries.add(d)
            d = os.path.dirname(d)
    return sorted(entries)


def derived_guid(rel):
    return hashlib.md5((GUID_SALT + ASSETS_ROOT + "/" + rel).encode("utf-8")).hexdigest()


TAIL = "  userData: \n  assetBundleName: \n  assetBundleVariant: \n"


def generated_meta(rel, is_folder, guid):
    head = "fileFormatVersion: 2\nguid: %s\n" % guid
    if is_folder:
        return head + "folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n" + TAIL
    ext = os.path.splitext(rel)[1].lower()
    if ext == ".cs":
        return head + ("MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n"
                       "  executionOrder: 0\n  icon: {instanceID: 0}\n") + TAIL
    if ext == ".compute":
        return head + "ComputeShaderImporter:\n  externalObjects: {}\n  currentAPIMask: 4\n  preprocessorOverride: 0\n" + TAIL
    if ext == ".asmdef":
        return head + "AssemblyDefinitionImporter:\n  externalObjects: {}\n" + TAIL
    if ext == "":
        return head + "DefaultImporter:\n  externalObjects: {}\n" + TAIL
    return head + "TextScriptImporter:\n  externalObjects: {}\n" + TAIL


def read_meta(repo, rel):
    """(meta text, guid) from the committed .meta, or (None, None) when the asset has none."""
    p = os.path.join(repo, rel + ".meta")
    if not os.path.isfile(p):
        return None, None
    text = open(p, "rb").read().decode("utf-8").replace("\r\n", "\n")
    for line in text.split("\n"):
        if line.startswith("guid: "):
            return text, line[6:].strip()
    raise SystemExit("no guid line in " + p)


def meta_for(repo, rel, is_folder):
    text, guid = read_meta(repo, rel)
    if text is None:
        guid = derived_guid(rel)
        text = generated_meta(rel, is_folder, guid)
    return text, guid


# ---------------------------------------------------------------- tar / unitypackage

def header(name, size, typeflag, mtime=MTIME):
    name_b = name.encode("utf-8")
    assert len(name_b) < 100, name
    h = bytearray(512)
    h[0:len(name_b)] = name_b
    h[100:108] = b"0000777\0"
    h[108:116] = b"0000000\0"
    h[116:124] = b"0000000\0"
    h[124:136] = ("%011o" % size).encode() + b"\0"
    h[136:148] = ("%011o" % mtime).encode() + b"\0"
    h[148:156] = b"        "           # checksum placeholder
    h[156:157] = typeflag
    h[257:265] = b"ustar  \0"          # GNU magic + version; uname/gname/devmajor/devminor/prefix stay NUL
    chk = sum(h)
    h[148:156] = ("%06o" % chk).encode() + b"\0 "
    return bytes(h)


def member(buf, name, data=None):
    if data is None:
        buf.write(header(name, 0, b"5"))
        return
    buf.write(header(name, len(data), b"0"))
    buf.write(data)
    buf.write(b"\0" * ((-len(data)) % 512))


def build_unitypackage(repo, rels, out_path, guids_path):
    """rels: repo-relative file paths; each lands at ASSETS_ROOT/<rel>. Folders are added automatically."""
    items = []
    files = set(rels)
    for rel in with_folders(rels):
        is_folder = rel not in files
        meta, guid = meta_for(repo, rel, is_folder)
        items.append((guid, rel, is_folder, meta))
    root_guid = hashlib.md5((GUID_SALT + ASSETS_ROOT).encode("utf-8")).hexdigest()
    items.append((root_guid, "", True, generated_meta("", True, root_guid)))
    items.sort()
    assert len({g for g, _, _, _ in items}) == len(items), "GUID collision"
    buf = io.BytesIO()
    guids = {}
    for guid, rel, is_folder, meta in items:
        assets_path = ASSETS_ROOT + ("/" + rel if rel else "")
        member(buf, guid + "/")
        if not is_folder:
            member(buf, guid + "/asset", open(os.path.join(repo, rel), "rb").read())
        member(buf, guid + "/asset.meta", meta.encode("utf-8"))
        member(buf, guid + "/pathname", assets_path.encode("utf-8"))
        guids[assets_path] = guid
    buf.write(b"\0" * 1024)
    tar = buf.getvalue()
    gz = io.BytesIO()
    with gzip.GzipFile(filename="archtemp.tar", mode="wb", fileobj=gz, mtime=MTIME, compresslevel=6) as f:
        f.write(tar)
    data = bytearray(gz.getvalue())
    data[8] = 4
    data[9] = 0                        # XFL/OS bytes as in the Unity export (cosmetic)
    with open(out_path, "wb") as f:
        f.write(data)
    with open(guids_path, "w", encoding="utf-8") as f:
        json.dump(guids, f, indent=1, ensure_ascii=False)
        f.write("\n")
    nfold = sum(1 for i in items if i[2])
    print("wrote %s: %d bytes (%d files, %d folders)" % (out_path, len(data), len(items) - nfold, nfold))


# ---------------------------------------------------------------- UPM zip

def build_upm_zip(repo, rels, pkg_name, out_path):
    """rels land at <pkg_name>/<rel>; every asset gets its .meta (committed or generated) next to it."""
    files = set(rels)
    entries = with_folders(rels)
    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr(pkg_name + "/", b"")
        for rel in entries:
            is_folder = rel not in files
            meta, _ = meta_for(repo, rel, is_folder)
            if is_folder:
                z.writestr(pkg_name + "/" + rel + "/", b"")
            else:
                z.write(os.path.join(repo, rel), pkg_name + "/" + rel)
            z.writestr(pkg_name + "/" + rel + ".meta", meta.encode("utf-8"))
    print("wrote %s: %d bytes (%d files + .meta, %d folders)" % (
        out_path, os.path.getsize(out_path), len(files), len(entries) - len(files)))


# ---------------------------------------------------------------- .meta generation

def write_missing_metas(repo, rels):
    files = set(rels)
    written = 0
    for rel in with_folders(rels):
        p = os.path.join(repo, rel + ".meta")
        if os.path.exists(p):
            continue
        is_folder = rel not in files
        guid = derived_guid(rel)
        with open(p, "wb") as f:
            f.write(generated_meta(rel, is_folder, guid).encode("utf-8"))
        written += 1
        print("meta: " + rel + ".meta")
    print("%d .meta file(s) written" % written)


# ---------------------------------------------------------------- main

def main():
    here = os.path.dirname(os.path.abspath(__file__))
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--repo", default=os.path.normpath(os.path.join(here, "..", "..")), help="repository root")
    ap.add_argument("--out", default=None, help="output directory (default: <repo>/build/out)")
    ap.add_argument("--version", default=None, help="version for the file names (default: package.json)")
    ap.add_argument("--only", default="main,tests,upm", help="comma-separated subset of main,tests,upm")
    ap.add_argument("--write-metas", action="store_true",
                    help="write missing .meta files into the repository and exit")
    a = ap.parse_args()

    repo = os.path.abspath(a.repo)
    pkg = json.load(open(os.path.join(repo, "package.json"), encoding="utf-8"))
    version = a.version or pkg["version"]
    docs = [d for d in DOC_FILES if os.path.isfile(os.path.join(repo, d))]
    main_rels = docs + [f for d in MAIN_DIRS for f in rel_files(repo, d)]
    test_rels = [f for d in TEST_DIRS for f in rel_files(repo, d)]

    if a.write_metas:
        write_missing_metas(repo, main_rels + test_rels)
        return

    out = os.path.abspath(a.out or os.path.join(repo, "build", "out"))
    os.makedirs(out, exist_ok=True)
    only = set(a.only.split(","))
    if "main" in only:
        build_unitypackage(repo, main_rels, os.path.join(out, "MeshDeletionTool_v%s.unitypackage" % version),
                           os.path.join(out, "guids_MeshDeletionTool.json"))
    if "tests" in only:
        build_unitypackage(repo, test_rels, os.path.join(out, "MeshDeletionTool_Tests_v%s.unitypackage" % version),
                           os.path.join(out, "guids_MeshDeletionTool_Tests.json"))
    if "upm" in only:
        build_upm_zip(repo, main_rels + test_rels, pkg["name"],
                      os.path.join(out, "MeshDeletionTool_UPM_v%s.zip" % version))


if __name__ == "__main__":
    main()
