using UnityEngine;
using UnityEditor;

namespace AddressableAssetGroupBuilder
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

                if (VerifyGroup(self))
                {
                    self.Test();
                }
            }

            if (GUILayout.Button("Build"))
            {
                var self = target as AddressableAssetGroupBuilder;

                if (VerifyGroup(self))
                {
                    self.Build();
                }
            }

            if (GUILayout.Button(new GUIContent("Clear", "Clear asset entries and labels.")))
            {
                AddressableAssetGroupBuilder.ClearAddressing();
            }

            EditorGUILayout.EndHorizontal();
        }

        private static bool VerifyGroup(AddressableAssetGroupBuilder builder)
        {
            var groupVerifier = new AddressableAssetGroupBuilder.GroupVerifier();
            groupVerifier.Join(builder);
            return groupVerifier.Verify();
        }
    }
}
