using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MeshDeletionTool
{
    // プレビュー（MeshDeletionPreviewFilter）が表示中の結果のポリゴン数を、インスペクターが読めるように Renderer 毎に残す（エディターのみ、保存しない）
    // プレビューのノードが表示する結果を決めたときに Publish し、ノードが破棄されたときに Withdraw する（別の結果に置き換わっていれば何もしない）
    // 更新されると Changed を 1 回だけ（次のエディターの更新で）呼ぶ。インスペクターはそこで再描画する（毎フレームの再描画やログはしない）
    internal static class MeshDeletionPreviewStats
    {
        internal sealed class Record
        {
            // 結果（処理できなかったときは null）と、処理できなかった理由（処理できたなら null）
            public PolygonStats Stats;
            public string Problem;
            // 結果を作った設定（MeshDeletionOptions.SettingsKey）。インスペクターの現在の設定と違えば計算中
            public string SettingsKey;
            public DateTime UpdatedAt;
        }

        // 元の Renderer（シーン上のもの）の InstanceID → 表示中の結果
        private static readonly Dictionary<int, Record> Records = new Dictionary<int, Record>();
        private static bool changeQueued;

        internal static event Action Changed;

        static MeshDeletionPreviewStats()
        {
            // プレイモードの切り替え・ドメインのリロードで古い結果を残さない
            EditorApplication.playModeStateChanged += _ => Clear();
        }

        internal static void Publish(Renderer original, Record record)
        {
            if (original == null || record == null)
                return;
            int id = original.GetInstanceID();
            if (Records.TryGetValue(id, out Record current) && ReferenceEquals(current, record))
                return;
            Records[id] = record;
            QueueChanged();
        }

        internal static void Withdraw(Renderer original, Record record)
        {
            if (ReferenceEquals(original, null) || record == null)
                return;
            int id = original.GetInstanceID();
            if (Records.TryGetValue(id, out Record current) && ReferenceEquals(current, record))
            {
                Records.Remove(id);
                QueueChanged();
            }
        }

        internal static Record Get(Renderer original)
        {
            if (original == null)
                return null;
            return Records.TryGetValue(original.GetInstanceID(), out Record record) ? record : null;
        }

        internal static void Clear()
        {
            if (Records.Count == 0)
                return;
            Records.Clear();
            QueueChanged();
        }

        // 同じ更新の中の複数の変更をまとめて 1 回だけ通知する（プレビューの計算の途中で再描画しない）
        private static void QueueChanged()
        {
            if (changeQueued)
                return;
            changeQueued = true;
            EditorApplication.delayCall += () =>
            {
                changeQueued = false;
                Changed?.Invoke();
            };
        }
    }
}
