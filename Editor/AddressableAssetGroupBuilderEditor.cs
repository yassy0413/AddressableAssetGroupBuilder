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

            if (GUILayout.Button("Test"))
            {
                var self = target as AddressableAssetGroupBuilder;
                self.Test();
            }
            if (GUILayout.Button("Build"))
            {
                var self = target as AddressableAssetGroupBuilder;
                self.Build();
            }
            if (GUILayout.Button("Clear"))
            {
                AddressableAssetGroupBuilder.ClearAddressing();
            }

            EditorGUILayout.EndHorizontal();
        }
    }
}
