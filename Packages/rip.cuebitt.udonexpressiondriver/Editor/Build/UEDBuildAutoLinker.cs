using System.Collections.Generic;
using UdonExpressionDriver;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.SDK3.Components;
using VRC.SDKBase.Editor.BuildPipeline;

namespace UdonExpressionDriver.Editor
{
    /// <summary>
    /// Adds forwarders/menu view/puppets/Animator to UED props for play or build, then
    /// removes everything marked autoLinked so the authored scene is never modified.
    /// </summary>
    [InitializeOnLoad]
    public class UEDBuildAutoLinker : IVRCSDKBuildRequestedCallback
    {
        static UEDBuildAutoLinker()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                try
                {
                    AutoLinkForwarders();
                    EnsurePropComponents();
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[UED] Auto-link forwarders failed: {e}");
                }
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                // When EnteredEditMode fires the scene may not be restored to its pre-play state
                // yet, so reverting here either finds nothing or gets overwritten by the restore
                // that follows (leaving the auto-added Animator/puppets/menu behind in the scene).
                // Defer to the next editor update so the cleanup runs against the restored edit
                // scene; each pass is isolated so one failure can't skip the others.
                EditorApplication.delayCall += RevertAutoLinkedAfterPlay;
            }
        }

        // Runs after leaving play mode once the edit-mode scene has been restored. Skips when the
        // editor is already heading back into play, since the next ExitingEditMode pass re-adds and
        // cleans up its own leftovers.
        private static void RevertAutoLinkedAfterPlay()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            RevertForwarders();
            RevertControllerAdditions();
        }

        private static void RevertControllerAdditions()
        {
            RevertAutoLinked();
            RevertAutoAddedAnimators();
            RevertAutoAddedGestureParams();
        }

        public int callbackOrder => 100;

        public bool OnBuildRequested(VRCSDKRequestedBuildType requestedBuildType)
        {
            // Release builds only (edit mode). ClientSim fires a build request during the play
            // transition; forwarders there are handled by the play hooks (isPlayingOrWillChangePlaymode).
            if (requestedBuildType == VRCSDKRequestedBuildType.Scene && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                try
                {
                    AutoLinkForwarders();
                    EnsurePropComponents();
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[UED] Auto-link forwarders failed; continuing build: {e}");
                }
            }

            return true;
        }

        // scans every loaded scene so props in additively loaded scenes are linked too
        private static IEnumerable<T> FindInScene<T>() where T : Component
        {
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var component in root.GetComponentsInChildren<T>(true))
                        yield return component;
            }
        }

        private static void EnsurePropComponents()
        {
            RevertControllerAdditions();

            // one GameObject can carry several UED behaviours, so dedupe per object not per component
            var processedLinks = new HashSet<GameObject>();
            var processedControllers = new HashSet<GameObject>();
            var addedCount = 0;

            foreach (var link in FindInScene<UEDArmatureLink>())
            {
                if (!processedLinks.Add(link.gameObject)) continue;

                var go = link.gameObject;
                var changed = false;

                // kinematic and gravity-free so the worn prop never falls or fights the wearer
                if (go.GetComponent<Rigidbody>() == null)
                {
                    var rigidbody = go.AddComponent<Rigidbody>();
                    rigidbody.isKinematic = true;
                    rigidbody.useGravity = false;
                    changed = true;
                }

                // object sync is what makes the worn transform follow the wearer for everyone else
                if (go.GetComponent<VRCObjectSync>() == null)
                {
                    go.AddComponent<VRCObjectSync>();
                    changed = true;
                }

                if (changed)
                {
                    EditorUtility.SetDirty(go);
                    addedCount++;
                }
            }

            foreach (var controller in FindInScene<UEDFullController>())
            {
                if (!processedControllers.Add(controller.gameObject)) continue;

                var go = controller.gameObject;
                var changed = false;

                if (controller.transform.root.GetComponentInChildren<Animator>(true) == null)
                {
                    controller.transform.root.gameObject.AddComponent<Animator>();
                    changed = true;
                    // hidden marker: revert only ever touches Animators we added, never the user's
                    UEDBehaviourInspector.SetMarker(controller, "autoAddedAnimator", true);
                }

                // all transient, marked autoLinked so the post-play pass can remove them again
                if (EnsureMenuView(controller)) changed = true;
                if (EnsurePuppets(controller)) changed = true;
                if (IsHandGestureEmulationEnabled(controller) && EnsureHandGestures(controller)) changed = true;

                // Idempotently wires VRCFury controller/menu/param data, then applies whatever
                // assets are stored on the controller (VRCFury's or the Expressions section's).
                UEDVrcFuryBridge.AutoImportMenu(controller);
                UEDVrcFuryBridge.ApplyExpressions(controller);
                UEDVrcFuryBridge.EnsureHandGestureParams(controller);

                // Swaps in a prop-relative copy of the controller (avatar-prop clip paths don't
                // resolve against the prop root's own Animator) as a transient generated asset.
                try
                {
                    UEDAnimatorRewriter.ApplyForProp(controller);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[UED] Failed to rewrite animator controller for '{controller.name}': {e}", controller);
                }

                if (changed)
                {
                    EditorUtility.SetDirty(go);
                    addedCount++;
                }
            }

            if (addedCount > 0)
                Debug.Log($"[UED] Added missing Rigidbody/VRC Object Sync/Animator to {addedCount} prop(s).");
        }

        private static bool EnsureMenuView(UEDFullController controller)
        {
            // private UdonSharp fields, so the references have to go through SerializedProperty
            var controllerSerialized = new SerializedObject(controller);
            var menuView = controllerSerialized.FindProperty("menuView");
            if (menuView == null || menuView.objectReferenceValue != null) return false;

            const string prefabPath = "Packages/rip.cuebitt.udonexpressiondriver/Runtime/ExpressionMenu/Radial Menu.prefab";
            var instance = InstantiateAutoLinked(prefabPath, "Expression Menu", controller.transform);
            if (instance == null) return false;

            var radialMenu = instance.GetComponent<RadialMenu>();
            // prefab without the component would otherwise leave a stray empty child in the scene
            if (radialMenu == null)
            {
                Object.DestroyImmediate(instance);
                return false;
            }

            menuView.objectReferenceValue = radialMenu;
            controllerSerialized.ApplyModifiedProperties();

            var radialSerialized = new SerializedObject(radialMenu);
            // the menu drives the controller, so it needs the back-reference too
            radialSerialized.FindProperty("fullController").objectReferenceValue = controller;
            radialSerialized.ApplyModifiedProperties();

            UEDBehaviourInspector.MarkAutoLinked(radialMenu);

            return true;
        }

        private static bool EnsurePuppets(UEDFullController controller)
        {
            var controllerSerialized = new SerializedObject(controller);
            var radialPuppetProp = controllerSerialized.FindProperty("radialPuppet");
            var axisPuppetProp = controllerSerialized.FindProperty("axisPuppet");
            if (radialPuppetProp == null || axisPuppetProp == null) return false;

            const string radialPrefabPath = "Packages/rip.cuebitt.udonexpressiondriver/Runtime/ExpressionMenu/Menu Controls/Radial Puppet/Radial Puppet.prefab";
            const string axisPrefabPath = "Packages/rip.cuebitt.udonexpressiondriver/Runtime/ExpressionMenu/Menu Controls/Axis Puppet/Axis Puppet.prefab";

            var radial = EnsurePuppet<RadialPuppet>(radialPuppetProp, radialPrefabPath, "Radial Puppet", controller);
            var axis = EnsurePuppet<AxisPuppet>(axisPuppetProp, axisPrefabPath, "Axis Puppet", controller);

            var changed = false;
            if (radial != null && radialPuppetProp.objectReferenceValue != radial)
            {
                radialPuppetProp.objectReferenceValue = radial;
                changed = true;
            }
            if (axis != null && axisPuppetProp.objectReferenceValue != axis)
            {
                axisPuppetProp.objectReferenceValue = axis;
                changed = true;
            }

            // wiring the handler counts as a change, so the controller still gets dirtied
            if (radial != null)
                if (LinkPuppetHandler(radial, controller)) changed = true;

            if (axis != null)
                if (LinkPuppetHandler(axis, controller)) changed = true;

            if (changed) controllerSerialized.ApplyModifiedProperties();
            return changed;
        }

        private static T EnsurePuppet<T>(SerializedProperty prop, string prefabPath, string objectName, UEDFullController controller) where T : UdonSharpBehaviour
        {
            var existing = prop.objectReferenceValue as T;
            if (existing != null) return existing;

            var instance = InstantiateAutoLinked(prefabPath, objectName, controller.transform);
            if (instance == null) return null;

            var component = instance.GetComponent<T>();
            // the marker is how the post-play revert knows this object is ours to destroy
            if (component != null) UEDBehaviourInspector.MarkAutoLinked(component);
            return component;
        }

        private static bool IsHandGestureEmulationEnabled(UEDFullController controller)
        {
            var serialized = new SerializedObject(controller);
            var prop = serialized.FindProperty("enableHandGestureEmulation");
            return prop != null && prop.boolValue;
        }

        private static bool EnsureHandGestures(UEDFullController controller)
        {
            var controllerSerialized = new SerializedObject(controller);
            var handGesturesProp = controllerSerialized.FindProperty("handGestures");
            if (handGesturesProp == null) return false;

            const string prefabPath = "Packages/rip.cuebitt.udonexpressiondriver/Runtime/ExpressionMenu/Menu Controls/Gesture Menu/Gesture Menu.prefab";

            var gesture = EnsurePuppet<HandGestureMenu>(handGesturesProp, prefabPath, "Hand Gestures", controller);

            var changed = false;
            if (gesture != null && handGesturesProp.objectReferenceValue != gesture)
            {
                handGesturesProp.objectReferenceValue = gesture;
                changed = true;
            }

            if (gesture != null)
                if (LinkPuppetHandler(gesture, controller)) changed = true;

            if (changed) controllerSerialized.ApplyModifiedProperties();
            return changed;
        }

        private static GameObject InstantiateAutoLinked(string prefabPath, string objectName, Transform parent)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                Debug.LogWarning($"[UED] Could not load prefab at '{prefabPath}'.");
                return null;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null) return null;

            instance.name = objectName;
            instance.transform.SetParent(parent, false);
            // inactive by default: the panel only shows once the player actually opens it
            instance.SetActive(false);
            return instance;
        }

        private static bool LinkPuppetHandler(UdonSharpBehaviour puppet, UEDPuppetHandler controller)
        {
            var serialized = new SerializedObject(puppet);
            var handlerProperty = serialized.FindProperty("handler");
            if (handlerProperty == null) return false;
            if (handlerProperty.objectReferenceValue == controller) return false;
            handlerProperty.objectReferenceValue = controller;
            serialized.ApplyModifiedProperties();
            return true;
        }

        private static void AutoLinkForwarders()
        {
            RevertForwarders();

            var processed = new HashSet<GameObject>();
            var totalPhysbone = 0;
            var totalContact = 0;

            foreach (var behaviour in FindInScene<UEDBehaviour>())
            {
                if (!processed.Add(behaviour.gameObject)) continue;

                var (physboneCount, contactCount) =
                    UEDBehaviourInspector.LinkChildForwardersAndCount(behaviour.gameObject);
                totalPhysbone += physboneCount;
                totalContact += contactCount;
            }

            if (totalPhysbone + totalContact > 0)
                Debug.Log($"[UED] Linked {totalPhysbone} Physbone + {totalContact} Contact forwarder(s).");
        }

        private static void RevertForwarders()
        {
            // only marked ones, so a forwarder the user added by hand is never touched
            foreach (var forwarder in FindInScene<PhysboneForwarder>())
                if (UEDBehaviourInspector.IsAutoLinked(forwarder)) DestroyForwarder(forwarder);

            foreach (var forwarder in FindInScene<ContactForwarder>())
                if (UEDBehaviourInspector.IsAutoLinked(forwarder)) DestroyForwarder(forwarder);
        }

        private static void DestroyForwarder(UdonSharpBehaviour forwarder)
        {
            // the backing UdonBehaviour goes too, or its program asset bakes into the build
            var backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(forwarder);
            if (backing != null) Object.DestroyImmediate(backing);
            Object.DestroyImmediate(forwarder);
        }

        private static void RevertAutoAddedAnimators()
        {
            var removed = 0;

            foreach (var controller in FindInScene<UEDFullController>())
            {
                var serialized = new SerializedObject(controller);
                var autoAdded = serialized.FindProperty("autoAddedAnimator");
                if (autoAdded == null || !autoAdded.boolValue) continue;

                var animator = serialized.FindProperty("animator")?.objectReferenceValue as Animator;
                // The marker is only set when the prop had no Animator anywhere, so any Animator the
                // controller's hierarchy holds now is the one UED added to the prop root.
                if (animator == null)
                    animator = controller.transform.root.GetComponentInChildren<Animator>(true);

                // Destroy the Animator before clearing the field: ApplyModifiedProperties below fires
                // OnValidate, which would otherwise re-grab the still-present Animator and leave the
                // field pointing at the just-destroyed component (inspector shows "Missing (Animator)").
                if (animator != null)
                {
                    Object.DestroyImmediate(animator);
                    removed++;
                }

                var animatorProperty = serialized.FindProperty("animator");
                if (animatorProperty != null) animatorProperty.objectReferenceValue = null;
                autoAdded.boolValue = false;
                serialized.ApplyModifiedProperties();
            }

            if (removed > 0)
                Debug.Log($"[UED] Removed {removed} auto-added Animator(s).");
        }

        private static void RevertAutoLinked()
        {
            var marked = new HashSet<GameObject>();

            foreach (var menu in FindInScene<RadialMenu>())
                if (UEDBehaviourInspector.IsAutoLinked(menu)) marked.Add(menu.gameObject);

            foreach (var puppet in FindInScene<RadialPuppet>())
                if (UEDBehaviourInspector.IsAutoLinked(puppet)) marked.Add(puppet.gameObject);

            foreach (var puppet in FindInScene<AxisPuppet>())
                if (UEDBehaviourInspector.IsAutoLinked(puppet)) marked.Add(puppet.gameObject);

            foreach (var menu in FindInScene<HandGestureMenu>())
                if (UEDBehaviourInspector.IsAutoLinked(menu)) marked.Add(menu.gameObject);

            if (marked.Count == 0) return;

            // null the refs before destroying the objects, or the controller keeps missing components
            foreach (var controller in FindInScene<UEDFullController>())
                ClearAutoAddedRefs(controller, marked);

            foreach (var go in marked)
                if (go != null) Object.DestroyImmediate(go);

            Debug.Log($"[UED] Removed {marked.Count} auto-added menu/puppet object(s).");
        }

        private static void ClearAutoAddedRefs(UEDFullController controller, HashSet<GameObject> marked)
        {
            var serialized = new SerializedObject(controller);
            var changed = false;

            var menuView = serialized.FindProperty("menuView");
            if (menuView != null && menuView.objectReferenceValue is RadialMenu radialMenu &&
                radialMenu != null && radialMenu.gameObject != null && marked.Contains(radialMenu.gameObject))
            {
                menuView.objectReferenceValue = null;
                changed = true;
            }

            foreach (var field in new[] { "radialPuppet", "axisPuppet", "handGestures" })
            {
                var prop = serialized.FindProperty(field);
                if (prop == null) continue;
                var component = prop.objectReferenceValue as Component;
                if (component != null && marked.Contains(component.gameObject))
                {
                    prop.objectReferenceValue = null;
                    changed = true;
                }
            }

            if (changed) serialized.ApplyModifiedProperties();
        }

        private static void RevertAutoAddedGestureParams()
        {
            var strippedCount = 0;

            foreach (var controller in FindInScene<UEDFullController>())
            {
                var serialized = new SerializedObject(controller);
                var markerProp = serialized.FindProperty("autoAddedHandGestureParams");
                if (markerProp == null || string.IsNullOrEmpty(markerProp.stringValue)) continue;

                if (StripGestureParams(serialized, markerProp.stringValue)) strippedCount++;

                markerProp.stringValue = "";
                serialized.ApplyModifiedProperties();
            }

            if (strippedCount > 0)
                Debug.Log($"[UED] Removed {strippedCount} auto-added Hand Gesture parameter set(s).");
        }

        private static bool StripGestureParams(SerializedObject serialized, string namesCsv)
        {
            var names = namesCsv.Split(',');
            var namesProp = serialized.FindProperty("paramNames");
            var typesProp = serialized.FindProperty("paramTypes");
            var defaultsProp = serialized.FindProperty("paramDefaults");
            var syncedProp = serialized.FindProperty("paramSynced");
            if (namesProp == null) return false;

            // the four arrays are parallel and index-aligned, so every one must be filtered together
            var keepNames = new List<string>();
            var keepTypes = new List<int>();
            var keepDefaults = new List<float>();
            var keepSynced = new List<bool>();
            var removed = false;

            for (var i = 0; i < namesProp.arraySize; i++)
            {
                var name = namesProp.GetArrayElementAtIndex(i).stringValue;
                if (Contains(name, names))
                {
                    removed = true;
                    continue;
                }
                keepNames.Add(name);
                keepTypes.Add(typesProp != null ? typesProp.GetArrayElementAtIndex(i).intValue : 0);
                keepDefaults.Add(defaultsProp != null ? defaultsProp.GetArrayElementAtIndex(i).floatValue : 0f);
                keepSynced.Add(syncedProp != null ? syncedProp.GetArrayElementAtIndex(i).boolValue : false);
            }

            if (!removed) return false;

            // shrink-to-size then overwrite: stale trailing entries would survive otherwise
            WriteArray(namesProp, keepNames);
            WriteArray(typesProp, keepTypes);
            WriteArray(defaultsProp, keepDefaults);
            WriteArray(syncedProp, keepSynced);
            return true;
        }

        private static bool Contains(string value, string[] candidates)
        {
            foreach (var c in candidates)
            {
                if (c == value) return true;
            }
            return false;
        }

        private static void WriteArray<T>(SerializedProperty prop, List<T> values)
        {
            if (prop == null) return;
            prop.arraySize = values.Count;
            for (var i = 0; i < values.Count; i++)
                prop.GetArrayElementAtIndex(i).boxedValue = values[i];
        }
    }
}
