using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MeshDeletionTool
{
    // MeshDeletionToolForTexture の処理を CPU と GPU のバックエンドで繰り返し実行し、処理段毎の時間（中央値）と結果の一致を報告するウィンドウ
    // メッシュは保存しない。GPU で結果が CPU と一致するか（「結果一致: ✓」）を PC で確認する手順もこのウィンドウで行う
    public class MeshDeletionBenchmarkWindow : MeshDeletionToolUtils
    {
        private Renderer targetRenderer;
        private int iterations = 3;
        private float alphaThreshold = 0.5f;
        private bool refineBoundary = true;
        private float boundaryPrecisionTexels = 1.0f;
        private int refineMaxDepth = 3;
        private bool mergeCutPolygons = true;
        private Vector2 scroll;
        private string report = "";

        // Phase A の Mono ハーネス（Unity と同じコピー動作を模した測定）の値。Editor の絶対時間ではなく比較の目安
        private static readonly (string Label, string Before, string After)[] PhaseABaseline =
        {
            ("サバゲ衣装 (5,637 頂点 / 8,776 三角形 / 72 シェイプ), 細分化あり", "22.3 s", "960 ms (うちテクスチャ読み出し 483 ms)"),
            ("同, 細分化なし（従来動作）", "7.2 s", "684 ms (うちテクスチャ読み出し 492 ms)"),
            ("x4 (22,548 頂点), 細分化あり / なし", "403 s / 114 s", "2.3 s / 852 ms"),
            ("x16 (90,192 頂点), 細分化あり / なし", "> 20 分で中断", "8.5 s / 1.6 s")
        };

        [MenuItem("Tools/MeshDeletionTool/ベンチマーク")]
        private static void Init()
        {
            MeshDeletionBenchmarkWindow window = (MeshDeletionBenchmarkWindow)GetWindow(typeof(MeshDeletionBenchmarkWindow));
            window.titleContent = new GUIContent("MeshDeletion ベンチマーク");
            window.Show();
        }

        private void OnGUI()
        {
            GUILayout.Label("対象と設定（テクスチャを持つ全サブメッシュを処理対象にする。メッシュは保存しない）", EditorStyles.boldLabel);
            targetRenderer = EditorGUILayout.ObjectField("対象オブジェクト", targetRenderer, typeof(Renderer), true) as Renderer;
            iterations = Mathf.Clamp(EditorGUILayout.IntField("計測回数（別に 1 回のウォームアップ）", iterations), 1, 50);
            alphaThreshold = EditorGUILayout.Slider("アルファ閾値", alphaThreshold, 0f, 1f);
            refineBoundary = EditorGUILayout.Toggle("境界の細分化", refineBoundary);
            EditorGUI.BeginDisabledGroup(!refineBoundary);
            boundaryPrecisionTexels = EditorGUILayout.Slider("境界の精度（テクセル）", boundaryPrecisionTexels, 0.5f, 4f);
            refineMaxDepth = EditorGUILayout.IntSlider("細分化の最大深さ", refineMaxDepth, 0, 5);
            mergeCutPolygons = EditorGUILayout.Toggle("切断後の再結合", mergeCutPolygons);
            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button("ベンチマーク実行（CPU と GPU）"))
            {
                report = Run();
                Debug.Log(report);
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.TextArea(report, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        // 1 つのバックエンドでの計測結果
        private class BackendResult
        {
            public string Name;
            public List<Dictionary<string, double>> StageRuns = new List<Dictionary<string, double>>();
            public List<Dictionary<string, double>> KernelRuns = new List<Dictionary<string, double>>();
            public Dictionary<string, (int Calls, long Elements)> KernelCounts = new Dictionary<string, (int, long)>();
            public List<double> Totals = new List<double>();
            public MeshArrays Output;
        }

        private string Run()
        {
            if (targetRenderer == null)
                return "対象オブジェクトが選択されていません。";
            Mesh originalMesh = GetOriginalMesh(targetRenderer);
            Material[] originalMaterials = GetOriginalMaterials(targetRenderer);
            if (originalMesh == null || originalMaterials == null)
                return "対象オブジェクトに有効なメッシュがありません。";
            string meshProblem = FindMeshProblem(originalMesh, true);
            if (meshProblem != null)
                return meshProblem;

            StringBuilder text = new StringBuilder();
            text.AppendLine("MeshDeletionTool ベンチマーク  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine("GPU: " + SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceType + ", Compute Shader " + (SystemInfo.supportsComputeShaders ? "対応" : "非対応") + ")");
            text.AppendLine("CPU: " + SystemInfo.processorType + " x" + SystemInfo.processorCount + ", Unity " + Application.unityVersion);

            // 入力（テクスチャの読み出しとメッシュの読み出しは Unity 側の処理として別に測る）
            // テクスチャはツール本体と同じ経路で読む: 読み出す間だけインポート設定を読み取り可能・非圧縮に変更し、読み終えたら元に戻す
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool[] targetSubMeshes = MeshDeletionToolForTexture.SubMeshesWithTexture(originalMesh.subMeshCount, originalMaterials);
            AlphaMask[] subMeshMasks = MeshDeletionToolForTexture.CollectAlphaMasks(originalMesh.subMeshCount, originalMaterials, targetSubMeshes);
            double textureMs = stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Restart();
            MeshArrays sourceArrays = MeshArraysUnityAdapter.FromMesh(originalMesh);
            double fromMeshMs = stopwatch.Elapsed.TotalMilliseconds;
            List<AlphaMask> distinctMasks = subMeshMasks.Where(m => m != null).Distinct().ToList();
            long texelCount = distinctMasks.Sum(m => (long)m.Width * m.Height);
            text.AppendLine("入力: " + originalMesh.name + " " + sourceArrays.VertexCount.ToString("#,0") + " 頂点 / " + sourceArrays.TriangleCount.ToString("#,0") + " 三角形 / " +
                            originalMesh.subMeshCount + " サブメッシュ（対象 " + targetSubMeshes.Count(t => t) + "）/ " + sourceArrays.BlendShapes.Count + " シェイプ / テクスチャ " +
                            distinctMasks.Count + " 枚 " + texelCount.ToString("#,0") + " テクセル");
            text.AppendLine("読み出し（Unity 側、1 回）: テクスチャ " + textureMs.ToString("0.0") + " ms（インポート設定の一時変更と復元を含む）, メッシュ " + fromMeshMs.ToString("0.0") + " ms");
            text.AppendLine("設定: 閾値 " + alphaThreshold + ", 細分化 " + (refineBoundary ? "あり（精度 " + boundaryPrecisionTexels + " テクセル, 深さ " + refineMaxDepth + ", 再結合 " + (mergeCutPolygons ? "あり" : "なし") + "）" : "なし") +
                            ", 計測 " + iterations + " 回（最初の 1 回は捨てる）");
            if (targetSubMeshes.Count(t => t) == 0)
            {
                text.AppendLine("テクスチャを持つサブメッシュが無いため計測できません。");
                return text.ToString();
            }

            // バックエンド毎に計測
            List<BackendResult> results = new List<BackendResult>();
            results.Add(Measure(new CpuStageBackend(), sourceArrays, subMeshMasks, targetSubMeshes));
            IAlphaStageBackend gpu = ComputeStageBackend.TryCreate(out string reason);
            if (gpu != null)
                results.Add(Measure(gpu, sourceArrays, subMeshMasks, targetSubMeshes));
            else
                text.AppendLine("GPU: 計測できません（" + reason + "）");

            // 処理段毎の中央値
            text.AppendLine();
            text.AppendLine("処理段の時間（ms、中央値）");
            List<string> stageNames = results.SelectMany(r => r.StageRuns.SelectMany(run => run.Keys)).Distinct().ToList();
            text.AppendLine("| 処理段 | " + string.Join(" | ", results.Select(r => r.Name)) + " |");
            foreach (string stage in stageNames)
            {
                text.AppendLine("| " + stage + " | " + string.Join(" | ", results.Select(r => Median(r.StageRuns.Select(run => run.TryGetValue(stage, out double v) ? v : 0.0)).ToString("0.0"))) + " |");
            }
            text.AppendLine("| **合計** | " + string.Join(" | ", results.Select(r => Median(r.Totals).ToString("0.0"))) + " |");
            List<string> kernelNames = results.SelectMany(r => r.KernelRuns.SelectMany(run => run.Keys)).Distinct().ToList();
            foreach (string kernel in kernelNames)
            {
                text.AppendLine("| &nbsp;&nbsp;カーネル " + kernel + " | " + string.Join(" | ", results.Select(r =>
                    r.KernelCounts.TryGetValue(kernel, out (int Calls, long Elements) c)
                        ? Median(r.KernelRuns.Select(run => run.TryGetValue(kernel, out double v) ? v : 0.0)).ToString("0.0") + " (" + c.Calls + " 回, " + c.Elements.ToString("#,0") + " 要素)"
                        : "-")) + " |");
            }

            // 結果の一致
            text.AppendLine();
            if (results.Count == 2)
            {
                string difference = MeshArraysComparer.FirstDifference(results[0].Output, results[1].Output);
                text.AppendLine(difference == null
                    ? "結果一致: ✓ CPU と GPU の出力（頂点座標・UV・三角形・法線・接線・ブレンドシェイプ）はビット単位で同じです。"
                    : "結果不一致: ✗ " + difference);
            }
            text.AppendLine("出力: " + results[0].Output.VertexCount.ToString("#,0") + " 頂点 / " + results[0].Output.TriangleCount.ToString("#,0") + " 三角形");

            // Phase A の値
            text.AppendLine();
            text.AppendLine("参考: 改修前後の CPU 時間（Phase A、Mono ハーネス。Unity の Mesh プロパティのコピー動作を模し、テクスチャ読み出しを含む。Editor の絶対時間とは異なる）");
            text.AppendLine("| モデル | 改修前 | 改修後 (CPU) |");
            foreach ((string label, string before, string after) in PhaseABaseline)
            {
                text.AppendLine("| " + label + " | " + before + " | " + after + " |");
            }
            return text.ToString();
        }

        // 1 つのバックエンドで iterations + 1 回実行し（最初の 1 回は捨てる）、処理段毎の時間と最後の出力を返す
        private BackendResult Measure(IAlphaStageBackend backend, MeshArrays sourceArrays, AlphaMask[] subMeshMasks, bool[] targetSubMeshes)
        {
            BackendResult result = new BackendResult { Name = backend.Name };
            try
            {
                for (int i = 0; i <= iterations; i++)
                {
                    backend.ResetTimings();
                    AlphaMeshDeletionPipeline pipeline = new AlphaMeshDeletionPipeline
                    {
                        AlphaThreshold = alphaThreshold,
                        RefineBoundary = refineBoundary,
                        RefineMaxDepth = refineMaxDepth,
                        RefinePartiallyCutTriangles = true,
                        RefineChordToleranceTexels = Mathf.CeilToInt(2f * boundaryPrecisionTexels),
                        MergeCutPolygons = mergeCutPolygons,
                        SimplifyToleranceTexels = boundaryPrecisionTexels,
                        Backend = backend,
                        MeasureTime = true
                    };
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    result.Output = pipeline.Run(sourceArrays, subMeshMasks, targetSubMeshes);
                    double total = stopwatch.Elapsed.TotalMilliseconds;
                    if (i == 0)
                        continue;   // ウォームアップ（JIT、シェーダーの読み込み、バッファの確保）
                    result.Totals.Add(total);
                    result.StageRuns.Add(pipeline.StageTimings.ToDictionary(t => t.Name, t => t.Milliseconds));
                    result.KernelRuns.Add(backend.Timings.ToDictionary(t => t.Name, t => t.Milliseconds));
                    foreach (StageTiming timing in backend.Timings)
                    {
                        result.KernelCounts[timing.Name] = (timing.Calls, timing.Elements);
                    }
                }
            }
            finally
            {
                backend.Dispose();
            }
            return result;
        }

        private static double Median(IEnumerable<double> values)
        {
            List<double> sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0)
                return 0.0;
            return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
        }
    }
}
