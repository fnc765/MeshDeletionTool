// 必要なUnityエディタのライブラリを使用
using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.VisualScripting;

// AlphaBoundaryDetectionWindowクラスを定義し、EditorWindowを継承
public class AlphaBoundaryDetectionWindow : EditorWindow
{
    // プライベート変数を宣言
    private Texture2D sourceTexture; // 元のテクスチャ
    private Texture2D alphaTexture; // アルファチャンネルのテクスチャ
    private List<Vector2> edgePoints; // エッジの座標リスト
    private List<List<Vector2>> boundaries; // 境界のリスト
    private List<List<Vector2>> simplifiedBoundaries;
    private List<List<Vector2>> labelEdgePoints = new List<List<Vector2>>();

    // メニューに新しい項目を追加し、ウィンドウを表示するメソッド
    [MenuItem("Tools/Alpha Boundary Detection")]
    public static void ShowWindow()
    {
        // ウィンドウを表示し、タイトルを設定
        GetWindow<AlphaBoundaryDetectionWindow>("Alpha Boundary Detection");
    }

    // GUIを描画するためのメソッド
    void OnGUI()
    {
        // ラベルを表示
        GUILayout.Label("Alpha Boundary Detection", EditorStyles.boldLabel);
        // ソーステクスチャのフィールドを表示し、選択されたテクスチャをsourceTextureに代入
        sourceTexture = (Texture2D)EditorGUILayout.ObjectField("Source Texture", sourceTexture, typeof(Texture2D), false);

        // "Detect Boundaries"ボタンを表示し、クリックされたら境界検出を実行
        if (GUILayout.Button("Detect Boundaries"))
        {
            // sourceTextureがnullでない場合に境界を検出
            if (sourceTexture != null)
            {
                alphaTexture = ExtractAlphaTexture(sourceTexture); // アルファテクスチャを抽出

                (edgePoints , labelEdgePoints) = DetectAlphaEdges(alphaTexture); // エッジを検出
                boundaries = TraceBoundariesFromEdges(alphaTexture, edgePoints); // エッジから境界をトレース
                Debug.Log("Boundary detection completed."); // 検出完了メッセージを出力
            }
            else
            {
                // sourceTextureがnullの場合に警告メッセージを出力
                Debug.LogWarning("Please assign a source texture.");
            }
        }

        // 境界が検出されている場合に、検出された境界を表示
        if (boundaries != null)
        {
            if (GUILayout.Button("Save to CSV"))
            {
                SaveToCSV(boundaries, "Boundaries.csv");
                Debug.Log("CSV file saved.");
            }

            if (GUILayout.Button("Save to label CSV"))
            {
                SaveToCSV(labelEdgePoints, "Boundaries.csv");
                Debug.Log("CSV file saved.");
            }

            if (GUILayout.Button("Simplify Boundaries"))
            {
                simplifiedBoundaries = new List<List<Vector2>>();
                foreach (var labelEdgePoint in labelEdgePoints)
                {
                    simplifiedBoundaries.Add(RamerDouglasPeucker(labelEdgePoint, 5.0f));
                }
                Debug.Log("Boundary simplification completed.");
            }

            if (simplifiedBoundaries != null)
            {
                if (GUILayout.Button("Save Simplified to CSV"))
                {
                    SaveToCSV(simplifiedBoundaries, "SimplifiedBoundaries.csv");
                    Debug.Log("Simplified CSV file saved.");
                }

                GUILayout.Label("Simplified Boundaries:", EditorStyles.boldLabel);
                foreach (var boundary in simplifiedBoundaries)
                {
                    GUILayout.Label("Simplified Boundary:");
                    foreach (var point in boundary)
                    {
                        GUILayout.Label(point.ToString());
                    }
                }
            }
        }
    }

    // アルファチャンネルのテクスチャを抽出するメソッド
    Texture2D ExtractAlphaTexture(Texture2D texture)
    {
        // 新しいテクスチャを作成し、アルファチャンネルの値を設定
        Texture2D alphaTex = new Texture2D(texture.width, texture.height);
        for (int y = 0; y < texture.height; y++)
        {
            for (int x = 0; x < texture.width; x++)
            {
                Color color = texture.GetPixel(x, y);
                alphaTex.SetPixel(x, y, new Color(color.a, color.a, color.a, 1.0f));
            }
        }
        alphaTex.Apply(); // 変更を適用
        return alphaTex; // アルファテクスチャを返す
    }

