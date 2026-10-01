using System.Collections.Generic;
using UdonExpressionDriver;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace UdonExpressionDriver.Editor
{
    /// Expressions section (the single way to populate data arrays) + settings + menu views &
    /// controls + status. VRCFury assets auto-import when present; nothing is locked.
    [CustomEditor(typeof(UEDFullController))]
    public class UEDFullControllerInspector : UEDBehaviourInspector
    {
        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target, false, false)) return;

            // The auto-import and animator rewrite touch the prop's components and assets, which can
            // throw when the prop is in a half-configured state (e.g. no Animator yet). Contain it so
            // nothing escapes into UdonSharp's inspector wrapper and floods the console every repaint.
            try
            {
                DrawCore();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[UED] Error drawing FullController inspector for '{target.name}': {e}", target);
            }
        }

        private void DrawCore()
        {
            // idempotent and count-checked internally, so a repaint only re-imports when the data actually differs
            var controller = (UEDFullController)target;
            var vrcFuryPresent = UEDVrcFuryBridge.AutoImportMenu(controller);

            // Swaps the prop's Animator onto a prop-relative copy of the controller (avatar-prop
            // clip paths don't resolve against the prop root's own Animator). Idempotent, so this
            // is cheap on every repaint and keeps the Animation window showing resolved bindings.
            try
            {
                UEDAnimatorRewriter.ApplyForProp(controller);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[UED] Failed to apply rewritten animator controller for '{controller.name}': {e}", controller);
            }

            DrawDescription(
                "Drives a prop's Animator from expression parameters and shows the prop's expressions " +
                "menu on a radial menu in the world. All configuration lives on this component. If the " +
                "prop carries a VRCFury Full Controller, its menu, parameters, and animator controller " +
                "are imported automatically.");

            DrawExpressionsSection(controller, vrcFuryPresent);
            DrawSettingsSection();
            DrawMenuViewsAndControlsSection();
            DrawStatus(controller);
        }

        private void DrawMenuViewsAndControlsSection()
        {
            BeginSection("Menu Views & Controls");

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("menuView"), new GUIContent("Radial Menu"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("radialPuppet"), new GUIContent("Radial Puppet"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("axisPuppet"), new GUIContent("Axis Puppet"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("handGestures"), new GUIContent("Hand Gesture Menu"));
            serializedObject.ApplyModifiedProperties();

            // these stay unset until play/build, so list what the auto-linker will add rather than faking it now
            var willCreate = new List<string>(4);
            if (serializedObject.FindProperty("menuView").objectReferenceValue == null) willCreate.Add("Radial Menu");
            if (serializedObject.FindProperty("radialPuppet").objectReferenceValue == null) willCreate.Add("Radial Puppet");
            if (serializedObject.FindProperty("axisPuppet").objectReferenceValue == null) willCreate.Add("Axis Puppet");
            if (serializedObject.FindProperty("enableHandGestureEmulation").boolValue
                && serializedObject.FindProperty("handGestures").objectReferenceValue == null) willCreate.Add("Hand Gesture Menu");

            if (willCreate.Count > 0)
            {
                var created = string.Join(", ", willCreate.ToArray());
                EditorGUILayout.HelpBox($"On play mode or build, the following will be created automatically: {created}.", MessageType.Info);
            }
            EndSection();
        }

        private void DrawSettingsSection()
        {
            BeginSection("Settings");

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("interactTogglesMenu"), new GUIContent("Interact Toggles Menu"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("enableHandGestureEmulation"), new GUIContent("Enable Hand Gesture Emulation"));
            serializedObject.ApplyModifiedProperties();

            if (serializedObject.FindProperty("enableHandGestureEmulation").boolValue)
            {
                EditorGUILayout.HelpBox("When Hand Gesture Emulation is on, a 'Hand Gestures' wedge appears at the top menu level only if the prop's Animator uses GestureLeft or GestureRight.", MessageType.Info);
            }
            EndSection();
        }

        private void DrawExpressionsSection(UEDFullController controller, bool vrcFuryPresent)
        {
            BeginSection("Expressions");

            // own SerializedObject, not the inherited one: these fields are hidden from the default draw
            var serialized = new SerializedObject(controller);
            EditorGUILayout.PropertyField(serialized.FindProperty("importedAnimatorController"), new GUIContent("Controller"));

            var menu = UEDVrcFuryBridge.GetStoredAsset<VRCExpressionsMenu>(serialized, "importedMenuGuid");
            var parameters = UEDVrcFuryBridge.GetStoredAsset<VRCExpressionParameters>(serialized, "importedParametersGuid");

            var newMenu = (VRCExpressionsMenu)EditorGUILayout.ObjectField("Menu", menu, typeof(VRCExpressionsMenu), false);
            var newParameters = (VRCExpressionParameters)EditorGUILayout.ObjectField("Parameter", parameters, typeof(VRCExpressionParameters), false);

            // stored by GUID so a VRCFury import and a manual assignment land in the same field
            if (newMenu != menu) UEDVrcFuryBridge.SetStoredAsset(serialized, "importedMenuGuid", newMenu);
            if (newParameters != parameters) UEDVrcFuryBridge.SetStoredAsset(serialized, "importedParametersGuid", newParameters);
            serialized.ApplyModifiedProperties();

            EditorGUILayout.HelpBox("Applied automatically in edit mode, play mode, and builds (as a generated, prop-relative copy).", MessageType.Info);

            if (vrcFuryPresent)
            {
                EditorGUILayout.Space(4);
                if (GUILayout.Button("Re-import from VRCFury", GUILayout.Height(28)))
                    UEDVrcFuryBridge.ReimportFromVrcFury(controller);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "No VRCFury component found on this prop. Configure the fields above manually, or " +
                    "install VRCFury and add a Full Controller to the prop for one-click import.",
                    MessageType.Info);
            }

            EndSection();
        }

        private static void DrawStatus(UEDFullController controller)
        {
            // read-only summary: the data arrays and Animator are populated elsewhere, not here
            var (paramCount, controlCount) = UEDVrcFuryBridge.CountData(controller);

            BeginSection("Status");
            EditorGUILayout.LabelField("Data", $"{paramCount} parameter(s), {controlCount} control(s)", EditorStyles.boldLabel);

            var serialized = new SerializedObject(controller);
            var animatorProperty = serialized.FindProperty("animator");
            if (animatorProperty != null)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.PropertyField(animatorProperty, new GUIContent("Animator"), true);
                EditorGUI.EndDisabledGroup();
            }

            EndSection();
        }
    }
}
