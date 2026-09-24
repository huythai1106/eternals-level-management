using System;
using System.Collections.Generic;
using System.IO;
using Eternals.LevelManagement;
using UnityEditor.AddressableAssets;
using UnityEditor;
using UnityEngine;
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

    /// <summary>
    /// Shared Inspector and catalog authoring controls for game-specific level managers.
    /// The package editor draws the shared settings and catalog authoring controls.
    /// </summary>
    [InitializeOnLoad]
    public static class LevelDataManagerEditorUtility
    {
        private const string UndoPrefix = "Eternals.LevelManagement: ";

        static LevelDataManagerEditorUtility()
        {
            Undo.undoRedoEvent += OnUndoRedo;
        }

        public static void DrawLevelManagerControls(LevelDataManagerSettingsBase manager)
        {
            if (manager == null)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Level Catalog (Eternals Level Management)", EditorStyles.boldLabel);
            DrawCommonSettings(manager);

            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                EditorGUILayout.HelpBox(
                    "Addressables Settings are not configured. Local level catalog tools are still available.",
                    MessageType.Warning);
                DrawUpdateLocalLevels(manager);
                return;
            }

            AddressableAssetGroup targetGroup = DrawRemoteGroup(manager, settings);
            List<UnityEngine.Object> localLevels = GetLocalLevels(manager);
            List<string> remoteGuids = CollectRemoteLevelGuids(manager);
            AddressableLevelRegistrationUtility.GetCounts(settings, remoteGuids, manager.AddressableLabel,
                out int registeredCount, out int labelledCount);

            EditorGUILayout.HelpBox(
                $"Local levels: {manager.LocalLevelCount}. Remote copies: {remoteGuids.Count}. " +
                $"{registeredCount}/{remoteGuids.Count} registered, {labelledCount}/{remoteGuids.Count} labelled '{manager.AddressableLabel}'.\n" +
                $"Configured A/B options: {manager.LevelOptions.Count}.\n" +
                $"Local folder: {manager.LevelsFolder}\nRemote copy folder: {manager.AddressableLevelsFolder}\n" +
                "Local levels remain in the player build as the offline fallback.",
                MessageType.Info);

            if (manager.UseAddressables && manager.LevelOptions.Count > 0 && !manager.DiscoverAddressablesAtRuntime)
            {
                EditorGUILayout.HelpBox(
                    "A/B option Addressables are discovered by label at runtime. Enable 'Discover Addressables At Runtime' " +
                    "to use remote option level sets; local option fallback remains available either way.",
                    MessageType.Warning);
            }

            DrawUpdateLocalLevels(manager);
            using (new EditorGUI.DisabledScope(
                       EditorApplication.isPlayingOrWillChangePlaymode ||
                       (localLevels.Count == 0 && manager.LevelOptions.Count == 0)))
            {
                if (GUILayout.Button("Create / Sync Remote Addressable Copies", GUILayout.Height(30f)))
                    CreateOrSyncRemoteCopies(manager, settings, targetGroup, localLevels);
            }

            bool allRegisteredAndLabelled = remoteGuids.Count > 0 &&
                                            registeredCount == remoteGuids.Count &&
                                            labelledCount == remoteGuids.Count;
            using (new EditorGUI.DisabledScope(
                       EditorApplication.isPlayingOrWillChangePlaymode ||
                       remoteGuids.Count == 0 || !allRegisteredAndLabelled))
            {
                if (GUILayout.Button("Disable Remote Addressable Registration"))
                    SetRemoteRegistration(manager, settings, targetGroup, remoteGuids, false);
            }

            if (remoteGuids.Count > 0 && !allRegisteredAndLabelled)
            {
                EditorGUILayout.HelpBox(
                    "Remote registration is incomplete. Run Create / Sync Remote Addressable Copies to repair the group, labels, and level order.",
                    MessageType.Warning);
            }
        }

        private static void DrawCommonSettings(LevelDataManagerSettingsBase manager)
        {
            string localFolder = EditorGUILayout.TextField("Local Level Folder", manager.LevelsFolder);
            if (!string.Equals(localFolder, manager.LevelsFolder, StringComparison.Ordinal))
                RecordAndDirty(manager, "Change Local Level Folder", () => manager.SetLevelFolderPath(localFolder));

            string remoteFolder = EditorGUILayout.TextField(
                "Addressable Copy Folder", manager.AddressableLevelsFolder);
            if (!string.Equals(remoteFolder, manager.AddressableLevelsFolder, StringComparison.Ordinal))
                RecordAndDirty(manager, "Change Addressable Copy Folder", () => manager.SetAddressableLevelFolderPath(remoteFolder));

            int maxLevel = EditorGUILayout.IntField("Max Levels (-1 = all)", manager.MaxLevel);
            if (maxLevel < -1)
                maxLevel = -1;
            if (maxLevel != manager.MaxLevel)
                RecordAndDirty(manager, "Change Maximum Level Count", () => manager.SetMaxLevel(maxLevel));
        }

        private static AddressableAssetGroup DrawRemoteGroup(
            LevelDataManagerSettingsBase manager,
            AddressableAssetSettings settings)
        {
            var groups = new List<AddressableAssetGroup>();
            var labels = new List<string>();
            int selectedIndex = 0;
            groups.Add(null);
            labels.Add("<Select group>");

            if (settings.groups != null)
            {
                for (int i = 0; i < settings.groups.Count; i++)
                {
                    AddressableAssetGroup group = settings.groups[i];
                    if (group == null)
                        continue;
                    groups.Add(group);
                    labels.Add(group.Name);
                    if (string.Equals(group.Guid, manager.RemoteAddressableGroupGuid, StringComparison.Ordinal))
                        selectedIndex = groups.Count - 1;
                }
            }

            if (selectedIndex == 0 && groups.Count > 1)
            {
                AddressableAssetGroup preferred = settings.FindGroup("Level_Remote") ??
                                                  settings.FindGroup("LevelData_Remote") ??
                                                  settings.DefaultGroup;
                int preferredIndex = groups.IndexOf(preferred);
                if (preferredIndex > 0)
                    selectedIndex = preferredIndex;
            }

            int nextIndex = EditorGUILayout.Popup("Remote Addressables Group", selectedIndex, labels.ToArray());
            if (nextIndex != selectedIndex && nextIndex >= 0 && nextIndex < groups.Count)
            {
                AddressableAssetGroup selectedGroup = groups[nextIndex];
                RecordAndDirty(
                    manager,
                    "Change Remote Addressables Group",
                    () => manager.SetRemoteAddressableGroupGuid(selectedGroup != null ? selectedGroup.Guid : string.Empty));
                return selectedGroup;
            }

            return groups[selectedIndex];
        }

        private static void DrawUpdateLocalLevels(LevelDataManagerSettingsBase manager)
        {
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (!GUILayout.Button("Update Local Level List"))
                    return;

                string folder = NormalizeFolder(manager.LevelsFolder);
                if (!AssetDatabase.IsValidFolder(folder))
                {
                    EditorUtility.DisplayDialog(
                        "Invalid level folder",
                        $"'{folder}' is not a valid project folder.",
                        "OK");
                    return;
                }

                var optionLevels = new List<List<UnityEngine.Object>>(manager.LevelOptions.Count);
                var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(manager.AddressableLabel))
                    seenLabels.Add(manager.AddressableLabel.Trim());
                for (int optionIndex = 0; optionIndex < manager.LevelOptions.Count; optionIndex++)
                {
                    LevelDataOption option = manager.LevelOptions[optionIndex];
                    if (string.IsNullOrWhiteSpace(option.label) ||
                        string.Equals(option.label.Trim(), LevelDataManagerSettingsBase.MainLevelOption, StringComparison.OrdinalIgnoreCase) ||
                        !seenLabels.Add(option.label.Trim()))
                    {
                        EditorUtility.DisplayDialog(
                            "Invalid level option",
                            $"Option {optionIndex + 1} must have a unique non-empty label different from the main Addressables label and 'main'.",
                            "OK");
                        return;
                    }

                    string optionFolder = NormalizeFolder(option.localFolderPath);
                    if (!AssetDatabase.IsValidFolder(optionFolder))
                    {
                        EditorUtility.DisplayDialog(
                            "Invalid option level folder",
                            $"Option '{option.label}' points to invalid folder '{optionFolder}'.",
                            "OK");
                        return;
                    }

                    if (!TryFindLevels(manager, optionFolder, out List<UnityEngine.Object> foundOptionLevels, out string error))
                    {
                        EditorUtility.DisplayDialog("Could not read option levels", error, "OK");
                        return;
                    }

                    optionLevels.Add(foundOptionLevels);
                }

                if (!TryFindLevels(manager, folder, out List<UnityEngine.Object> foundLevels, out string mainError))
                {
                    EditorUtility.DisplayDialog("Could not read local levels", mainError, "OK");
                    return;
                }

                Undo.RecordObject(manager, UndoPrefix + "Update Local Level List");
                manager.SetLevelFolderPath(folder);
                manager.SetLocalLevelObjects(foundLevels);
                for (int i = 0; i < manager.LevelOptions.Count; i++)
                    manager.SetLocalOptionLevelObjects(manager.LevelOptions[i].label, optionLevels[i]);
                EditorUtility.SetDirty(manager);
                AssetDatabase.SaveAssets();
                Debug.Log(
                    $"{manager.GetType().Name}: updated {foundLevels.Count} main level(s) and " +
                    $"{optionLevels.Count} option level set(s).",
                    manager);
            }
        }

        private static bool TryFindLevels(
            LevelDataManagerSettingsBase manager,
            string folder,
            out List<UnityEngine.Object> levels,
            out string error)
        {
            levels = null;
            error = null;
            if (!AssetDatabase.IsValidFolder(folder))
            {
                error = $"'{folder}' is not a valid project folder.";
                return false;
            }

            string[] guids = AssetDatabase.FindAssets(
                "t:" + manager.LevelDataType.Name,
                new[] { folder });
            var paths = new List<string>(guids.Length);
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!string.IsNullOrEmpty(path))
                    paths.Add(path);
            }
            paths.Sort(StringComparer.OrdinalIgnoreCase);

            int count = manager.MaxLevel < 0 ? paths.Count : Mathf.Min(manager.MaxLevel, paths.Count);
            levels = new List<UnityEngine.Object>(count);
            for (int i = 0; i < count; i++)
            {
                UnityEngine.Object level = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(paths[i]);
                if (level != null && manager.LevelDataType.IsInstanceOfType(level))
                    levels.Add(level);
            }

            return true;
        }

        private static void CreateOrSyncRemoteCopies(
            LevelDataManagerSettingsBase manager,
            AddressableAssetSettings settings,
            AddressableAssetGroup targetGroup,
            IReadOnlyList<UnityEngine.Object> localLevels)
        {
            var optionPlans = new List<OptionCopyPlan>(manager.LevelOptions.Count);
            var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(manager.AddressableLabel))
                seenLabels.Add(manager.AddressableLabel.Trim());

            for (int i = 0; i < manager.LevelOptions.Count; i++)
            {
                LevelDataOption option = manager.LevelOptions[i];
                string label = option.label?.Trim();
                string localFolder = NormalizeFolder(option.localFolderPath);
                string destinationFolder = GetOptionAddressableFolder(option);

                if (string.IsNullOrWhiteSpace(label) ||
                    string.Equals(label, LevelDataManagerSettingsBase.MainLevelOption, StringComparison.OrdinalIgnoreCase) ||
                    !seenLabels.Add(label))
                {
                    EditorUtility.DisplayDialog(
                        "Invalid level option",
                        $"Option {i + 1} must have a unique label, different from the main label and 'main'.",
                        "OK");
                    return;
                }

                if (!TryFindLevels(manager, localFolder, out List<UnityEngine.Object> optionLevels, out string findError) ||
                    optionLevels.Count == 0)
                {
                    EditorUtility.DisplayDialog(
                        "Invalid option level source",
                        string.IsNullOrEmpty(findError)
                            ? $"Option '{label}' has no level assets in '{localFolder}'."
                            : findError,
                        "OK");
                    return;
                }

                if (!IsValidAddressableFolder(destinationFolder))
                {
                    EditorUtility.DisplayDialog(
                        "Invalid option Addressable folder",
                        $"Option '{label}' destination must be under Assets and outside Resources: '{destinationFolder}'.",
                        "OK");
                    return;
                }

                optionPlans.Add(new OptionCopyPlan(label, localFolder, destinationFolder, optionLevels));
            }

            if (localLevels.Count > 0 && string.IsNullOrWhiteSpace(manager.AddressableLabel))
            {
                EditorUtility.DisplayDialog("Missing main Addressables label", "Set the main Addressable Label before syncing main levels.", "OK");
                return;
            }

            string message =
                $"Sync main levels: {localLevels.Count}\n" +
                $"Main: {manager.LevelsFolder} → {manager.AddressableLevelsFolder} [{manager.AddressableLabel}]\n\n" +
                $"A/B option sets: {optionPlans.Count}\n";
            for (int i = 0; i < optionPlans.Count; i++)
            {
                OptionCopyPlan plan = optionPlans[i];
                message += $"{plan.label}: {plan.levels.Count} level(s), {plan.localFolder} → {plan.destinationFolder}\n";
            }
            message += "\nExisting destination assets are updated in place to preserve their GUIDs. Local source assets are not modified or deleted.";
            if (!EditorUtility.DisplayDialog("Sync remote level copies", message, "Create / Sync", "Cancel"))
                return;

            const string undoName = UndoPrefix + "Sync Remote Copies";
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            Undo.RegisterCompleteObjectUndo(manager, undoName);

            int createdCount = 0;
            int updatedCount = 0;
            int removedLabelCount = 0;
            if (localLevels.Count > 0)
            {
                if (!AddressableLevelCopyUtility.CreateOrUpdateCopies(
                        settings,
                        targetGroup,
                        localLevels,
                        manager.AddressableLevelsFolder,
                        manager.AddressableLabel,
                        undoName,
                        out AddressableLevelCopyResult mainResult,
                        out string mainError))
                {
                    Undo.CollapseUndoOperations(undoGroup);
                    EditorUtility.DisplayDialog("Could not sync main levels", mainError, "OK");
                    return;
                }

                manager.SetAddressableLevelGuids(mainResult.AssetGuids);
                createdCount += mainResult.CreatedCount;
                updatedCount += mainResult.UpdatedCount;
                removedLabelCount += mainResult.RemovedLabelCount;
            }

            manager.SetRemoteAddressableGroupGuid(targetGroup.Guid);

            for (int i = 0; i < optionPlans.Count; i++)
            {
                OptionCopyPlan plan = optionPlans[i];
                manager.SetLocalOptionLevelObjects(plan.label, plan.levels);

                if (!AddressableLevelCopyUtility.CreateOrUpdateCopies(
                        settings,
                        targetGroup,
                        plan.levels,
                        plan.destinationFolder,
                        plan.label,
                        undoName,
                        out AddressableLevelCopyResult optionResult,
                        out string optionError))
                {
                    EditorUtility.SetDirty(manager);
                    AssetDatabase.SaveAssets();
                    Undo.CollapseUndoOperations(undoGroup);
                    EditorUtility.DisplayDialog(
                        "Could not sync all level options",
                        $"Option '{plan.label}' failed after earlier catalog data may already have been updated.\n\n{optionError}",
                        "OK");
                    return;
                }

                createdCount += optionResult.CreatedCount;
                updatedCount += optionResult.UpdatedCount;
                removedLabelCount += optionResult.RemovedLabelCount;
            }

            EditorUtility.SetDirty(manager);
            AssetDatabase.SaveAssets();
            Undo.CollapseUndoOperations(undoGroup);
            Debug.Log(
                $"{manager.GetType().Name}: synchronized main and {optionPlans.Count} option level set(s). " +
                $"Created: {createdCount}, updated: {updatedCount}, labels removed from local/stale entries: {removedLabelCount}.",
                manager);
        }

        private static string GetOptionAddressableFolder(LevelDataOption option)
        {
            string configuredFolder = NormalizeFolder(option.addressableFolderPath);
            return !string.IsNullOrEmpty(configuredFolder)
                ? configuredFolder
                : NormalizeFolder(option.localFolderPath) + "Addressable";
        }

        private static bool IsValidAddressableFolder(string folder)
        {
            return folder.StartsWith("Assets/", StringComparison.Ordinal) &&
                   folder.IndexOf("/Resources", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private sealed class OptionCopyPlan
        {
            public OptionCopyPlan(
                string label,
                string localFolder,
                string destinationFolder,
                List<UnityEngine.Object> levels)
            {
                this.label = label;
                this.localFolder = localFolder;
                this.destinationFolder = destinationFolder;
                this.levels = levels;
            }

            public readonly string label;
            public readonly string localFolder;
            public readonly string destinationFolder;
            public readonly List<UnityEngine.Object> levels;
        }

        private static List<UnityEngine.Object> GetLocalLevels(LevelDataManagerSettingsBase manager)
        {
            int count = manager.LocalLevelCount;
            var levels = new List<UnityEngine.Object>(count);
            for (int i = 0; i < count; i++)
                levels.Add(manager.GetLocalLevelObject(i));
            return levels;
        }

        private static List<string> CollectRemoteLevelGuids(LevelDataManagerSettingsBase manager)
        {
            IReadOnlyList<UnityEngine.AddressableAssets.AssetReference> references = manager.AddressableLevels;
            var guids = new List<string>(references != null ? references.Count : 0);
            if (references == null)
                return guids;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            string expectedFolder = NormalizeFolder(manager.AddressableLevelsFolder);
            for (int i = 0; i < references.Count; i++)
            {
                string guid = references[i]?.AssetGUID;
                if (string.IsNullOrEmpty(guid))
                    continue;
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.Equals(NormalizeFolder(Path.GetDirectoryName(path)), expectedFolder, StringComparison.OrdinalIgnoreCase) &&
                    seen.Add(guid))
                {
                    guids.Add(guid);
                }
            }
            return guids;
        }

        private static void SetRemoteRegistration(
            LevelDataManagerSettingsBase manager,
            AddressableAssetSettings settings,
            AddressableAssetGroup targetGroup,
            IReadOnlyList<string> guids,
            bool enable)
        {
            if (!AddressableLevelRegistrationUtility.TryGetChangeCount(settings, targetGroup, guids, manager.AddressableLabel, enable,
                    out int changeCount, out string error))
            {
                EditorUtility.DisplayDialog("Invalid Addressables registration", error, "OK");
                return;
            }

            string message = enable
                ? $"Register {changeCount} levels in '{targetGroup.Name}'?"
                : $"Unregister {changeCount} remote copies? The assets and local fallback levels will be kept.";
            if (!EditorUtility.DisplayDialog("Level Addressable registration", message, enable ? "Enable" : "Disable", "Cancel"))
                return;

            if (!AddressableLevelRegistrationUtility.Apply(settings, targetGroup, guids, manager.AddressableLabel, enable,
                    UndoPrefix + (enable ? "Enable" : "Disable"), out changeCount, out error))
            {
                EditorUtility.DisplayDialog("Addressables registration failed", error, "OK");
                return;
            }

            Debug.Log($"{manager.GetType().Name}: {(enable ? "registered" : "unregistered")} {changeCount} remote level(s).", manager);
        }

        private static void RecordAndDirty(UnityEngine.Object target, string undoName, Action change)
        {
            Undo.RecordObject(target, UndoPrefix + undoName);
            change();
            EditorUtility.SetDirty(target);
        }

        private static void OnUndoRedo(in UndoRedoInfo info)
        {
            if (string.IsNullOrEmpty(info.undoName) || !info.undoName.StartsWith(UndoPrefix, StringComparison.Ordinal))
                return;

            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return;

            foreach (AddressableAssetGroup group in settings.groups)
                group?.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, false, false);
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
        }

        private static string NormalizeFolder(string folder)
        {
            return string.IsNullOrWhiteSpace(folder)
                ? string.Empty
                : folder.Replace('\\', '/').TrimEnd('/');
        }
    }

    [CustomEditor(typeof(LevelDataManagerSettingsBase), true)]
    public sealed class LevelDataManagerSettingsEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (target is ILevelDataManagerValidator validator)
            {
                EditorGUILayout.Space();
                if (GUILayout.Button("Validate All Levels", GUILayout.Height(28f)))
                    validator.ValidateLevelData();
            }

            LevelDataManagerEditorUtility.DrawLevelManagerControls(
                (LevelDataManagerSettingsBase)target);
        }
    }
}
