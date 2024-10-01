using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public class CSVGraphEditor : EditorWindow
{
    private string csvFilePath = "";
    private List<Vector2> points = new List<Vector2>();

    [MenuItem("Tools/CSV Graph Viewer")]
    public static void ShowWindow()
    {
        GetWindow<CSVGraphEditor>("CSV Graph Viewer");
    }

    private void OnGUI()
    {
        GUILayout.Label("CSV Graph Viewer", EditorStyles.boldLabel);

        if (GUILayout.Button("Load CSV File"))
        {
            csvFilePath = EditorUtility.OpenFilePanel("Select CSV File", "", "csv");
            if (!string.IsNullOrEmpty(csvFilePath))
            {
                LoadCSVData(csvFilePath);
            }
        }

        if (points.Count > 0)
        {
            GUILayout.Label("Graph Preview:");
            DrawGraph();
        }
    }

    private void LoadCSVData(string filePath)
    {
        points.Clear();
        string[] lines = File.ReadAllLines(filePath);
        foreach (var line in lines)
        {
            string[] values = line.Split(',');
            if (values.Length == 2)
            {
                try
                {
                    float x = float.Parse(values[0]);
                    float y = float.Parse(values[1]);
                    points.Add(new Vector2(x, y));
                }
                catch (Exception e)
                {
                    Debug.LogError("Error parsing CSV: " + e.Message);
                }
            }
        }
    }

    private void DrawGraph()
    {
        Rect graphRect = GUILayoutUtility.GetRect(400, 200);
        GUI.Box(graphRect, GUIContent.none);

        if (points.Count == 0) return;

        Vector2 minPoint = points[0];
        Vector2 maxPoint = points[0];

        foreach (var point in points)
        {
            minPoint = Vector2.Min(minPoint, point);
            maxPoint = Vector2.Max(maxPoint, point);
        }

        for (int i = 1; i < points.Count; i++)
        {
            Vector2 start = NormalizePoint(points[i - 1], minPoint, maxPoint, graphRect);
            Vector2 end = NormalizePoint(points[i], minPoint, maxPoint, graphRect);
            Handles.DrawLine(start, end);
        }
    }

    private Vector2 NormalizePoint(Vector2 point, Vector2 min, Vector2 max, Rect graphRect)
    {
        float normalizedX = Mathf.InverseLerp(min.x, max.x, point.x);
        float normalizedY = Mathf.InverseLerp(min.y, max.y, point.y);

        float xPos = graphRect.x + normalizedX * graphRect.width;
        float yPos = graphRect.y + (1 - normalizedY) * graphRect.height;

        return new Vector2(xPos, yPos);
    }
}
