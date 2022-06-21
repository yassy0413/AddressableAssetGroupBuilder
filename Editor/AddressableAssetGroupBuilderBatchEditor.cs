using UnityEngine;
using UnityEditor;

namespace AddressableAssets.GroupBuilder
{
    [CustomEditor(typeof(AddressableAssetGroupBuilderBatch))]
    public sealed class AddressableAssetGroupBuilderBatchEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button(new GUIContent("Test", "Output target asset entries with labels to console.")))
            {
                var self = target as AddressableAssetGroupBuilderBatch;
                self.TestAll();
            }
            if (GUILayout.Button("Build"))
            {
                var self = target as AddressableAssetGroupBuilderBatch;
                self.BuildAll();
            }
            if (GUILayout.Button(new GUIContent("Clear", "Clear asset entries and labels.")))
            {
                AddressableAssetGroupBuilder.ClearAddressing();
            }

            EditorGUILayout.EndHorizontal();
        }
    }
}
