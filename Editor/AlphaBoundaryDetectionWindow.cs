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
                labelEdgePoints = DetectAlphaEdges(alphaTexture); // エッジを検出＆境界をトレース
                Debug.Log("Boundary detection completed."); // 検出完了メッセージを出力
            }
            else
            {
                // sourceTextureがnullの場合に警告メッセージを出力
                Debug.LogWarning("Please assign a source texture.");
            }
        }

        // 境界が検出されている場合に、検出された境界を表示
        if (labelEdgePoints != null)
        {

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

    List<Vector2> DetectConsecutiveEdges(Texture2D alphaTex, int initX, int initY, int height, int width, HashSet<Vector2> scanndePixcel)
    {
        List<Vector2> edgeList = new List<Vector2>();
        int x = initX;
        int y = initY;

        HashSet<Vector2> localScanndePixcel = new HashSet<Vector2>();

        // 初期エッジ座標を追加
        edgeList.Add(new Vector2(x, y));
        scanndePixcel.Add(new Vector2(x, y));

        // 前回の座標
        int prevX = x;
        int prevY = y;

        while (true)
        {
            bool foundEdge = false;

            int[] dx = { -1,  0,  1,  1,  1,  0, -1, -1, -2, 2, 2, -2, -2, -2, 2, 2 };
            int[] dy = {  1,  1,  1,  0, -1, -1, -1,  0,  2, 2, -2, -2,  2, -2, 2, -2 };

            for (int i = 0; i < dx.Length; i++)
            {
                int currentX = x + dx[i];
                int currentY = y + dy[i];

                if (scanndePixcel.Contains(new Vector2(currentX, currentY)))
                    continue;

                scanndePixcel.Add(new Vector2(currentX, currentY));
                if (IsEdge(alphaTex, currentX, currentY))
                {
                    x = currentX;
                    y = currentY;
                    foundEdge = true;
                    break;
                }
            }

            // エッジが見つからない場合は終了
            if (!foundEdge)
            {
                break;
            }

            // 新しいエッジ座標を追加
            edgeList.Add(new Vector2(x, y));

            // 前回の座標が現在の座標と同じ場合はループを終了
            if (prevX == x && prevY == y)
            {
                break;
            }

            // 前回の座標を更新
            prevX = x;
            prevY = y;
        }

        scanndePixcel.UnionWith(localScanndePixcel);

        return edgeList;
    }

    // アルファテクスチャからエッジを検出するメソッド
    List<List<Vector2>> DetectAlphaEdges(Texture2D alphaTex)
    {
        int width = alphaTex.width;
        int height = alphaTex.height;
        List<List<Vector2>> labelEdgePoints = new List<List<Vector2>>();

        HashSet<Vector2> scanndePixcel = new HashSet<Vector2>();

        // 各ピクセルをループして、エッジを検出
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (scanndePixcel.Contains(new Vector2(x ,y)))
                    continue;

                // 対象ピクセルがエッジなら
                if (IsEdge(alphaTex, x, y))
                {
                    List<Vector2> edgePoint = DetectConsecutiveEdges(alphaTex, x, y, height, width, scanndePixcel);
                    labelEdgePoints.Add(edgePoint);
                }
            }

        }

        // 同じラベルのリストを1つにまとめる
        // List<List<Vector2>> margeLabelEdgePoints = new List<List<Vector2>>();
        int i = 0;
        while (i < labelEdgePoints.Count)
        {
            int j = i + 1;
            while (j < labelEdgePoints.Count)
            {
                Vector2 edge1Start = labelEdgePoints[i][0];
                Vector2 edge1End = labelEdgePoints[i][labelEdgePoints[i].Count - 1];
                Vector2 edge2Start = labelEdgePoints[j][0];
                Vector2 edge2End = labelEdgePoints[j][labelEdgePoints[j].Count - 1];
                int threshold = 10;

                bool merged = false;

                if (Vector2.Distance(edge1End, edge2Start) < threshold)
                {
                    labelEdgePoints[i].AddRange(labelEdgePoints[j]);
                    labelEdgePoints.RemoveAt(j);
                    merged = true;
                }
                else if (Vector2.Distance(edge1End, edge2End) < threshold)
                {
                    labelEdgePoints[j].Reverse();
                    labelEdgePoints[i].AddRange(labelEdgePoints[j]);
                    labelEdgePoints.RemoveAt(j);
                    merged = true;
                }
                else if (Vector2.Distance(edge1Start, edge2Start) < threshold)
                {
                    labelEdgePoints[i].Reverse();
                    labelEdgePoints[i].AddRange(labelEdgePoints[j]);
                    labelEdgePoints.RemoveAt(j);
                    merged = true;
                }
                else if (Vector2.Distance(edge1Start, edge2End) < threshold)
                {
                    labelEdgePoints[i].Reverse();
                    labelEdgePoints[j].Reverse();
                    labelEdgePoints[i].AddRange(labelEdgePoints[j]);
                    labelEdgePoints.RemoveAt(j);
                    merged = true;
                }

                if (!merged)
                {
                    j++;
                }
            }
            i++;
        }

        return labelEdgePoints; // エッジポイントリストを返す
    }

    // ピクセルがエッジであるかどうかを判定するメソッド
    bool IsEdge(Texture2D alphaTex, int x, int y, float threshold = 0.5f) // しきい値を追加
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