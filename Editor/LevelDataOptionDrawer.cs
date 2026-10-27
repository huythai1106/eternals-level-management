using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Eternals.LevelManagement.Editor
{
    /// <summary>Folder picker that returns a project-relative path ("Assets/...").</summary>
    internal static class LevelFolderPicker
    {
        public static bool TryPick(string title, string currentFolder, out string projectRelativeFolder)
        {
            projectRelativeFolder = null;
            string normalized = string.IsNullOrWhiteSpace(currentFolder) ? string.Empty : currentFolder.Replace('\\', '/').TrimEnd('/');
            string startFolder = AssetDatabase.IsValidFolder(normalized) ? Path.GetFullPath(normalized) : Application.dataPath;

            string picked = EditorUtility.OpenFolderPanel(title, startFolder, string.Empty);
            if (string.IsNullOrEmpty(picked))
                return false;

            string relative = FileUtil.GetProjectRelativePath(picked.Replace('\\', '/'));
            if (string.IsNullOrEmpty(relative) || !relative.StartsWith("Assets", StringComparison.Ordinal))
            {
                EditorUtility.DisplayDialog("Invalid folder", "Select a folder inside this project's Assets folder.", "OK");
                return false;
            }

            projectRelativeFolder = relative;
            return true;
        }
    }

    /// <summary>Draws a level option with a folder picker next to each folder path.</summary>
    [CustomPropertyDrawer(typeof(LevelDataOption))]
    public sealed class LevelDataOptionDrawer : PropertyDrawer
    {
        private const int ChildLineCount = 3;
        private const float PickButtonWidth = 70f;

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float line = EditorGUIUtility.singleLineHeight;
            float space = EditorGUIUtility.standardVerticalSpacing;
            return property.isExpanded ? line + ChildLineCount * (line + space) : line;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            float line = EditorGUIUtility.singleLineHeight;
            float step = line + EditorGUIUtility.standardVerticalSpacing;
            var rect = new Rect(position.x, position.y, position.width, line);

            SerializedProperty labelProperty = property.FindPropertyRelative(nameof(LevelDataOption.label));
            string title = string.IsNullOrWhiteSpace(labelProperty.stringValue) ? label.text : labelProperty.stringValue;
            property.isExpanded = EditorGUI.Foldout(rect, property.isExpanded, title, true);

            if (property.isExpanded)
            {
                EditorGUI.indentLevel++;
                rect.y += step;
                EditorGUI.PropertyField(rect, labelProperty);
                rect.y += step;
                DrawFolderRow(rect, property.FindPropertyRelative(nameof(LevelDataOption.localFolderPath)), "Local Folder Path");
                rect.y += step;
                DrawFolderRow(rect, property.FindPropertyRelative(nameof(LevelDataOption.addressableFolderPath)), "Addressable Folder Path");
                EditorGUI.indentLevel--;
            }

            EditorGUI.EndProperty();
        }

        private static void DrawFolderRow(Rect rect, SerializedProperty folderProperty, string label)
        {
            var textRect = new Rect(rect.x, rect.y, rect.width - PickButtonWidth - 2f, rect.height);
            var buttonRect = new Rect(rect.xMax - PickButtonWidth, rect.y, PickButtonWidth, rect.height);

            EditorGUI.PropertyField(textRect, folderProperty, new GUIContent(label));
            if (GUI.Button(buttonRect, "Select...") &&
                LevelFolderPicker.TryPick(label, folderProperty.stringValue, out string folder))
            {
                folderProperty.stringValue = folder;
                GUI.FocusControl(null);
            }
        }
    }
}
