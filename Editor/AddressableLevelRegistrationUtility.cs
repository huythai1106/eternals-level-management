using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;

namespace Eternals.LevelManagement.Editor
{
    public static class AddressableLevelRegistrationUtility
    {
        public static void GetCounts(
            AddressableAssetSettings settings,
            IReadOnlyList<string> assetGuids,
            string label,
            out int registeredCount,
            out int labelledCount)
        {
            registeredCount = 0;
            labelledCount = 0;
            if (settings == null || assetGuids == null)
                return;

            for (int i = 0; i < assetGuids.Count; i++)
            {
                AddressableAssetEntry entry = settings.FindAssetEntry(assetGuids[i], true);
                if (entry == null)
                    continue;

                registeredCount++;
                if (!string.IsNullOrEmpty(label) && entry.labels.Contains(label))
                    labelledCount++;
            }
        }

        public static bool TryGetChangeCount(
            AddressableAssetSettings settings,
            AddressableAssetGroup targetGroup,
            IReadOnlyList<string> assetGuids,
            string label,
            bool enable,
            out int changeCount,
            out string error)
        {
            changeCount = 0;
            error = null;

            if (settings == null)
            {
                error = "Create Addressables Settings before registering levels.";
                return false;
            }

            if (assetGuids == null)
            {
                error = "The asset GUID list is null.";
                return false;
            }

            if (enable)
            {
                if (targetGroup == null || targetGroup.Settings != settings ||
                    targetGroup.ReadOnly || !targetGroup.HasSchema<BundledAssetGroupSchema>())
                {
                    error = "Select an editable bundled group from this project's Addressables Settings.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(label))
                {
                    error = "Set a non-empty Addressables label before enabling level discovery.";
                    return false;
                }
            }

            for (int i = 0; i < assetGuids.Count; i++)
            {
                string guid = assetGuids[i];
                AddressableAssetEntry entry = settings.FindAssetEntry(guid, true);
                if (enable)
                {
                    if (entry == null)
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guid);
                        if (!path.StartsWith("Assets/", StringComparison.Ordinal) ||
                            path.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            error = $"Cannot register '{path}'. Assets must be under Assets and outside Resources folders.";
                            return false;
                        }

                        changeCount++;
                    }
                    else if (!entry.labels.Contains(label))
                    {
                        if (entry.ReadOnly || entry.parentGroup == null || entry.parentGroup.ReadOnly)
                        {
                            error = $"Cannot label read-only Addressable entry '{AssetDatabase.GUIDToAssetPath(guid)}'.";
                            return false;
                        }

                        changeCount++;
                    }
                }
                else if (entry != null)
                {
                    if (entry.IsSubAsset || entry.ParentEntry != null || entry.ReadOnly ||
                        entry.parentGroup == null || entry.parentGroup.ReadOnly)
                    {
                        error = $"Cannot unregister '{AssetDatabase.GUIDToAssetPath(guid)}' because it belongs to a folder/collection or is read-only.";
                        return false;
                    }

                    changeCount++;
                }
            }

            return true;
        }

        public static bool Apply(
            AddressableAssetSettings settings,
            AddressableAssetGroup targetGroup,
            IReadOnlyList<string> assetGuids,
            string label,
            bool enable,
            string undoName,
            out int changeCount,
            out string error)
        {
            if (!TryGetChangeCount(
                    settings,
                    targetGroup,
                    assetGuids,
                    label,
                    enable,
                    out changeCount,
                    out error))
            {
                return false;
            }

            if (changeCount == 0)
                return true;

            var changedGroups = new HashSet<AddressableAssetGroup>();
            for (int i = 0; i < assetGuids.Count; i++)
            {
                AddressableAssetEntry entry = settings.FindAssetEntry(assetGuids[i], true);
                AddressableAssetGroup group = entry != null ? entry.parentGroup : targetGroup;
                if (group != null)
                    changedGroups.Add(group);
            }

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            Undo.RegisterCompleteObjectUndo(settings, undoName);
            foreach (AddressableAssetGroup group in changedGroups)
                Undo.RegisterCompleteObjectUndo(group, undoName);

            for (int i = 0; i < assetGuids.Count; i++)
            {
                string guid = assetGuids[i];
                AddressableAssetEntry entry = settings.FindAssetEntry(guid, true);
                if (enable)
                {
                    if (entry == null)
                        entry = settings.CreateOrMoveEntry(guid, targetGroup, false, false);
                    entry?.SetLabel(label, true, true, false);
                }
                else if (entry != null)
                {
                    settings.RemoveAssetEntry(guid, false);
                }
            }

            foreach (AddressableAssetGroup group in changedGroups)
                EditorUtility.SetDirty(group);
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, changedGroups, true, true);
            EditorUtility.SetDirty(settings);
            Undo.CollapseUndoOperations(undoGroup);
            AssetDatabase.SaveAssets();
            return true;
        }
    }
}
