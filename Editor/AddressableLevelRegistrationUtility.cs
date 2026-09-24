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
    /// The game editor only needs to call DrawLevelManagerControls from its custom Inspector.
    /// </summary>
    [InitializeOnLoad]
    public static class LevelDataManagerEditorUtility
    {
        private const string UndoPrefix = "Eternals.LevelManagement: ";

        static LevelDataManagerEditorUtility()
        {
            Undo.undoRedoEvent += OnUndoRedo;
        }

        public static void DrawLevelManagerControls<TLevel>(LevelDataManagerBase<TLevel> manager)
            where TLevel : UnityEngine.Object
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
            List<TLevel> localLevels = GetLocalLevels(manager);
            List<string> remoteGuids = CollectRemoteLevelGuids(manager);
            AddressableLevelRegistrationUtility.GetCounts(settings, remoteGuids, manager.AddressableLabel,
                out int registeredCount, out int labelledCount);

            EditorGUILayout.HelpBox(
                $"Local levels: {manager.LocalLevelCount}. Remote copies: {remoteGuids.Count}. " +
                $"{registeredCount}/{remoteGuids.Count} registered, {labelledCount}/{remoteGuids.Count} labelled '{manager.AddressableLabel}'.\n" +
                $"Local folder: {manager.LevelsFolder}\nRemote copy folder: {manager.AddressableLevelsFolder}\n" +
                "Local levels remain in the player build as the offline fallback.",
                MessageType.Info);

            DrawUpdateLocalLevels(manager);
            using (new EditorGUI.DisabledScope(
                       EditorApplication.isPlayingOrWillChangePlaymode || localLevels.Count == 0))
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

        private static void DrawCommonSettings<TLevel>(LevelDataManagerBase<TLevel> manager)
            where TLevel : UnityEngine.Object
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

        private static AddressableAssetGroup DrawRemoteGroup<TLevel>(
            LevelDataManagerBase<TLevel> manager,
            AddressableAssetSettings settings)
            where TLevel : UnityEngine.Object
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

        private static void DrawUpdateLocalLevels<TLevel>(LevelDataManagerBase<TLevel> manager)
            where TLevel : UnityEngine.Object
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

                string filter = "t:" + typeof(TLevel).Name;
                string[] guids = AssetDatabase.FindAssets(filter, new[] { folder });
                var paths = new List<string>(guids.Length);
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    if (!string.IsNullOrEmpty(path))
                        paths.Add(path);
                }
                paths.Sort(StringComparer.OrdinalIgnoreCase);

                int count = manager.MaxLevel < 0 ? paths.Count : Mathf.Min(manager.MaxLevel, paths.Count);
                var foundLevels = new List<TLevel>(count);
                for (int i = 0; i < count; i++)
                {
                    TLevel level = AssetDatabase.LoadAssetAtPath<TLevel>(paths[i]);
                    if (level != null)
                        foundLevels.Add(level);
                }

                Undo.RecordObject(manager, UndoPrefix + "Update Local Level List");
                manager.SetLevelFolderPath(folder);
                manager.SetLocalLevels(foundLevels);
                EditorUtility.SetDirty(manager);
                AssetDatabase.SaveAssets();
                Debug.Log(
                    $"{manager.GetType().Name}: updated {foundLevels.Count} local level(s) from '{folder}'.",
                    manager);
            }
        }

        private static void CreateOrSyncRemoteCopies<TLevel>(
            LevelDataManagerBase<TLevel> manager,
            AddressableAssetSettings settings,
            AddressableAssetGroup targetGroup,
            IReadOnlyList<TLevel> localLevels)
            where TLevel : UnityEngine.Object
        {
            string message =
                $"Create or update {localLevels.Count} remote level copies?\n\n" +
                $"Source: {manager.LevelsFolder}\nDestination: {manager.AddressableLevelsFolder}\n" +
                $"Group: {(targetGroup != null ? targetGroup.Name : "<none>")}\nLabel: {manager.AddressableLabel}\n\n" +
                "Existing destination assets are updated in place to preserve their GUIDs. Local source assets are not modified or deleted.";
            if (!EditorUtility.DisplayDialog("Sync remote level copies", message, "Create / Sync", "Cancel"))
                return;

            const string undoName = UndoPrefix + "Sync Remote Copies";
            if (!AddressableLevelCopyUtility.CreateOrUpdateCopies(
                    settings,
                    targetGroup,
                    localLevels,
                    manager.AddressableLevelsFolder,
                    manager.AddressableLabel,
                    undoName,
                    out AddressableLevelCopyResult result,
                    out string error))
            {
                EditorUtility.DisplayDialog("Could not sync remote levels", error, "OK");
                return;
            }

            Undo.RecordObject(manager, undoName);
            manager.SetRemoteAddressableGroupGuid(targetGroup.Guid);
            manager.SetAddressableLevelGuids(result.AssetGuids);
            EditorUtility.SetDirty(manager);
            AssetDatabase.SaveAssets();
            Debug.Log(
                $"{manager.GetType().Name}: synchronized {result.AssetGuids.Count} remote level copy/copies in " +
                $"'{manager.AddressableLevelsFolder}'. Created: {result.CreatedCount}, updated: {result.UpdatedCount}, " +
                $"labels removed from local/stale entries: {result.RemovedLabelCount}.",
                manager);
        }

        private static List<TLevel> GetLocalLevels<TLevel>(LevelDataManagerBase<TLevel> manager)
            where TLevel : UnityEngine.Object
        {
            int count = manager.LocalLevelCount;
            var levels = new List<TLevel>(count);
            for (int i = 0; i < count; i++)
                levels.Add(manager.GetLevelData(i));
            return levels;
        }

        private static List<string> CollectRemoteLevelGuids<TLevel>(LevelDataManagerBase<TLevel> manager)
            where TLevel : UnityEngine.Object
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

        private static void SetRemoteRegistration<TLevel>(
            LevelDataManagerBase<TLevel> manager,
            AddressableAssetSettings settings,
            AddressableAssetGroup targetGroup,
            IReadOnlyList<string> guids,
            bool enable)
            where TLevel : UnityEngine.Object
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
}
