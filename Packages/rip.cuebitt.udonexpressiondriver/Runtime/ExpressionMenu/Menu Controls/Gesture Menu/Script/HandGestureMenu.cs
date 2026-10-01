using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;

#if !COMPILER_UDONSHARP && UNITY_EDITOR
using UnityEditor;
#endif

namespace UdonExpressionDriver
{
    /// <summary>
    /// World-space hand-gesture picker. Buttons are Unity UI Toggles in a ToggleGroup
    /// (single-select with no code needed); each Toggle's On Value Changed is wired in
    /// the Inspector to OnLeftChanged/OnRightChanged, and the script scans the arrays to
    /// find which one is on. Holds no synced state, so BehaviourSyncMode.None.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class HandGestureMenu : UdonSharpBehaviour
    {
        [Header("Content")]
        [SerializeField] [Tooltip("Current left-hand gesture index, 0-7")]
        private int leftGesture;

        [SerializeField] [Tooltip("Current right-hand gesture index, 0-7")]
        private int rightGesture;

        [Header("Event Handler")]
        [SerializeField] [Tooltip("Component to notify when a gesture is selected or the header is clicked")]
        private UEDPuppetHandler handler;

        [SerializeField, HideInInspector] private bool autoLinked;

        [Header("Internal")]
        [Tooltip("Header text shown at the top of the panel.")]
        [SerializeField] private TMP_Text headerLabel;
        [Tooltip("Left-hand gesture toggles (8, index = gesture value). All share one ToggleGroup; leave a slot null to skip.")]
        [SerializeField] private Toggle[] leftToggles;
        [Tooltip("Right-hand gesture toggles (8, index = gesture value). All share one ToggleGroup; leave a slot null to skip.")]
        [SerializeField] private Toggle[] rightToggles;

        private bool _suppress;

        /// <summary>Current left-hand gesture index (0-7). Setting it refreshes the toggle highlight.</summary>
        public int LeftGesture
        {
            get => leftGesture;
            set
            {
                leftGesture = value;
                _RefreshToggles();
            }
        }

        /// <summary>Current right-hand gesture index (0-7). Setting it refreshes the toggle highlight.</summary>
        public int RightGesture
        {
            get => rightGesture;
            set
            {
                rightGesture = value;
                _RefreshToggles();
            }
        }

        private void Start()
        {
            _RefreshToggles();
        }

        /// <summary>Wired to every left-hand Toggle's On Value Changed; parameterless because UdonSharp drops the event arg, so it scans for the now-on toggle.</summary>
        public void OnLeftChanged()
        {
            // we caused this change in _RefreshToggles; don't echo it back
            if (_suppress) return;
            var index = _FindOn(leftToggles);
            // no toggle on, or the group re-fired the gesture already selected
            if (index < 0 || index == leftGesture) return;
            leftGesture = index;
            // panel state only; the controller owns the synced GestureLeft/Right params
            if (handler != null) handler._OnHandGesture(leftGesture, rightGesture);
        }

        /// <summary>Wired to every right-hand Toggle's On Value Changed; see <see cref="OnLeftChanged"/>.</summary>
        public void OnRightChanged()
        {
            if (_suppress) return;
            var index = _FindOn(rightToggles);
            if (index < 0 || index == rightGesture) return;
            rightGesture = index;
            if (handler != null) handler._OnHandGesture(leftGesture, rightGesture);
        }

        /// <summary>Called by the panel's close/header button; returns to the menu.</summary>
        public void OnHeaderClicked()
        {
            if (handler != null) handler._OnPuppetClose();
        }

        private void _RefreshToggles()
        {
            // Suppress so setting isOn (which fires onValueChanged and coordinates the ToggleGroup)
            // does not echo back through OnLeftChanged/OnRightChanged while seeding from the controller.
            _suppress = true;
            _SetSelected(leftToggles, leftGesture);
            _SetSelected(rightToggles, rightGesture);
            _suppress = false;
        }

        private void _SetSelected(Toggle[] toggles, int selectedIndex)
        {
            if (toggles == null) return;
            for (var i = 0; i < toggles.Length; i++)
            {
                // a null slot lets a prefab author skip a gesture without shifting the others
                if (toggles[i] != null)
                    toggles[i].isOn = i == selectedIndex;
            }
        }

        private int _FindOn(Toggle[] toggles)
        {
            if (toggles == null) return -1;
            for (var i = 0; i < toggles.Length; i++)
            {
                // ToggleGroup allows at most one on, so the first hit is the answer
                if (toggles[i] != null && toggles[i].isOn) return i;
            }
            return -1;
        }

#if !COMPILER_UDONSHARP && UNITY_EDITOR
        public void OnValidate()
        {
            // Defer to the next editor update so the field is fully deserialized before we touch the
            // toggles (avoids ordering issues during import/undo).
            EditorApplication.delayCall += () => { if (this == null) return; _RefreshToggles(); };
        }
#endif
    }
}
