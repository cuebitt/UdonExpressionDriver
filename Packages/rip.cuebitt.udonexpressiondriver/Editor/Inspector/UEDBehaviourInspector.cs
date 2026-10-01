using UdonExpressionDriver;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace UdonExpressionDriver.Editor
{
    /// <summary>Base inspector for UED behaviours: draws the description box and the raw serialized fields.</summary>
    [CustomEditor(typeof(UEDBehaviour), true)]
    public class UEDBehaviourInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target, false, false)) return;

            DrawDescription(
                "Base class shared by UED prop behaviours. It does nothing on its own. " +
                "Add a UEDArmatureLink for a wearable prop or a UEDFullController to drive an expressions menu.");

            base.OnInspectorGUI();
        }

        protected static void DrawDescription(string text)
        {
            EditorGUILayout.HelpBox(text, MessageType.Info);
        }

        private static GUIStyle _sectionTitleStyle;

        private static GUIStyle SectionTitleStyle
        {
            get
            {
                // Construct lazily instead of in a static field initializer: Unity forbids
                // allocating GUIStyle/RectOffset in the ScriptableObject-constructor path.
                if (_sectionTitleStyle == null)
                {
                    _sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel);
                    _sectionTitleStyle.margin = new RectOffset(0, 0, 2, 6);
                }
                return _sectionTitleStyle;
            }
        }

        protected static void BeginSection(string title)
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(title, SectionTitleStyle);
        }

        protected static void EndSection()
        {
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// Adds a PhysboneForwarder to every child with a VRCPhysBone and a ContactForwarder
        /// to every child with a contact sender/receiver, wiring them to the root UEDBehaviour.
        /// Forwarders are flagged autoLinked so UEDBuildAutoLinker can remove them after
        /// play/build. Idempotent: skips children that already have one.
        /// </summary>
        public static (int PhysboneCount, int ContactCount) LinkChildForwardersAndCount(GameObject go)
        {
            // the root is its own forwarder target, so skip it and walk children only
            var rootBehaviour = go.GetComponent<UEDBehaviour>();
            if (rootBehaviour == null) return (0, 0);

            var physboneCount = 0;
            var contactCount = 0;

            foreach (var child in go.GetComponentsInChildren<Transform>(true))
            {
                var childGo = child.gameObject;
                if (childGo == go) continue;

                // sender and receiver alike: either one produces events the root behaviour can't see
                if (childGo.GetComponent<VRCPhysBone>() != null && childGo.GetComponent<PhysboneForwarder>() == null)
                {
                    var forwarder = UdonSharpUndo.AddComponent<PhysboneForwarder>(childGo);
                    ConfigureForwarder(forwarder, rootBehaviour);
                    physboneCount++;
                }

                var hasContact = childGo.GetComponent<VRCContactReceiver>() != null ||
                                 childGo.GetComponent<VRCContactSender>() != null;
                if (hasContact && childGo.GetComponent<ContactForwarder>() == null)
                {
                    var forwarder = UdonSharpUndo.AddComponent<ContactForwarder>(childGo);
                    ConfigureForwarder(forwarder, rootBehaviour);
                    contactCount++;
                }
            }

            return (physboneCount, contactCount);
        }

        private static void ConfigureForwarder(UdonSharpBehaviour forwarder, UdonSharpBehaviour target)
        {
            var serialized = new SerializedObject(forwarder);
            var targetProperty = serialized.FindProperty("target");
            if (targetProperty != null) targetProperty.objectReferenceValue = target;
            var autoProperty = serialized.FindProperty("autoLinked");
            // hidden field on the forwarder; survives the play-mode domain reload that a static list wouldn't
            if (autoProperty != null) autoProperty.boolValue = true;
            serialized.ApplyModifiedProperties();
        }

        internal static bool GetMarker(UdonSharpBehaviour behaviour, string field)
        {
            var serialized = new SerializedObject(behaviour);
            var property = serialized.FindProperty(field);
            return property != null && property.boolValue;
        }

        internal static void SetMarker(UdonSharpBehaviour behaviour, string field, bool value)
        {
            var serialized = new SerializedObject(behaviour);
            var property = serialized.FindProperty(field);
            if (property == null) return;
            property.boolValue = value;
            serialized.ApplyModifiedProperties();
        }

        internal static bool IsAutoLinked(UdonSharpBehaviour behaviour)
        {
            return GetMarker(behaviour, "autoLinked");
        }

        internal static void MarkAutoLinked(UdonSharpBehaviour behaviour)
        {
            SetMarker(behaviour, "autoLinked", true);
        }
    }
}