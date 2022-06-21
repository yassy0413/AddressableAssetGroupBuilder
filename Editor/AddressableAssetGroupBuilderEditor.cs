using UnityEngine;
using UnityEditor;

namespace AddressableAssets.GroupBuilder
{
    [CustomEditor(typeof(AddressableAssetGroupBuilder))]
    public sealed class AddressableAssetGroupBuilderEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button(new GUIContent("Test", "Output target asset entries with labels to console.")))
            {
                var self = target as AddressableAssetGroupBuilder;
                self.Test();
            }
            if (GUILayout.Button("Build"))
            {
                var self = target as AddressableAssetGroupBuilder;
                self.Build();
            }
            if (GUILayout.Button(new GUIContent("Clear", "Clear asset entries and labels.")))
            {
                AddressableAssetGroupBuilder.ClearAddressing();
            }

            EditorGUILayout.EndHorizontal();
        }
    }
}
