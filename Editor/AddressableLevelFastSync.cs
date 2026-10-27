using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace Eternals.LevelManagement.Editor
{
    /// <summary>
    /// Faster alternative to <see cref="AddressableLevelCopyUtility"/> for large level lists. It produces the same result
    /// (remote copies, ordered addresses, label, group) but batches every asset operation with
    /// <see cref="AssetDatabase.StartAssetEditing"/>, skips Undo, and rewrites a copy only when its content changed.
    /// </summary>
    public static class AddressableLevelFastSync
    {
        private sealed class SyncSet
        {
            public string Label;               // Addressables label (the main set uses the manager's Addressable Label)
            public string SourceName;          // display only
            public string DestinationFolder;
            public List<UnityEngine.Object> Sources;
        }

        public static void Run(
            LevelDataManagerSettingsBase manager,
            AddressableAssetSettings settings,
            AddressableAssetGroup group)
        {
            if (manager == null)
                return;

            if (settings == null)
            {
                EditorUtility.DisplayDialog("Fast sync", "Addressables Settings are not configured.", "OK");
                return;
            }

            if (group == null || group.Settings != settings || group.ReadOnly)
            {
                EditorUtility.DisplayDialog("Fast sync", "Select an editable Remote Addressables Group first.", "OK");
                return;
            }

            if (!TryBuildSets(manager, out List<SyncSet> sets, out string error))
            {
                EditorUtility.DisplayDialog("Fast sync", error, "OK");
                return;
            }

            string summary = string.Empty;
            int total = 0;
            foreach (SyncSet set in sets)
            {
                summary += $"{set.SourceName}: {set.Sources.Count} level(s) -> {set.DestinationFolder} [{set.Label}]\n";
                total += set.Sources.Count;
            }

            if (!EditorUtility.DisplayDialog(
                    "Fast sync remote copies",
                    $"{summary}\n{total} level(s) in total, group '{group.Name}'. There is no Undo. " +
                    "Existing copies are updated in place (GUIDs are kept) and only when their content changed. Continue?",
                    "Sync",
                    "Cancel"))
            {
                return;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            int created = 0, updated = 0, unchanged = 0, unlabeled = 0;
            try
            {
                for (int s = 0; s < sets.Count; s++)
                {
                    SyncSet set = sets[s];
                    string title = $"Fast sync ({s + 1}/{sets.Count}) {set.SourceName}";

                    List<string> destinationPaths = CopyAssets(set, title, ref created, ref updated, ref unchanged);
                    if (destinationPaths == null)
                    {
                        Debug.LogWarning("[AddressableLevelFastSync] Stopped before finishing. Run it again to continue.", manager);
                        return;
                    }

                    List<string> guids = RegisterAddressables(settings, group, set, destinationPaths, title, ref unlabeled);
                    if (guids == null)
                    {
                        Debug.LogWarning("[AddressableLevelFastSync] Stopped before finishing. Run it again to continue.", manager);
                        return;
                    }

                    if (set.Label == manager.AddressableLabel)
                        manager.SetAddressableLevelGuids(guids);
                }

                manager.SetRemoteAddressableGroupGuid(group.Guid);
                EditorUtility.SetDirty(manager);
                EditorUtility.DisplayProgressBar("Fast sync", "Saving Addressables settings...", 1f);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Debug.Log(
                $"[AddressableLevelFastSync] Finished in {watch.Elapsed.TotalSeconds:F1}s: created {created}, updated {updated}, " +
                $"unchanged {unchanged}, labels removed from sources/stale entries {unlabeled}.",
                manager);
        }

        private static bool TryBuildSets(LevelDataManagerSettingsBase manager, out List<SyncSet> sets, out string error)
        {
            sets = new List<SyncSet>();
            error = null;

            if (string.IsNullOrWhiteSpace(manager.AddressableLabel))
            {
                error = "Set the Addressable Label for the main levels first.";
                return false;
            }

            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { manager.AddressableLabel.Trim() };

            int mainCount = manager.LocalLevelCount;
            var main = new List<UnityEngine.Object>(mainCount);
            for (int i = 0; i < mainCount; i++)
                main.Add(manager.GetLocalLevelObject(i));
            if (main.Count > 0)
            {
                sets.Add(new SyncSet
                {
                    Label = manager.AddressableLabel,
                    SourceName = "main",
                    DestinationFolder = Normalize(manager.AddressableLevelsFolder),
                    Sources = main
                });
            }

            for (int o = 0; o < manager.LevelOptions.Count; o++)
            {
                LevelDataOption option = manager.LevelOptions[o];
                string label = option.label?.Trim();
                if (string.IsNullOrWhiteSpace(label) ||
                    label.Equals(LevelDataManagerSettingsBase.MainLevelOption, StringComparison.OrdinalIgnoreCase) ||
                    !labels.Add(label))
                {
                    error = $"Option {o + 1} needs a unique label that is different from 'main' and from the main Addressable Label.";
                    return false;
                }

                int count = manager.GetLocalOptionLevelCount(label);
                if (count == 0)
                    continue; // no levels yet (Update Local Level List was not run for it)

                var sources = new List<UnityEngine.Object>(count);
                for (int i = 0; i < count; i++)
                    sources.Add(manager.GetLocalOptionLevelObject(label, i));

                string folder = Normalize(option.addressableFolderPath);
                if (string.IsNullOrEmpty(folder))
                    folder = Normalize(option.localFolderPath) + "Addressable";

                sets.Add(new SyncSet { Label = label, SourceName = label, DestinationFolder = folder, Sources = sources });
            }

            if (sets.Count == 0)
            {
                error = "The local level list is empty. Click 'Update Local Level List' first.";
                return false;
            }

            foreach (SyncSet set in sets)
            {
                if (!set.DestinationFolder.StartsWith("Assets/", StringComparison.Ordinal) ||
                    set.DestinationFolder.IndexOf("/Resources", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    error = $"The copy folder of '{set.SourceName}' must be under Assets and outside Resources folders: '{set.DestinationFolder}'.";
                    return false;
                }

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < set.Sources.Count; i++)
                {
                    UnityEngine.Object source = set.Sources[i];
                    string path = source != null ? AssetDatabase.GetAssetPath(source) : null;
                    if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal))
                    {
                        error = $"'{set.SourceName}' has an empty level at index {i} or one that is not under Assets.";
                        return false;
                    }

                    if (!names.Add(Path.GetFileName(path)))
                    {
                        error = $"'{set.SourceName}' has two levels with the same file name '{Path.GetFileName(path)}'.";
                        return false;
                    }
                }
            }

            return true;
        }

        // Creates or updates the copies in a single import. Returns the destination paths in source order, or null when stopped.
        private static List<string> CopyAssets(SyncSet set, string title, ref int created, ref int updated, ref int unchanged)
        {
            EnsureFolder(set.DestinationFolder);

            var destinationPaths = new List<string>(set.Sources.Count);
            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < set.Sources.Count; i++)
                {
                    if (i % 50 == 0 &&
                        EditorUtility.DisplayCancelableProgressBar(title, $"Copying assets {i}/{set.Sources.Count}", (float)i / set.Sources.Count))
                    {
                        return null;
                    }

                    UnityEngine.Object source = set.Sources[i];
                    string destinationPath = set.DestinationFolder + "/" + Path.GetFileName(AssetDatabase.GetAssetPath(source));
                    destinationPaths.Add(destinationPath);

                    UnityEngine.Object existing = AssetDatabase.LoadMainAssetAtPath(destinationPath);
                    if (existing == null)
                    {
                        AssetDatabase.CreateAsset(UnityEngine.Object.Instantiate(source), destinationPath);
                        created++;
                    }
                    else if (existing.GetType() != source.GetType())
                    {
                        Debug.LogError($"[AddressableLevelFastSync] '{destinationPath}' already contains an asset of a different type.");
                        return null;
                    }
                    else if (EditorJsonUtility.ToJson(existing) != EditorJsonUtility.ToJson(source))
                    {
                        EditorUtility.CopySerialized(source, existing);
                        EditorUtility.SetDirty(existing);
                        updated++;
                    }
                    else
                    {
                        unchanged++;
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            return destinationPaths;
        }

        // Registers the copies in the group, sets ordered addresses (same format as the package copy utility), applies the label,
        // and removes the label from the local sources and from stale copies that are no longer in the list.
        private static List<string> RegisterAddressables(
            AddressableAssetSettings settings,
            AddressableAssetGroup group,
            SyncSet set,
            List<string> destinationPaths,
            string title,
            ref int unlabeled)
        {
            var guids = new List<string>(destinationPaths.Count);
            var expected = new HashSet<string>(destinationPaths, StringComparer.OrdinalIgnoreCase);
            var changed = new HashSet<AddressableAssetGroup> { group };
            string prefix = set.Label.Replace('/', '_').Replace('\\', '_').Trim();

            for (int i = 0; i < destinationPaths.Count; i++)
            {
                if (i % 100 == 0 &&
                    EditorUtility.DisplayCancelableProgressBar(title, $"Registering Addressables {i}/{destinationPaths.Count}", (float)i / destinationPaths.Count))
                {
                    return null;
                }

                string guid = AssetDatabase.AssetPathToGUID(destinationPaths[i]);
                AddressableAssetEntry existing = settings.FindAssetEntry(guid, true);
                if (existing?.parentGroup != null)
                    changed.Add(existing.parentGroup);

                AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, group, false, false);
                if (entry == null)
                {
                    Debug.LogError($"[AddressableLevelFastSync] Could not register '{destinationPaths[i]}' as Addressable.");
                    return null;
                }

                entry.SetAddress($"{prefix}/{i:D6}_{Path.GetFileNameWithoutExtension(destinationPaths[i])}", false);
                entry.SetLabel(set.Label, true, true, false);
                guids.Add(guid);
            }

            for (int i = 0; i < set.Sources.Count; i++)
                unlabeled += Unlabel(settings, AssetDatabase.GetAssetPath(set.Sources[i]), set.Label, changed);

            if (AssetDatabase.IsValidFolder(set.DestinationFolder))
            {
                foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { set.DestinationFolder }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!expected.Contains(path) &&
                        string.Equals(Normalize(Path.GetDirectoryName(path)), set.DestinationFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        unlabeled += Unlabel(settings, path, set.Label, changed);
                    }
                }
            }

            foreach (AddressableAssetGroup changedGroup in changed)
                EditorUtility.SetDirty(changedGroup);
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, changed, true, true);
            EditorUtility.SetDirty(settings);
            return guids;
        }

        private static int Unlabel(
            AddressableAssetSettings settings,
            string assetPath,
            string label,
            HashSet<AddressableAssetGroup> changed)
        {
            AddressableAssetEntry entry = settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(assetPath), true);
            if (entry == null || !entry.labels.Contains(label) || entry.ReadOnly)
                return 0;

            if (entry.parentGroup != null)
                changed.Add(entry.parentGroup);
            entry.SetLabel(label, false, true, false);
            return 1;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;

            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static string Normalize(string folder)
        {
            return string.IsNullOrWhiteSpace(folder) ? string.Empty : folder.Replace('\\', '/').TrimEnd('/');
        }
    }
}
