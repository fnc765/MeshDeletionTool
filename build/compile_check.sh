#!/bin/bash
# Compile the three assemblies (Runtime -> Editor -> Tests) with the Roslyn compiler bundled in a Unity 2022.3
# Editor, without launching Unity and without a license. Errors are reported as csc would; 0 errors and
# "LAYOUT-OK" for every configuration means the sources compile in the same layout Unity uses.
#
# usage: UNITY_EDITOR_DIR=/path/to/Unity/2022.3.xf1 build/compile_check.sh [out-dir]
#   UNITY_EDITOR_DIR  the Editor install (contains Editor/Data on Linux, Editor\Data on Windows;
#                     Unity.app/Contents on macOS). Required.
#   NDMF_DIR          folder holding nadena.dev.ndmf.dll and nadena.dev.ndmf.runtime.dll (a project's
#                     Library/ScriptAssemblies, or Library/PackageCache/nadena.dev.ndmf@*/ after a build).
#                     Also needs System.Collections.Immutable.dll: either in the same folder or NDMF_IMMUTABLE_DLL.
#                     Optional: without it only the configuration without NDMF is compiled.
#   VRCSDK_BASE_DLL   path to VRCSDKBase.dll (optional; enables the NDMF + VRC_SDK_VRCSDK3 configuration).
#   NUNIT_DLL         nunit.framework.dll (default: searched in the Editor's template libcache and in a project's
#                     Library/PackageCache/com.unity.ext.nunit@*). The TestRunner DLLs come from the same libcache.
#                     Optional: without them the Tests assembly is skipped.
set -e
REPO=$(cd "$(dirname "$0")/.." && pwd)
OUT=${1:-$REPO/build/out/compile}
mkdir -p "$OUT"

[ -n "$UNITY_EDITOR_DIR" ] || { echo "UNITY_EDITOR_DIR is not set" >&2; exit 2; }
DATA=""
for c in "$UNITY_EDITOR_DIR/Editor/Data" "$UNITY_EDITOR_DIR/Data" "$UNITY_EDITOR_DIR/Contents" "$UNITY_EDITOR_DIR"; do
  [ -d "$c/Managed/UnityEngine" ] && DATA=$c && break
done
[ -n "$DATA" ] || { echo "no Managed/UnityEngine under $UNITY_EDITOR_DIR" >&2; exit 2; }

DOTNET=$(ls "$DATA"/NetCoreRuntime/dotnet "$DATA"/NetCoreRuntime/dotnet.exe 2>/dev/null | head -1)
CSC_DLL="$DATA/DotNetSdkRoslyn/csc.dll"
[ -x "$DOTNET" ] && [ -f "$CSC_DLL" ] || { echo "Roslyn not found under $DATA (NetCoreRuntime/dotnet, DotNetSdkRoslyn/csc.dll)" >&2; exit 2; }

# Reference set: the .NET 4.8 API reference assemblies and every UnityEngine/UnityEditor module, as Unity does.
RSP="$OUT/refs.rsp"
{
  echo "-nostdlib+ -nologo -langversion:9.0 -target:library -debug:portable -unsafe- -warn:2"
  echo "-define:UNITY_EDITOR;UNITY_2022_3;UNITY_2022;UNITY_5_3_OR_NEWER;UNITY_2022_3_OR_NEWER;CSHARP_7_OR_LATER"
  for f in "$DATA"/UnityReferenceAssemblies/unity-4.8-api/*.dll "$DATA"/UnityReferenceAssemblies/unity-4.8-api/Facades/*.dll "$DATA"/Managed/UnityEngine/*.dll; do
    case "$(basename "$f")" in *.TestRunner.dll|nunit*) continue;; esac
    echo "-r:\"$f\""
  done
} > "$RSP"

LIBCACHE=$(ls -d "$DATA"/Resources/PackageManager/ProjectTemplates/libcache/*/ScriptAssemblies 2>/dev/null | head -1)
if [ -z "$NUNIT_DLL" ]; then
  NUNIT_DLL=$(ls "$LIBCACHE"/nunit.framework.dll "$PWD"/Library/PackageCache/com.unity.ext.nunit@*/net35/unity-custom/nunit.framework.dll 2>/dev/null | head -1)
fi
TESTRUNNER_OK=0
[ -n "$NUNIT_DLL" ] && [ -f "$LIBCACHE/UnityEngine.TestRunner.dll" ] && [ -f "$LIBCACHE/UnityEditor.TestRunner.dll" ] && TESTRUNNER_OK=1

NDMF_REFS=""
if [ -n "$NDMF_DIR" ]; then
  IMM=${NDMF_IMMUTABLE_DLL:-$(ls "$NDMF_DIR"/System.Collections.Immutable.dll "$NDMF_DIR"/Dependencies~/System.Collections.Immutable.dll 2>/dev/null | head -1)}
  for f in "$NDMF_DIR/nadena.dev.ndmf.dll" "$NDMF_DIR/nadena.dev.ndmf.runtime.dll" "$IMM"; do
    [ -f "$f" ] || { echo "missing $f" >&2; exit 2; }
    NDMF_REFS="$NDMF_REFS -r:$f"
  done
fi

compile() {  # compile <name> <defines> [extra -r: args...]
  local name=$1 defs=$2; shift 2
  local d=""; [ -n "$defs" ] && d="-define:$defs"
  local o="$OUT/$name"; mkdir -p "$o"
  local csc="$DOTNET $CSC_DLL -noconfig @$RSP $d $*"
  (cd "$REPO" &&
    $csc -out:"$o/MeshDeletionTool.Runtime.dll" Runtime/*.cs &&
    $csc -r:"$o/MeshDeletionTool.Runtime.dll" -out:"$o/MeshDeletionTool.Editor.dll" Editor/*.cs Editor/Core/*.cs &&
    if [ $TESTRUNNER_OK = 1 ]; then
      $csc -r:"$o/MeshDeletionTool.Runtime.dll" -r:"$o/MeshDeletionTool.Editor.dll" -r:"$NUNIT_DLL" \
        -r:"$LIBCACHE/UnityEngine.TestRunner.dll" -r:"$LIBCACHE/UnityEditor.TestRunner.dll" \
        -out:"$o/MeshDeletionTool.Tests.dll" Tests/Editor/*.cs
    else
      echo "(Tests assembly skipped: nunit.framework.dll / TestRunner DLLs not found)"
    fi)
  echo "LAYOUT-OK $name defines=[$defs]"
}

compile plain ""
if [ -n "$NDMF_REFS" ]; then
  compile ndmf "NDMF" $NDMF_REFS
  if [ -n "$VRCSDK_BASE_DLL" ]; then
    compile ndmf_vrc "NDMF;VRC_SDK_VRCSDK3" $NDMF_REFS -r:"$VRCSDK_BASE_DLL"
  else
    echo "(NDMF;VRC_SDK_VRCSDK3 skipped: VRCSDK_BASE_DLL not set)"
  fi
else
  echo "(NDMF configurations skipped: NDMF_DIR not set)"
fi
