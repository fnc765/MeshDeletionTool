// NDMF（nadena.dev.ndmf 1.8.0 以降）がプロジェクトにあるときだけコンパイルされる
#if NDMF
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MeshDeletionTool
{
    // NDMF のプレビュー: プレイモードに入らなくても、MeshDeletionForTexture を適用した結果をシーンビュー・ゲームビューに表示する
    // NDMF のプレビューは元のオブジェクトを隠し、NDMF のプレビュー用シーンに作った代わりの Renderer（プロキシ）を描く。
    // このフィルターはプロキシのメッシュだけを削った結果に差し替えるので、シーン・プレハブ・元のメッシュは一切変更せず、保存もされない
    // 対象は読み込まれている全シーンの MeshDeletionForTexture（アバターの配下にないものも含む。NDMF のプレビューはアバターのルートを要求しない）
    // オン・オフは Toggle（インスペクターのボタンと、NDMF のプレビュー設定 Tools/NDM Framework/Configure Previews に出るスイッチ）。既定はオフで、設定は保存しない
    internal sealed class MeshDeletionPreviewFilter : IRenderFilter
    {
        internal static readonly MeshDeletionPreviewFilter Instance = new MeshDeletionPreviewFilter();

        // プレビューのオン・オフ（全ての MeshDeletionForTexture で共通）
        internal static readonly TogglablePreviewNode Toggle =
            TogglablePreviewNode.Create(() => "MeshDeletionForTexture（テクスチャに合わせたメッシュの削除）", null, false);

        // NDMF 全体のプレビューのオン・オフのメニュー（NDMF の Menus.ENABLE_PREVIEW_MENU_NAME）
        internal const string NdmfEnablePreviewsMenu = "Tools/NDM Framework/Enable Previews";

        private MeshDeletionPreviewFilter()
        {
        }

        public IEnumerable<TogglablePreviewNode> GetPreviewControlNodes()
        {
            yield return Toggle;
        }

        public bool IsEnabled(ComputeContext context)
        {
            return context.Observe(Toggle.IsEnabled);
        }

        // 対象: MeshDeletionForTexture と同じオブジェクトの SkinnedMeshRenderer / MeshRenderer（1 つずつ別のグループ）
        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            ImmutableList<RenderGroup>.Builder groups = ImmutableList.CreateBuilder<RenderGroup>();
            foreach (MeshDeletionForTexture component in context.GetComponentsByType<MeshDeletionForTexture>())
            {
                if (component == null)
                    continue;
                Renderer renderer = context.GetComponent<Renderer>(component.gameObject);
                if (renderer is SkinnedMeshRenderer || renderer is MeshRenderer)
                    groups.Add(RenderGroup.For(renderer));
            }
            return groups.ToImmutable();
        }

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> proxyPairs, ComputeContext context)
        {
            (Renderer original, Renderer proxy) = proxyPairs.First();
            return Task.FromResult<IRenderFilterNode>(Node.Create(original, proxy, context, null, false));
        }

        // 1 つの Renderer のプレビュー。生成したメッシュ（MeshCache の項目）を毎フレーム プロキシに割り当てる
        // （NDMF はプロキシを毎フレーム元の Renderer の状態に戻してから各ノードの OnFrame を呼ぶ）
        // 表示する結果のポリゴン数は MeshDeletionPreviewStats に残し（インスペクターが表示する）、ノードの破棄で取り下げる
        private sealed class Node : IRenderFilterNode
        {
            private readonly Renderer original;
            private readonly MeshCache.Entry entry;

            public RenderAspects WhatChanged { get; private set; }

            private Node(Renderer original, MeshCache.Entry entry)
            {
                this.original = original;
                this.entry = entry;
                WhatChanged = RenderAspects.Mesh;
                if (entry != null)
                    MeshDeletionPreviewStats.Publish(original, entry.Record);
            }

            // context で設定（MeshDeletionForTexture の各項目）を監視し、入力（プロキシのメッシュ・マテリアル・テクスチャ）と設定が同じなら
            // prior（前のノード）かキャッシュのメッシュを使い回す。forceRecompute なら作り直す（元のメッシュが書き換えられたとき）
            internal static Node Create(Renderer original, Renderer proxy, ComputeContext context, Node prior, bool forceRecompute)
            {
                MeshDeletionForTexture component = context.GetComponent<MeshDeletionForTexture>(original.gameObject);
                // 入力は前段のプレビュー（他のプラグイン）を反映したプロキシのメッシュとマテリアル（ビルドでも前段の処理の後に実行される）
                Mesh inputMesh = MeshDeletionRunner.GetOriginalMesh(proxy, false);
                if (component == null || inputMesh == null)
                    return new Node(original, null);
                int subMeshCount = inputMesh.subMeshCount;
                string settingsKey = context.Observe(component,
                    c => MeshDeletionRunner.OptionsFromComponent(c, subMeshCount).SettingsKey(),
                    (a, b) => a == b);
                string key = InputKey(original, proxy, inputMesh) + "|" + settingsKey;

                if (prior != null && !forceRecompute && prior.entry != null && prior.entry.Key == key)
                {
                    prior.WhatChanged = 0;
                    return prior;
                }
                return new Node(original, MeshCache.Acquire(key, forceRecompute, () => Compute(component, original, proxy, settingsKey)));
            }

            public Task<IRenderFilterNode> Refresh(IEnumerable<(Renderer, Renderer)> proxyPairs, ComputeContext context, RenderAspects updatedAspects)
            {
                (Renderer original, Renderer proxy) = proxyPairs.First();
                // マテリアル・テクスチャの変更はキー（メインテクスチャとその内容のハッシュ）で判定する。メッシュ自体の書き換えはキーに現れないので作り直す
                bool forceRecompute = (updatedAspects & RenderAspects.Mesh) != 0 && entry != null && MeshDeletionRunner.GetOriginalMesh(proxy, false) == entry.InputMesh;
                return Task.FromResult<IRenderFilterNode>(Create(original, proxy, context, this, forceRecompute));
            }

            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (entry == null || entry.Mesh == null)
                    return;
                if (proxy is SkinnedMeshRenderer skinnedMeshRenderer)
                {
                    skinnedMeshRenderer.sharedMesh = entry.Mesh;
                }
                else if (proxy.TryGetComponent(out MeshFilter meshFilter))
                {
                    meshFilter.sharedMesh = entry.Mesh;
                }
            }

            public void Dispose()
            {
                if (entry == null)
                    return;
                MeshDeletionPreviewStats.Withdraw(original, entry.Record);
                MeshCache.Release(entry);
            }
        }

        // 入力を表すキー: 元の Renderer、プロキシのメッシュ、サブメッシュ毎のメインテクスチャ（とその内容のハッシュ。再インポートで変わる）
        private static string InputKey(Renderer original, Renderer proxy, Mesh inputMesh)
        {
            StringBuilder key = new StringBuilder();
            key.Append(original.GetInstanceID()).Append('|').Append(inputMesh.GetInstanceID());
            foreach (Material material in proxy.sharedMaterials)
            {
                Texture texture = material != null ? material.mainTexture : null;
                key.Append('|').Append(texture != null ? texture.GetInstanceID() + ":" + texture.imageContentsHash : "-");
            }
            return key.ToString();
        }

        // プロキシのメッシュを component の設定（settingsKey はその SettingsKey）で削る（計算バックエンドは自動）
        // 処理できなければ理由を 1 行ログに出し、メッシュ無しの項目にする。どちらの場合もインスペクター用の記録（Record）を付ける
        private static MeshCache.Entry Compute(MeshDeletionForTexture component, Renderer original, Renderer proxy, string settingsKey)
        {
            MeshCache.Entry entry = new MeshCache.Entry { InputMesh = MeshDeletionRunner.GetOriginalMesh(proxy, false) };
            entry.Record = new MeshDeletionPreviewStats.Record { SettingsKey = settingsKey, UpdatedAt = DateTime.Now };
            string name = original.gameObject.name;
            string problem = MeshDeletionRunner.FindMeshProblem(entry.InputMesh, true);
            if (problem != null)
            {
                Debug.LogWarning("[プレビュー] '" + name + "': " + problem, original);
                entry.Record.Problem = problem;
                return entry;
            }
            try
            {
                MeshDeletionOptions options = MeshDeletionRunner.OptionsFromComponent(component, entry.InputMesh.subMeshCount);
                // 経過のログは出さず、要約の 1 行だけを出す（設定を変える度に作り直すため）
                MeshDeletionResult result = MeshDeletionRunner.Run(proxy, options, null, _ => { });
                entry.Mesh = result.Mesh;
                entry.Mesh.name += "_preview";
                entry.Mesh.hideFlags = HideFlags.DontSave;
                entry.Record.Stats = result.Stats;
                Debug.Log("[プレビュー] " + result.Summary + "（'" + name + "'）", original);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[プレビュー] '" + name + "' のプレビューを作れませんでした: " + e.Message, original);
                entry.Record.Problem = "プレビューを作れませんでした: " + e.Message;
            }
            return entry;
        }

        // 生成したプレビュー用メッシュのキャッシュ（入力と設定のキー毎）。参照するノードが無くなったら次のエディターの更新で破棄する
        // （NDMF はノードを作り直すときに新しいノードを作ってから古いノードを破棄するとは限らないので、すぐには破棄しない）
        private static class MeshCache
        {
            internal sealed class Entry
            {
                public string Key;
                public Mesh InputMesh;
                // 削った結果（処理できなかったときは null）
                public Mesh Mesh;
                // インスペクターに表示するポリゴン数（処理できなかったときは理由）
                public MeshDeletionPreviewStats.Record Record;
                public int References;
            }

            private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();

            static MeshCache()
            {
                // ドメインのリロードでキャッシュ（static）が失われる前に、生成したメッシュを破棄する
                AssemblyReloadEvents.beforeAssemblyReload += () =>
                {
                    foreach (Entry entry in Entries.Values)
                        DestroyMesh(entry);
                    Entries.Clear();
                };
            }

            internal static Entry Acquire(string key, bool forceRecompute, Func<Entry> compute)
            {
                if (!forceRecompute && Entries.TryGetValue(key, out Entry cached))
                {
                    cached.References++;
                    return cached;
                }
                Entry entry = compute();
                entry.Key = key;
                entry.References = 1;
                // 作り直した場合、古い項目はそれを参照するノードが破棄されたときに破棄される
                Entries[key] = entry;
                return entry;
            }

            internal static void Release(Entry entry)
            {
                entry.References--;
                if (entry.References > 0)
                    return;
                EditorApplication.delayCall += () =>
                {
                    if (entry.References > 0)
                        return;
                    if (Entries.TryGetValue(entry.Key, out Entry current) && current == entry)
                        Entries.Remove(entry.Key);
                    DestroyMesh(entry);
                };
            }

            private static void DestroyMesh(Entry entry)
            {
                if (entry.Mesh != null)
                    Object.DestroyImmediate(entry.Mesh);
                entry.Mesh = null;
            }
        }
    }
}
#endif
