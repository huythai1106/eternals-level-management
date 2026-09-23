using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Eternals.LevelManagement.Editor
{
    public sealed class AddressableLevelCopyResult
    {
        internal AddressableLevelCopyResult(
            List<string> assetGuids,
            int createdCount,
            int updatedCount,
            int removedLabelCount)
        {
            AssetGuids = assetGuids.AsReadOnly();
            CreatedCount = createdCount;
            UpdatedCount = updatedCount;
            RemovedLabelCount = removedLabelCount;
        }

        public IReadOnlyList<string> AssetGuids { get; }
        public int CreatedCount { get; }
        public int UpdatedCount { get; }
        public int RemovedLabelCount { get; }
    }

    /// <summary>
    /// Creates a separate, ordered Addressables copy of a local level list.
    /// Existing destination assets are updated in place so their GUIDs remain stable
    /// across Addressables content updates.
    /// </summary>
    public static class AddressableLevelCopyUtility
    {
        public static string GetSiblingAddressableFolder(string sourceFolder, string suffix = "Addressable")
        {
            string normalizedSource = NormalizeFolder(sourceFolder);
            if (string.IsNullOrEmpty(normalizedSource) || string.IsNullOrEmpty(suffix))
                return string.Empty;

            return normalizedSource + suffix;
        }

        public static bool CreateOrUpdateCopies<TLevel>(
            AddressableAssetSettings settings,
            AddressableAssetGroup targetGroup,
            IReadOnlyList<TLevel> sourceLevels,
            string destinationFolder,
            string label,
            string undoName,
            out AddressableLevelCopyResult result,
            out string error)
            where TLevel : UnityEngine.Object
        {
            result = null;
            error = null;

            if (!TryValidate(
                    settings,
                    targetGroup,
                    sourceLevels,
                    destinationFolder,
                    label,
                    out string normalizedDestination,
                    out List<CopyItem<TLevel>> copyItems,
                    out error))
            {
                return false;
            }

            if (!TryCollectEntriesToUnlabel(
                    settings,
                    copyItems,
                    normalizedDestination,
                    label,
                    out List<AddressableAssetEntry> entriesToUnlabel,
                    out error))
            {
                return false;
            }

            if (!EnsureFolderExists(normalizedDestination, out error))
                return false;

            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);

            var createdPaths = new List<string>();
            int createdCount = 0;
            int updatedCount = 0;

            for (int i = 0; i < copyItems.Count; i++)
            {
                CopyItem<TLevel> item = copyItems[i];
                if (item.DestinationAsset != null)
                    continue;

                if (!AssetDatabase.CopyAsset(item.SourcePath, item.DestinationPath))
                {
                    RollbackCreatedAssets(createdPaths);
                    error = $"Could not copy '{item.SourcePath}' to '{item.DestinationPath}'.";
                    return false;
                }

                TLevel createdAsset = AssetDatabase.LoadAssetAtPath<TLevel>(item.DestinationPath);
                if (createdAsset == null)
                {
                    createdPaths.Add(item.DestinationPath);
                    RollbackCreatedAssets(createdPaths);
                    error = $"Unity copied '{item.SourcePath}', but the destination asset could not be loaded.";
                    return false;
                }

                item.DestinationAsset = createdAsset;
                createdPaths.Add(item.DestinationPath);
                Undo.RegisterCreatedObjectUndo(createdAsset, undoName);
                createdCount++;
            }

            for (int i = 0; i < copyItems.Count; i++)
            {
                CopyItem<TLevel> item = copyItems[i];
                if (createdPaths.Contains(item.DestinationPath))
                    continue;

                Undo.RegisterCompleteObjectUndo(item.DestinationAsset, undoName);
                EditorUtility.CopySerialized(item.SourceAsset, item.DestinationAsset);
                EditorUtility.SetDirty(item.DestinationAsset);
                updatedCount++;
            }

            var assetGuids = new List<string>(copyItems.Count);
            var changedGroups = new HashSet<AddressableAssetGroup>();
            changedGroups.Add(targetGroup);

            for (int i = 0; i < copyItems.Count; i++)
            {
                string guid = AssetDatabase.AssetPathToGUID(copyItems[i].DestinationPath);
                AddressableAssetEntry existingEntry = settings.FindAssetEntry(guid, true);
                if (existingEntry?.parentGroup != null)
                    changedGroups.Add(existingEntry.parentGroup);

                assetGuids.Add(guid);
            }

            for (int i = 0; i < entriesToUnlabel.Count; i++)
            {
                if (entriesToUnlabel[i].parentGroup != null)
                    changedGroups.Add(entriesToUnlabel[i].parentGroup);
            }

            Undo.RegisterCompleteObjectUndo(settings, undoName);
            foreach (AddressableAssetGroup group in changedGroups)
                Undo.RegisterCompleteObjectUndo(group, undoName);

            string addressPrefix = SanitizeAddressSegment(label);
            for (int i = 0; i < assetGuids.Count; i++)
            {
                AddressableAssetEntry entry = settings.CreateOrMoveEntry(
                    assetGuids[i],
                    targetGroup,
                    false,
                    false);
                if (entry == null)
                {
                    error = $"Could not register '{copyItems[i].DestinationPath}' as Addressable.";
                    return false;
                }

                string assetName = Path.GetFileNameWithoutExtension(copyItems[i].DestinationPath);
                entry.SetAddress($"{addressPrefix}/{i:D6}_{assetName}", false);
                entry.SetLabel(label, true, true, false);
            }

            for (int i = 0; i < entriesToUnlabel.Count; i++)
                entriesToUnlabel[i].SetLabel(label, false, true, false);

            foreach (AddressableAssetGroup group in changedGroups)
                EditorUtility.SetDirty(group);
            settings.SetDirty(
                AddressableAssetSettings.ModificationEvent.BatchModification,
                changedGroups,
                true,
                true);
            EditorUtility.SetDirty(settings);
            Undo.CollapseUndoOperations(undoGroup);
            AssetDatabase.SaveAssets();

            result = new AddressableLevelCopyResult(
                assetGuids,
                createdCount,
                updatedCount,
                entriesToUnlabel.Count);
            return true;
        }

        private static bool TryValidate<TLevel>(
            AddressableAssetSettings settings,
            AddressableAssetGroup targetGroup,
            IReadOnlyList<TLevel> sourceLevels,
            string destinationFolder,
            string label,
            out string normalizedDestination,
            out List<CopyItem<TLevel>> copyItems,
            out string error)
            where TLevel : UnityEngine.Object
        {
            normalizedDestination = NormalizeFolder(destinationFolder);
            copyItems = null;
            error = null;

            if (settings == null)
            {
                error = "Create Addressables Settings before creating remote level copies.";
                return false;
            }

            if (targetGroup == null || targetGroup.Settings != settings ||
                targetGroup.ReadOnly || !targetGroup.HasSchema<BundledAssetGroupSchema>())
            {
                error = "Select an editable bundled Addressables group from this project.";
                return false;
            }

            if (sourceLevels == null || sourceLevels.Count == 0)
            {
                error = "The local level list is empty.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(label))
            {
                error = "Set a non-empty Addressables label before creating remote level copies.";
                return false;
            }

            if (!normalizedDestination.StartsWith("Assets/", StringComparison.Ordinal) ||
                normalizedDestination.IndexOf("/Resources", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                error = "The Addressable copy folder must be under Assets and outside Resources folders.";
                return false;
            }

            var destinationPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            copyItems = new List<CopyItem<TLevel>>(sourceLevels.Count);
            for (int i = 0; i < sourceLevels.Count; i++)
            {
                TLevel source = sourceLevels[i];
                if (source == null)
                {
                    error = $"Local level entry {i} is null.";
                    return false;
                }

                string sourcePath = AssetDatabase.GetAssetPath(source);
                if (string.IsNullOrEmpty(sourcePath) ||
                    !sourcePath.StartsWith("Assets/", StringComparison.Ordinal) ||
                    !AssetDatabase.IsMainAsset(source))
                {
                    error = $"'{source.name}' must be a main asset stored under Assets.";
                    return false;
                }

                string destinationPath = normalizedDestination + "/" + Path.GetFileName(sourcePath);
                if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"The destination for '{source.name}' is the same as its local asset path.";
                    return false;
                }

                if (!destinationPaths.Add(destinationPath))
                {
                    error = $"More than one local level would be copied to '{destinationPath}'. Use unique asset file names.";
                    return false;
                }

                UnityEngine.Object existingObject = AssetDatabase.LoadMainAssetAtPath(destinationPath);
                if (existingObject != null && existingObject.GetType() != source.GetType())
                {
                    error = $"'{destinationPath}' already contains a different asset type.";
                    return false;
                }

                copyItems.Add(new CopyItem<TLevel>(
                    source,
                    sourcePath,
                    destinationPath,
                    existingObject as TLevel));
            }

            return true;
        }

        private static bool TryCollectEntriesToUnlabel<TLevel>(
            AddressableAssetSettings settings,
            IReadOnlyList<CopyItem<TLevel>> copyItems,
            string destinationFolder,
            string label,
            out List<AddressableAssetEntry> entries,
            out string error)
            where TLevel : UnityEngine.Object
        {
            entries = new List<AddressableAssetEntry>();
            error = null;
            var seenGuids = new HashSet<string>(StringComparer.Ordinal);
            var expectedDestinationPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < copyItems.Count; i++)
            {
                CopyItem<TLevel> item = copyItems[i];
                expectedDestinationPaths.Add(item.DestinationPath);

                string sourceGuid = AssetDatabase.AssetPathToGUID(item.SourcePath);
                AddressableAssetEntry sourceEntry = settings.FindAssetEntry(sourceGuid, true);
                if (sourceEntry != null && sourceEntry.labels.Contains(label))
                {
                    if (!TryAddEditableEntry(sourceEntry, sourceGuid, entries, seenGuids, out error))
                        return false;
                }
            }

            string[] guids = AssetDatabase.IsValidFolder(destinationFolder)
                ? AssetDatabase.FindAssets(string.Empty, new[] { destinationFolder })
                : Array.Empty<string>();
            for (int i = 0; i < guids.Length; i++)
            {
                string guid = guids[i];
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (expectedDestinationPaths.Contains(path) ||
                    !string.Equals(
                        NormalizeFolder(Path.GetDirectoryName(path)),
                        destinationFolder,
                        StringComparison.OrdinalIgnoreCase) ||
                    AssetDatabase.LoadAssetAtPath<TLevel>(path) == null)
                {
                    continue;
                }

                AddressableAssetEntry entry = settings.FindAssetEntry(guid, true);
                if (entry != null && entry.labels.Contains(label) &&
                    !TryAddEditableEntry(entry, guid, entries, seenGuids, out error))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryAddEditableEntry(
            AddressableAssetEntry entry,
            string guid,
            List<AddressableAssetEntry> entries,
            HashSet<string> seenGuids,
            out string error)
        {
            error = null;
            if (entry.ReadOnly || entry.parentGroup == null || entry.parentGroup.ReadOnly)
            {
                error = $"Cannot remove the remote level label from read-only Addressable entry " +
                        $"'{AssetDatabase.GUIDToAssetPath(guid)}'.";
                return false;
            }

            if (seenGuids.Add(guid))
                entries.Add(entry);
            return true;
        }

        private static bool EnsureFolderExists(string folderPath, out string error)
        {
            error = null;
            if (AssetDatabase.IsValidFolder(folderPath))
                return true;

            string[] parts = folderPath.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    string guid = AssetDatabase.CreateFolder(current, parts[i]);
                    if (string.IsNullOrEmpty(guid))
                    {
                        error = $"Could not create destination folder '{next}'.";
                        return false;
                    }
                }

                current = next;
            }

            return true;
        }

        private static void RollbackCreatedAssets(List<string> createdPaths)
        {
            for (int i = createdPaths.Count - 1; i >= 0; i--)
                AssetDatabase.DeleteAsset(createdPaths[i]);
            AssetDatabase.Refresh();
        }

        private static string NormalizeFolder(string folder)
        {
            return string.IsNullOrWhiteSpace(folder)
                ? string.Empty
                : folder.Replace('\\', '/').TrimEnd('/');
        }

        private static string SanitizeAddressSegment(string value)
        {
            return value.Replace('/', '_').Replace('\\', '_').Trim();
        }

        private sealed class CopyItem<TLevel>
            where TLevel : UnityEngine.Object
        {
            public CopyItem(
                TLevel sourceAsset,
                string sourcePath,
                string destinationPath,
                TLevel destinationAsset)
            {
                SourceAsset = sourceAsset;
                SourcePath = sourcePath;
                DestinationPath = destinationPath;
                DestinationAsset = destinationAsset;
            }

            public TLevel SourceAsset { get; }
            public string SourcePath { get; }
            public string DestinationPath { get; }
            public TLevel DestinationAsset { get; set; }
        }
    }
}
