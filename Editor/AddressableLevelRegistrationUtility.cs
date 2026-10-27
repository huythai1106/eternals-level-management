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

        /// <summary>
        /// Draws the serialized fields of the manager like the default Inspector, but places the Local Addressables Group
        /// right below "Local Level Storage" and the Remote Addressables Group right below "Addressable Label".
        /// Call <see cref="DrawLevelManagerControls(LevelDataManagerSettingsBase, bool)"/> with groupFieldsDrawnInline = true afterwards.
        /// </summary>
        public static void DrawInspectorFields(SerializedObject serializedObject, LevelDataManagerSettingsBase manager)
        {
            serializedObject.UpdateIfRequiredOrScript();
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;

            SerializedProperty property = serializedObject.GetIterator();
            bool enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;

                // The generated per-option level data is shown inside each option row instead.
                if (property.propertyPath == "localOptionLevels")
                    continue;

                if (property.propertyPath == "levelOptions")
                {
                    DrawOptionsSection(serializedObject);
                    continue;
                }

                if (property.propertyPath == "priorityOption")
                {
                    DrawPriorityOption(property, serializedObject.FindProperty("levelOptions"));
                    continue;
                }

                // With Addressables storage the hard-reference list stays empty; the Addressable references are listed
                // in the Level Catalog section (below Max Levels) instead.
                if (property.propertyPath == "levels" && manager.UsesAddressableLocalLevels && manager.DirectReferenceCount == 0)
                {
                    // Draw the list in the same spot as the Direct References "Levels" field so both modes look alike.
                    serializedObject.ApplyModifiedProperties();
                    DrawLocalLevelReferences(manager, "Levels");
                    serializedObject.Update();
                    continue;
                }

                // Direct References: same paged drawer, so thousands of levels don't make the Inspector endless.
                if (property.propertyPath == "levels" && property.isArray)
                {
                    DrawPagedReferenceList(
                        property,
                        serializedObject.targetObject,
                        $"Levels ({property.arraySize})",
                        "direct",
                        true);
                    continue;
                }

                using (new EditorGUI.DisabledScope(property.propertyPath == "m_Script"))
                {
                    // Tiêu đề nhóm đã là "Local Level Storage" nên trường này đặt tên khác cho khỏi trùng.
                    if (property.propertyPath == "localLevelStorage")
                        EditorGUILayout.PropertyField(property, new GUIContent("Storage Mode", property.tooltip), true);
                    else
                        EditorGUILayout.PropertyField(property, true);
                }

                if (settings == null)
                    continue;

                if (property.propertyPath == "localLevelStorage" &&
                    property.enumValueIndex == (int)LocalLevelStorage.Addressables)
                {
                    DrawLocalGroup(manager, settings);
                }
                else if (property.propertyPath == "addressableLabel")
                {
                    DrawRemoteGroup(manager, settings);
                }
            }

            serializedObject.ApplyModifiedProperties();
        }

        // Editable, paged list of the main Addressable level references (Addressables storage). Drawn from a short-lived
        // SerializedObject so it can sit below the Max Levels field, outside the default property loop.
        private static void DrawLocalLevelReferences(LevelDataManagerSettingsBase manager, string titlePrefix = "Levels")
        {
            using (var serialized = new SerializedObject(manager))
            {
                serialized.UpdateIfRequiredOrScript();
                SerializedProperty references = serialized.FindProperty("localAddressableLevels");
                if (references != null)
                {
                    DrawPagedReferenceList(
                        references,
                        manager,
                        $"{titlePrefix} ({references.arraySize})",
                        "main",
                        true);
                }

                serialized.ApplyModifiedProperties();
            }
        }

        // ---- A/B level options: one row per option with its folders and generated level data ----

        private static void DrawOptionsSection(SerializedObject serializedObject)
        {
            SerializedProperty options = serializedObject.FindProperty("levelOptions");
            SerializedProperty localOptions = serializedObject.FindProperty("localOptionLevels");
            bool addressableStorage = serializedObject.FindProperty("localLevelStorage").enumValueIndex ==
                                      (int)LocalLevelStorage.Addressables;
            UnityEngine.Object owner = serializedObject.targetObject;

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"A/B Level Options ({options.arraySize})", EditorStyles.boldLabel);
                if (GUILayout.Button("+ Add option", GUILayout.Width(100f)))
                {
                    int index = options.arraySize;
                    options.InsertArrayElementAtIndex(index);
                    SerializedProperty added = options.GetArrayElementAtIndex(index);
                    added.FindPropertyRelative(nameof(LevelDataOption.label)).stringValue = $"option_{index + 1}";
                    added.FindPropertyRelative(nameof(LevelDataOption.localFolderPath)).stringValue = string.Empty;
                    added.FindPropertyRelative(nameof(LevelDataOption.addressableFolderPath)).stringValue = string.Empty;
                }
            }

            int removeIndex = -1;
            for (int i = 0; i < options.arraySize; i++)
            {
                if (DrawOptionRow(options.GetArrayElementAtIndex(i), localOptions, addressableStorage, owner, i))
                    removeIndex = i;
            }

            if (removeIndex >= 0)
                options.DeleteArrayElementAtIndex(removeIndex);

            DrawOrphanOptionData(options, localOptions);
        }

        // Returns true when the user asked to remove this option.
        private static bool DrawOptionRow(
            SerializedProperty option,
            SerializedProperty localOptions,
            bool addressableStorage,
            UnityEngine.Object owner,
            int index)
        {
            SerializedProperty labelProperty = option.FindPropertyRelative(nameof(LevelDataOption.label));
            SerializedProperty localData = FindLocalOptionData(localOptions, labelProperty.stringValue);
            SerializedProperty dataList = localData?.FindPropertyRelative(addressableStorage ? "references" : "levels");
            int count = dataList != null ? dataList.arraySize : 0;

            string key = "Eternals.Option." + owner.GetInstanceID() + "." + index;
            bool remove = false;
            bool expanded = SessionState.GetBool(key, false);

            using (new EditorGUILayout.HorizontalScope())
            {
                string title = string.IsNullOrWhiteSpace(labelProperty.stringValue) ? $"Option {index + 1}" : labelProperty.stringValue;
                expanded = EditorGUILayout.Foldout(expanded, $"{title}   ({count} levels)", true);
                if (GUILayout.Button("Remove", GUILayout.Width(70f)))
                    remove = true;
            }

            SessionState.SetBool(key, expanded);
            if (!expanded)
                return remove;

            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(labelProperty, new GUIContent("Label"));
            DrawFolderProperty(option.FindPropertyRelative(nameof(LevelDataOption.localFolderPath)), "Local Folder Path");
            DrawFolderProperty(option.FindPropertyRelative(nameof(LevelDataOption.addressableFolderPath)), "Addressable Folder Path");

            if (dataList == null)
            {
                EditorGUILayout.HelpBox(
                    "No generated levels for this option yet. Click 'Update Local Level List'.",
                    MessageType.None);
            }
            else
            {
                DrawPagedReferenceList(
                    dataList,
                    owner,
                    addressableStorage
                        ? $"Generated levels (Addressable references: {count})"
                        : $"Generated levels (asset references: {count})",
                    "option." + index);
            }

            EditorGUI.indentLevel--;
            return remove;
        }

        private static void DrawFolderProperty(SerializedProperty folder, string label)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PropertyField(folder, new GUIContent(label));
                if (GUILayout.Button("Select...", GUILayout.Width(70f)) &&
                    LevelFolderPicker.TryPick(label, folder.stringValue, out string picked))
                {
                    folder.stringValue = picked;
                    GUI.FocusControl(null);
                }
            }
        }

        private static SerializedProperty FindLocalOptionData(SerializedProperty localOptions, string label)
        {
            if (localOptions == null || string.IsNullOrWhiteSpace(label))
                return null;

            for (int i = 0; i < localOptions.arraySize; i++)
            {
                SerializedProperty entry = localOptions.GetArrayElementAtIndex(i);
                if (string.Equals(entry.FindPropertyRelative("label").stringValue, label.Trim(), StringComparison.OrdinalIgnoreCase))
                    return entry;
            }

            return null;
        }

        // Generated data whose label no longer matches any option (e.g. an option was removed or renamed).
        private static void DrawOrphanOptionData(SerializedProperty options, SerializedProperty localOptions)
        {
            if (localOptions == null)
                return;

            var orphans = new List<int>();
            for (int i = 0; i < localOptions.arraySize; i++)
            {
                string label = localOptions.GetArrayElementAtIndex(i).FindPropertyRelative("label").stringValue;
                bool found = false;
                for (int o = 0; o < options.arraySize && !found; o++)
                {
                    found = string.Equals(
                        options.GetArrayElementAtIndex(o).FindPropertyRelative(nameof(LevelDataOption.label)).stringValue?.Trim(),
                        label,
                        StringComparison.OrdinalIgnoreCase);
                }

                if (!found)
                    orphans.Add(i);
            }

            if (orphans.Count == 0)
                return;

            EditorGUILayout.HelpBox(
                $"{orphans.Count} generated level set(s) no longer match any option label and are not used.",
                MessageType.Warning);
            if (GUILayout.Button("Clear unused level sets"))
            {
                for (int i = orphans.Count - 1; i >= 0; i--)
                    localOptions.DeleteArrayElementAtIndex(orphans[i]);
            }
        }

        // Priority option as a drop-down of "main" and the configured option labels instead of free text.
        private static void DrawPriorityOption(SerializedProperty priority, SerializedProperty options)
        {
            var names = new List<string> { LevelDataManagerSettingsBase.MainLevelOption };
            for (int i = 0; i < options.arraySize; i++)
            {
                string label = options.GetArrayElementAtIndex(i).FindPropertyRelative(nameof(LevelDataOption.label)).stringValue?.Trim();
                if (!string.IsNullOrEmpty(label) &&
                    !names.Exists(n => string.Equals(n, label, StringComparison.OrdinalIgnoreCase)))
                {
                    names.Add(label);
                }
            }

            string current = string.IsNullOrWhiteSpace(priority.stringValue)
                ? LevelDataManagerSettingsBase.MainLevelOption
                : priority.stringValue.Trim();
            int selected = names.FindIndex(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase));
            var shown = new List<string>(names);
            if (selected < 0)
            {
                shown.Add($"{current} (no such option)");
                selected = shown.Count - 1;
            }

            int next = EditorGUILayout.Popup(new GUIContent("Priority Option"), selected, shown.ToArray());
            if (next != selected && next < names.Count)
                priority.stringValue = names[next];
        }

        // Editable list of Addressable level references, shown a page at a time so thousands of levels stay responsive.
        // The state (foldout, page) is kept per asset for the Editor session.
        private static void DrawPagedReferenceList(SerializedProperty list, UnityEngine.Object owner, string title, string stateName, bool openByDefault = false)
        {
            const int PageSize = 50;
            string key = "Eternals.LevelList." + owner.GetInstanceID() + "." + stateName;

            bool expanded = SessionState.GetBool(key + ".open", openByDefault);
            expanded = EditorGUILayout.Foldout(expanded, title, true);
            SessionState.SetBool(key + ".open", expanded);
            if (!expanded)
                return;

            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox(
                "Normally generated by 'Update Local Level List'. You can edit elements by hand, but entries must stay in level order.",
                MessageType.None);

            int size = EditorGUILayout.DelayedIntField("Size", list.arraySize);
            if (size >= 0 && size != list.arraySize)
                list.arraySize = size;

            int pageCount = Mathf.Max(1, Mathf.CeilToInt(list.arraySize / (float)PageSize));
            int page = Mathf.Clamp(SessionState.GetInt(key + ".page", 0), 0, pageCount - 1);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(page <= 0))
                {
                    if (GUILayout.Button("< Prev", GUILayout.Width(70f)))
                        page--;
                }

                GUILayout.Label($"Page {page + 1} / {pageCount}", GUILayout.Width(90f));

                using (new EditorGUI.DisabledScope(page >= pageCount - 1))
                {
                    if (GUILayout.Button("Next >", GUILayout.Width(70f)))
                        page++;
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label("Go to element", GUILayout.Width(90f));
                int firstIndex = page * PageSize;
                int jump = EditorGUILayout.DelayedIntField(firstIndex, GUILayout.Width(60f));
                if (jump != firstIndex)
                    page = Mathf.Clamp(jump / PageSize, 0, pageCount - 1);
            }

            SessionState.SetInt(key + ".page", page);

            int start = page * PageSize;
            int end = Mathf.Min(list.arraySize, start + PageSize);
            for (int i = start; i < end; i++)
                EditorGUILayout.PropertyField(list.GetArrayElementAtIndex(i), new GUIContent($"Element {i}"));

            EditorGUI.indentLevel--;
        }

        public static void DrawLevelManagerControls(LevelDataManagerSettingsBase manager)
        {
            DrawLevelManagerControls(manager, false);
        }

        public static void DrawLevelManagerControls(LevelDataManagerSettingsBase manager, bool groupFieldsDrawnInline)
        {
            if (manager == null)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Level Catalog (Eternals Level Management)", EditorStyles.boldLabel);
            DrawCommonSettings(manager);

            if (!groupFieldsDrawnInline && manager.UsesAddressableLocalLevels && manager.DirectReferenceCount == 0)
                DrawLocalLevelReferences(manager, "Level Catalog");

            if (manager.UsesAddressableLocalLevels && manager.DirectReferenceCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"Local Level Storage is Addressables, but {manager.DirectReferenceCount} level(s) are still hard references in the Levels list " +
                    "and are loaded with this asset. Click 'Update Local Level List' to register them in the local group and clear the references.",
                    MessageType.Warning);
            }

            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                EditorGUILayout.HelpBox(
                    "Addressables Settings are not configured. Local level catalog tools are still available.",
                    MessageType.Warning);
                DrawUpdateLocalLevels(manager, null, null);
                return;
            }

            AddressableAssetGroup targetGroup = DrawRemoteGroup(manager, settings, !groupFieldsDrawnInline);
            AddressableAssetGroup localGroup = manager.UsesAddressableLocalLevels
                ? DrawLocalGroup(manager, settings, !groupFieldsDrawnInline)
                : null;
            List<UnityEngine.Object> localLevels = GetLocalLevels(manager);
            List<string> remoteGuids = CollectRemoteLevelGuids(manager, out int unresolvedRemoteReferenceCount);
            AddressableLevelRegistrationUtility.GetCounts(settings, remoteGuids, manager.AddressableLabel,
                out int registeredCount, out int labelledCount);

            EditorGUILayout.HelpBox(
                $"Local levels: {manager.LocalLevelCount}. Remote copies: {remoteGuids.Count}. " +
                $"{registeredCount}/{remoteGuids.Count} registered, {labelledCount}/{remoteGuids.Count} labelled '{manager.AddressableLabel}'.\n" +
                $"Configured A/B options: {manager.LevelOptions.Count}.\n" +
                $"Local folder: {manager.LevelsFolder}\nRemote copy folder: {manager.AddressableLevelsFolder}\n" +
                "Local levels remain in the player build as the offline fallback.",
                MessageType.Info);

            if (unresolvedRemoteReferenceCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"Skipped {unresolvedRemoteReferenceCount} remote reference(s) whose GUIDs do not resolve to assets. " +
                    "Run 'Create / Sync Remote Addressable Copies' to rebuild the remote copy list from the current local levels.",
                    MessageType.Warning);
            }

            if (manager.UseAddressables && manager.LevelOptions.Count > 0 && !manager.DiscoverAddressablesAtRuntime)
            {
                EditorGUILayout.HelpBox(
                    "A/B option Addressables are discovered by label at runtime. Enable 'Discover Addressables At Runtime' " +
                    "to use remote option level sets; local option fallback remains available either way.",
                    MessageType.Warning);
            }

            DrawUpdateLocalLevels(manager, settings, localGroup);
            using (new EditorGUI.DisabledScope(
                       EditorApplication.isPlayingOrWillChangePlaymode ||
                       (localLevels.Count == 0 && manager.LevelOptions.Count == 0)))
            {
                if (GUILayout.Button("Create / Sync Remote Addressable Copies", GUILayout.Height(30f)))
                    AddressableLevelFastSync.Run(manager, settings, targetGroup);
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
            string localFolder = DrawFolderField("Local Level Folder", manager.LevelsFolder);
            if (!string.Equals(localFolder, manager.LevelsFolder, StringComparison.Ordinal))
                RecordAndDirty(manager, "Change Local Level Folder", () => manager.SetLevelFolderPath(localFolder));

            string remoteFolder = DrawFolderField("Addressable Copy Folder", manager.AddressableLevelsFolder);
            if (!string.Equals(remoteFolder, manager.AddressableLevelsFolder, StringComparison.Ordinal))
                RecordAndDirty(manager, "Change Addressable Copy Folder", () => manager.SetAddressableLevelFolderPath(remoteFolder));

            int maxLevel = EditorGUILayout.IntField("Max Levels (-1 = all)", manager.MaxLevel);
            if (maxLevel < -1)
                maxLevel = -1;
            if (maxLevel != manager.MaxLevel)
                RecordAndDirty(manager, "Change Maximum Level Count", () => manager.SetMaxLevel(maxLevel));
        }

        // Text field for a project folder plus a button that opens the folder picker. Returns the (possibly changed) path.
        private static string DrawFolderField(string label, string value)
        {
            string result;
            using (new EditorGUILayout.HorizontalScope())
            {
                result = EditorGUILayout.TextField(label, value);
                if (GUILayout.Button("Select...", GUILayout.Width(70f)) &&
                    LevelFolderPicker.TryPick(label, value, out string picked))
                {
                    result = picked;
                    GUI.FocusControl(null); // so the text field shows the new value right away
                }
            }

            return result;
        }

        private static AddressableAssetGroup DrawLocalGroup(
            LevelDataManagerSettingsBase manager,
            AddressableAssetSettings settings,
            bool draw = true)
        {
            var groups = new List<AddressableAssetGroup> { null };
            var labels = new List<string> { "<Select local group>" };
            int selectedIndex = 0;

            if (settings.groups != null)
            {
                for (int i = 0; i < settings.groups.Count; i++)
                {
                    AddressableAssetGroup group = settings.groups[i];
                    if (group == null || !IsLocalBundledGroup(group))
                        continue;

                    groups.Add(group);
                    labels.Add(group.Name);
                    if (string.Equals(group.Guid, manager.LocalAddressableGroupGuid, StringComparison.Ordinal))
                        selectedIndex = groups.Count - 1;
                }
            }

            if (draw)
            {
                DrawSearchableGroupField(
                    "Local Addressables Group",
                    groups[selectedIndex],
                    groups,
                    selectedGroup => RecordAndDirty(
                        manager,
                        "Change Local Addressables Group",
                        () => manager.SetLocalAddressableGroupGuid(selectedGroup != null ? selectedGroup.Guid : string.Empty)));
            }

            return groups[selectedIndex];
        }

        // Drop-down with a search box (AdvancedDropdown) for choosing an Addressables group; the first entry (null) clears the selection.
        private static void DrawSearchableGroupField(
            string label,
            AddressableAssetGroup current,
            IReadOnlyList<AddressableAssetGroup> groups,
            Action<AddressableAssetGroup> onSelected)
        {
            Rect rect = EditorGUILayout.GetControlRect();
            rect = EditorGUI.PrefixLabel(rect, new GUIContent(label));
            string currentName = current != null ? current.Name : "<Select group>";
            if (!EditorGUI.DropdownButton(rect, new GUIContent(currentName), FocusType.Keyboard))
                return;

            var dropdown = new GroupSearchDropdown(new UnityEditor.IMGUI.Controls.AdvancedDropdownState(), groups, onSelected);
            dropdown.Show(rect);
        }

        private sealed class GroupSearchDropdown : UnityEditor.IMGUI.Controls.AdvancedDropdown
        {
            private readonly IReadOnlyList<AddressableAssetGroup> groups;
            private readonly Action<AddressableAssetGroup> onSelected;

            public GroupSearchDropdown(
                UnityEditor.IMGUI.Controls.AdvancedDropdownState state,
                IReadOnlyList<AddressableAssetGroup> groups,
                Action<AddressableAssetGroup> onSelected)
                : base(state)
            {
                this.groups = groups;
                this.onSelected = onSelected;
                minimumSize = new Vector2(280f, 320f);
            }

            protected override UnityEditor.IMGUI.Controls.AdvancedDropdownItem BuildRoot()
            {
                var root = new UnityEditor.IMGUI.Controls.AdvancedDropdownItem("Addressables Groups");
                for (int i = 0; i < groups.Count; i++)
                {
                    string name = groups[i] != null ? groups[i].Name : "<None>";
                    root.AddChild(new UnityEditor.IMGUI.Controls.AdvancedDropdownItem(name) { id = i });
                }

                return root;
            }

            protected override void ItemSelected(UnityEditor.IMGUI.Controls.AdvancedDropdownItem item)
            {
                if (item.id >= 0 && item.id < groups.Count)
                    onSelected?.Invoke(groups[item.id]);
            }
        }

        // A bundled group whose build and load paths are local, so its levels ship inside the player build.
        private static bool IsLocalBundledGroup(AddressableAssetGroup group)
        {
            var schema = group.GetSchema<BundledAssetGroupSchema>();
            return schema != null && !group.ReadOnly && !schema.BuildPath.GetName(group.Settings).Contains("Remote", StringComparison.OrdinalIgnoreCase);
        }

        // Registers the local level assets in the local group in a single batch; each address is "{prefix}/{index}_{name}".
        private static bool RegisterLocalLevels(
            AddressableAssetSettings settings,
            AddressableAssetGroup localGroup,
            IReadOnlyList<UnityEngine.Object> levels,
            string addressPrefix,
            out string error)
        {
            error = null;
            if (localGroup == null || localGroup.Settings != settings || localGroup.ReadOnly)
            {
                error = "Select an editable local Addressables group for local levels.";
                return false;
            }

            var changed = new HashSet<AddressableAssetGroup> { localGroup };
            for (int i = 0; i < levels.Count; i++)
            {
                string path = levels[i] != null ? AssetDatabase.GetAssetPath(levels[i]) : null;
                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    error = $"Level {i} of '{addressPrefix}' must be an asset under Assets.";
                    return false;
                }

                string guid = AssetDatabase.AssetPathToGUID(path);
                AddressableAssetEntry existing = settings.FindAssetEntry(guid, true);
                if (existing?.parentGroup != null)
                    changed.Add(existing.parentGroup);

                AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, localGroup, false, false);
                if (entry == null)
                {
                    error = $"Could not register '{path}' as Addressable.";
                    return false;
                }

                entry.SetAddress($"{addressPrefix}/{i:D6}_{Path.GetFileNameWithoutExtension(path)}", false);
            }

            foreach (AddressableAssetGroup group in changed)
                EditorUtility.SetDirty(group);
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, changed, true, true);
            EditorUtility.SetDirty(settings);
            return true;
        }

        private static AddressableAssetGroup DrawRemoteGroup(
            LevelDataManagerSettingsBase manager,
            AddressableAssetSettings settings,
            bool draw = true)
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

            if (draw)
            {
                DrawSearchableGroupField(
                    "Remote Addressables Group",
                    groups[selectedIndex],
                    groups,
                    selectedGroup => RecordAndDirty(
                        manager,
                        "Change Remote Addressables Group",
                        () => manager.SetRemoteAddressableGroupGuid(selectedGroup != null ? selectedGroup.Guid : string.Empty)));
            }

            return groups[selectedIndex];
        }

        private static void DrawUpdateLocalLevels(
            LevelDataManagerSettingsBase manager,
            AddressableAssetSettings settings,
            AddressableAssetGroup localGroup)
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

                if (manager.UsesAddressableLocalLevels)
                {
                    // Local levels become entries of the local group and the manager keeps only references to them.
                    if (settings == null)
                    {
                        EditorUtility.DisplayDialog("Local Addressables group", "Addressables Settings are not configured.", "OK");
                        return;
                    }

                    if (!RegisterLocalLevels(settings, localGroup, foundLevels, "LevelLocal", out string registerError))
                    {
                        EditorUtility.DisplayDialog("Could not register local levels", registerError, "OK");
                        return;
                    }

                    for (int i = 0; i < manager.LevelOptions.Count; i++)
                    {
                        string prefix = "LevelLocal_" + manager.LevelOptions[i].label.Trim();
                        if (!RegisterLocalLevels(settings, localGroup, optionLevels[i], prefix, out registerError))
                        {
                            EditorUtility.DisplayDialog("Could not register local option levels", registerError, "OK");
                            return;
                        }
                    }

                    manager.SetLocalAddressableGroupGuid(localGroup.Guid);
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
            paths.Sort(NaturalLevelNameComparer.Instance);

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

        private static List<string> CollectRemoteLevelGuids(
            LevelDataManagerSettingsBase manager,
            out int unresolvedReferenceCount)
        {
            unresolvedReferenceCount = 0;
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
                if (string.IsNullOrWhiteSpace(path))
                {
                    unresolvedReferenceCount++;
                    continue;
                }

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
            LevelDataManagerEditorUtility.DrawInspectorFields(serializedObject, (LevelDataManagerSettingsBase)target);

            if (target is ILevelDataManagerValidator validator)
            {
                EditorGUILayout.Space();
                if (GUILayout.Button("Validate All Levels", GUILayout.Height(28f)))
                    validator.ValidateLevelData();
            }

            LevelDataManagerEditorUtility.DrawLevelManagerControls(
                (LevelDataManagerSettingsBase)target, true);
        }
    }
}