    // アルファテクスチャからエッジを検出するメソッド
    (List<Vector2>, List<List<Vector2>>) DetectAlphaEdges(Texture2D alphaTex)
    {
        int width = alphaTex.width;
        int height = alphaTex.height;
        List<Vector2> edgePoints = new List<Vector2>(); // エッジのリスト
        List<(List<Vector2>, int, int)> labelEdgePoints = new List<(List<Vector2>, int, int)>();
        List<int> sameLabels = new List<int>();

        // 各ピクセルをループして、エッジを検出
        for (int y = 0; y < height; y++)
        {
            Dictionary<int, int> firstEdgePoints = new Dictionary<int, int>();
            for (int x = 0; x < width; x++)
            {
                // 対象ピクセルがエッジなら
                if (IsEdge(alphaTex, x, y))
                {
                    edgePoints.Add(new Vector2(x, y));

                    int labelFoundNum = -1;
                    // 既存の線分ラベルに属しているか
                    for (int labelNum = 0; labelNum < labelEdgePoints.Count; labelNum++)
                    {
                        (List<Vector2> labelEdgePoint, int firstEdgePointIndex, int endEdgePointIndex)
                            = labelEdgePoints[labelNum];

                        Vector2 firstEdgePoint = labelEdgePoint[firstEdgePointIndex];
                        Vector2 currentEdgePoint = labelEdgePoint[labelEdgePoint.Count - 1];
                        Vector2 endEdgePoint = labelEdgePoint[endEdgePointIndex];

                        float firstDistance = Vector2.Distance(firstEdgePoint, new Vector2(x, y));
                        float currentDistance =  Vector2.Distance(currentEdgePoint, new Vector2(x, y));
                        float endDistance = Vector2.Distance(endEdgePoint, new Vector2(x, y));

                        if (firstDistance < 2 || endDistance < 2 || currentDistance < 2)
                        {
                            if (labelFoundNum == -1) // 別のラベルに割り当てられていないなら
                            {
                                labelEdgePoint.Add(new Vector2(x, y));
                                labelFoundNum = labelNum;
                                if (!firstEdgePoints.ContainsKey(labelNum))
                                    firstEdgePoints.Add(labelNum, labelEdgePoint.Count - 1);
                            }
                            else
                            {
                                if (sameLabels[labelFoundNum] == -1)
                                    sameLabels[labelFoundNum] = sameLabels.Max() + 1;
                                sameLabels[labelNum] = sameLabels[labelFoundNum];
                            }
                        }
                    }
                    if (labelFoundNum == -1)
                    {
                        labelEdgePoints.Add((new List<Vector2>() { new Vector2(x, y) }, 0, 0));
                        sameLabels.Add(-1);
                    }
                }
            }

            foreach (var firstEdgePoint in firstEdgePoints)
            {
                int endEdgePoint = labelEdgePoints[firstEdgePoint.Key].Item1.Count - 1;

                var currentTuple = labelEdgePoints[firstEdgePoint.Key];
                var newTuple = (currentTuple.Item1, firstEdgePoint.Value, endEdgePoint);
                labelEdgePoints[firstEdgePoint.Key] = newTuple;
            }
        }

        // 同じラベルのリストを1つにまとめる
        List<List<Vector2>> margeLabelEdgePoints = new List<List<Vector2>>();
        // labelの数分ループ
        for (int labelNum = 0; labelNum <= sameLabels.Max(); labelNum++)
        {
            List<Vector2> edgePoint = new List<Vector2>();
            // 線分のList分ループ
            for (int edgePointNum = 0; edgePointNum < sameLabels.Count; edgePointNum++)
            {
                if (sameLabels[edgePointNum] == labelNum)
                {
                    edgePoint.AddRange(labelEdgePoints[edgePointNum].Item1);
                }
            }
            if (edgePoint.Count != 0)
                margeLabelEdgePoints.Add(edgePoint);
        }

        for (int edgePointNum = 0; edgePointNum < sameLabels.Count; edgePointNum++)
        {
            if (sameLabels[edgePointNum] == -1)
            {
                margeLabelEdgePoints.Add(labelEdgePoints[edgePointNum].Item1);
            }
        }

        return (edgePoints, margeLabelEdgePoints); // エッジポイントリストを返す
    }

    // エッジポイントから境界をトレースするメソッド
    List<List<Vector2>> TraceBoundariesFromEdges(Texture2D alphaTex, List<Vector2> edgePoints)
    {
        int width = alphaTex.width;
        int height = alphaTex.height;
        bool[,] visited = new bool[width, height]; // 訪問済みフラグを管理する配列
        List<List<Vector2>> boundaries = new List<List<Vector2>>(); // 境界のリスト

        foreach (var point in edgePoints)
        {
            int x = (int)point.x;
            int y = (int)point.y;
            if (!visited[x, y])
            {
                List<Vector2> boundary = new List<Vector2>();
                TraceBoundary(alphaTex, x, y, visited, boundary);
                if (boundary.Count > 10)
                {
                    boundaries.Add(boundary);
                }
            }
        }

        return boundaries; // 境界リストを返す
    }

