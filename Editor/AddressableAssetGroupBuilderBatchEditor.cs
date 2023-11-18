using UnityEngine;
using UnityEditor;

namespace AddressableAssets.GroupBuilder
{
    [CustomEditor(typeof(AddressableAssetGroupBuilderBatch))]
    public sealed class AddressableAssetGroupBuilderBatchEditor : Editor
    {
        private SerializedProperty keepGroupsRegexPatternProp;

        private void OnEnable()
        {
            keepGroupsRegexPatternProp = serializedObject.FindProperty("keepGroupsRegexPattern");
        }

        public override void OnInspectorGUI()
        {
            if (target is not AddressableAssetGroupBuilderBatch self)
            {
                return;
            }
            
            base.OnInspectorGUI();

            if (self.removeUnusedGroupsWhenBuild)
            {
                serializedObject.Update();
                EditorGUILayout.PropertyField(keepGroupsRegexPatternProp, true);
                serializedObject.ApplyModifiedProperties();
            }
            
            EditorGUILayout.BeginHorizontal();
            var test = GUILayout.Button(new GUIContent("Test", "Output target asset entries with labels to console."));
            var build = GUILayout.Button("Build");
            var clear = GUILayout.Button(new GUIContent("Clear", "Clear asset entries and labels."));
            EditorGUILayout.EndHorizontal();

            if (test && VerifyGroup(self))
            {
                self.TestAll();
            }

            if (build && VerifyGroup(self))
            {
                self.BuildAll();
            }

            if (clear)
            {
                AddressableAssetGroupBuilder.ClearAddressing();
            }
        }

        private static bool VerifyGroup(AddressableAssetGroupBuilderBatch builderBatch)
        {
            var groupVerifier = new AddressableAssetGroupBuilder.GroupVerifier();

            foreach (var builder in builderBatch.builders)
            {
                groupVerifier.Join(builder);
            }

            return groupVerifier.Verify();
        }
    }
}
