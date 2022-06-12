using System;
using UnityEngine;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;

namespace AddressableAssets.GroupBuilder
{
    [CreateAssetMenu(
        fileName = "AddressableAssetGroupBuilderBatch_",
        menuName = "Addressables/Group Builder Batch")]
    public sealed class AddressableAssetGroupBuilderBatch : ScriptableObject
    {
        public string defaultGroupName = "DefaultGroup";
        public AddressableAssetGroupTemplate defaultGroupTemplate;
        public AddressableAssetGroupBuilder[] builders = Array.Empty<AddressableAssetGroupBuilder>();

        public void TestAll()
        {
            foreach (var builder in builders)
            {
                builder.Test();
            }
        }

        public void BuildAll()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var work = new AddressableAssetGroupBuilder.Work();
            try
            {
                var addressableAssetSettings = AddressableAssetSettingsDefaultObject.Settings;

                AddressableAssetGroupBuilder.RemoveMissingGroupReferences();

                // apply defautl group
                if (!string.IsNullOrEmpty(defaultGroupName))
                {
                    if (defaultGroupTemplate == null)
                    {
                        Debug.LogError("Default Group Template must be set.");
                        return;
                    }

                    var group = addressableAssetSettings.FindGroup(defaultGroupName);
                    if (group == null)
                    {
                        group = addressableAssetSettings.CreateGroup(
                            defaultGroupName, true, false, false, null, defaultGroupTemplate.GetTypes());
                    }
                    defaultGroupTemplate.ApplyToAddressableAssetGroup(group);
                }

                // build groups
                foreach (var builder in builders)
                {
                    builder.Build(work);
                }

                // remove unused groups
                foreach (var group in addressableAssetSettings.groups.ToArray())
                {
                    if (group.Default || work.groupMap.ContainsKey(group.Name))
                    {
                        continue;
                    }
                    addressableAssetSettings.RemoveGroup(group);
                }

                AddressableAssetGroupBuilder.Finalize(work);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                throw;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Debug.Log($"AddressablesGroupBuilderBatch finished. {stopwatch.Elapsed.TotalSeconds:F3} seconds");
            }
        }
    }
}