    // ピクセルがエッジであるかどうかを判定するメソッド
    bool IsEdge(Texture2D alphaTex, int x, int y, float threshold = 0.1f) // しきい値を追加
    {
        float alpha = alphaTex.GetPixel(x, y).r;
        if (alpha <= threshold) // アルファがしきい値以下ならエッジではない
            return false;

        int width = alphaTex.width;
        int height = alphaTex.height;

        // 隣接ピクセルをチェックして、少なくとも一つの隣接ピクセルが透明ならエッジと判定
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx;
                int ny = y + dy;
                if (nx >= 0 && nx < width && ny >= 0 && ny < height)
                {
                    if (alphaTex.GetPixel(nx, ny).r <= threshold)
                        return true;
                }
            }
        }
        return false; // 隣接ピクセルが全て不透明ならエッジではない
    }

    // 境界をトレースするメソッド
    void TraceBoundary(Texture2D alphaTex, int startX, int startY, bool[,] visited, List<Vector2> boundary)
    {
        int width = alphaTex.width;
        int height = alphaTex.height;
        int x = startX;
        int y = startY;
        boundary.Add(new Vector2(x, y)); // 現在のピクセルを境界リストに追加
        visited[x, y] = true; // 訪問済みとしてマーク

        // 隣接ピクセルを定義
        int[] dx = { 1, 1, 0, -1, -1, -1, 0, 1 };
        int[] dy = { 0, 1, 1, 1, 0, -1, -1, -1 };

        HashSet<Vector2> edgeSet = new HashSet<Vector2>(edgePoints); // edgePointsをセットに変換

        while (true)
        {
            bool foundNext = false;
            // 隣接ピクセルをチェックして、次のエッジピクセルを見つける
            for (int i = 0; i < 8; i++)
            {
                int nx = x + dx[i];
                int ny = y + dy[i];
                if (nx >= 0 && nx < width && ny >= 0 && ny < height && !visited[nx, ny] && edgeSet.Contains(new Vector2(nx, ny)))
                {
                    x = nx;
                    y = ny;
                    boundary.Add(new Vector2(x, y)); // 新しいエッジピクセルを境界リストに追加
                    visited[nx, ny] = true; // 訪問済みとしてマーク
                    foundNext = true;
                    break;
                }
            }
            if (!foundNext) // 次のエッジピクセルが見つからない場合は終了
                break;
        }
    }

    // Ramer–Douglas–Peuckerアルゴリズムを使用して頂点を間引くメソッド
    List<Vector2> RamerDouglasPeucker(List<Vector2> points, float epsilon)
    {
        if (points.Count < 3)
            return new List<Vector2>(points);

        int index = -1;
        float maxDistance = 0.0f;
        int end = points.Count - 1;

        for (int i = 1; i < end; i++)
        {
            float distance = PerpendicularDistance(points[i], points[0], points[end]);
            if (distance > maxDistance)
            {
                index = i;
                maxDistance = distance;
            }
        }

        if (maxDistance > epsilon)
        {
            List<Vector2> leftList = RamerDouglasPeucker(points.GetRange(0, index + 1), epsilon);
            List<Vector2> rightList = RamerDouglasPeucker(points.GetRange(index, points.Count - index), epsilon);

            List<Vector2> result = new List<Vector2>(leftList);
            result.AddRange(rightList.GetRange(1, rightList.Count - 1));
            return result;
        }
        else
        {
            return new List<Vector2> { points[0], points[end] };
        }
    }

    // 点から線分への垂直距離を計算するメソッド
    float PerpendicularDistance(Vector2 point, Vector2 start, Vector2 end)
    {
        if (start == end)
            return Vector2.Distance(point, start);

        float n = Mathf.Abs((end.x - start.x) * (start.y - point.y) - (start.x - point.x) * (end.y - start.y));
        float d = Mathf.Sqrt(Mathf.Pow(end.x - start.x, 2) + Mathf.Pow(end.y - start.y, 2));
        return n / d;
    }

    // 境界をCSVファイルとして保存するメソッド
    void SaveToCSV(List<List<Vector2>> boundaries, string defaultName)
    {
        string path = EditorUtility.SaveFilePanel("Save Boundaries as CSV", "", defaultName, "csv");

        if (path.Length == 0) return;

        using (StreamWriter writer = new StreamWriter(path))
        {
            foreach (var boundary in boundaries)
            {
                foreach (var point in boundary)
                {
                    writer.WriteLine($"{point.x},{point.y}");
                }
                writer.WriteLine(); // 境界間に空行を入れる
            }
        }
    }
}