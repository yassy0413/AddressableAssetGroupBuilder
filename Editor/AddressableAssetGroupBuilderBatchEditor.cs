using UnityEditor;
using UnityEngine;

namespace AddressableAssetGroupBuilder
{
    [CustomEditor(typeof(AddressableAssetGroupBuilderBatch))]
    public sealed class AddressableAssetGroupBuilderBatchEditor : Editor
    {
        private static readonly GUIContent TestContent =
            new("Test", "Output target asset entries with labels to console.");
        private static readonly GUIContent ClearContent = new("Clear", "Clear asset entries and labels.");

        private SerializedProperty keepGroupNamesRegexPatternProp;

        private void OnEnable()
        {
            keepGroupNamesRegexPatternProp = serializedObject.FindProperty("keepGroupNamesRegexPattern");
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
                EditorGUILayout.PropertyField(keepGroupNamesRegexPatternProp, true);
                serializedObject.ApplyModifiedProperties();
            }

            DrawAutoAddressingStatus(self);

            EditorGUILayout.BeginHorizontal();
            var test = GUILayout.Button(TestContent);
            var build = GUILayout.Button("Build");
            var clear = GUILayout.Button(ClearContent);
            EditorGUILayout.EndHorizontal();

            if (test && self.VerifyGroups())
            {
                self.TestAll();
            }

            if (build && self.VerifyGroups())
            {
                self.BuildAll();
            }

            if (clear)
            {
                AddressableAssetGroupBuilder.ClearAddressing(self.keepGroupNamesRegexPattern);
            }
        }

        private static void DrawAutoAddressingStatus(AddressableAssetGroupBuilderBatch self)
        {
            if (!self.autoAddressing)
            {
                return;
            }

            var active = AutoAddressing.ActiveBatch;
            if (active == null)
            {
                EditorGUILayout.HelpBox(
                    "Auto Addressing is enabled on this batch but no active batch could be determined. " +
                    "Check the console: only one batch may enable Auto Addressing.",
                    MessageType.Error);
            }
            else if (active != self)
            {
                EditorGUILayout.HelpBox($"Auto Addressing is handled by [{active.name}], not this batch.",
                    MessageType.Warning);
            }
            else if (!AutoAddressing.Enabled)
            {
                EditorGUILayout.HelpBox(
                    $"Auto Addressing is active on this batch, but disabled for this user ({AutoAddressing.MenuPath}).",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"Auto Addressing is active on this batch. Imported / moved / deleted assets are addressed incrementally; " +
                    $"up to {AutoAddressing.MaxAssetsPerUpdate} changed / removed paths are processed per editor update.",
                    MessageType.Info);
            }
        }
    }
}