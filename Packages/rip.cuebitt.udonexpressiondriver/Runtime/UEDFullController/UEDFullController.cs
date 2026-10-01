using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace UdonExpressionDriver
{
    /// <summary>
    /// Drives a prop's Animator from expression parameters and exposes the prop's
    /// menu to a world-space view. All configuration is embedded on the component.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class UEDFullController : UEDMenuHost
    {
        private const int ParamTypeFloat = 0;
        private const int ParamTypeInt = 1;
        private const int ParamTypeBool = 2;

        private const int ControlButton = 0;
        private const int ControlToggle = 1;
        private const int ControlSubMenu = 2;
        private const int ControlTwoAxis = 3;
        private const int ControlFourAxis = 4;
        private const int ControlBack = 5;
        private const int ControlRadialPuppet = 6;
        private const int ControlHandGestures = 7;

        private const string GestureLeftName = "GestureLeft";
        private const string GestureRightName = "GestureRight";
        private const string HandGesturesControlName = "Hand Gestures";

        private const int MaxMenuStackDepth = 8;
        private const int MaxMenuControls = 8;
        private const float ToggleEpsilon = 0.001f;

        [Header("Animator")]
        [Tooltip("Animator on the prop whose parameters this controller drives.")]
        [SerializeField] private Animator animator;

        [Header("Parameters")]
        [Tooltip("Animator parameter names, one per entry.")]
        [SerializeField] private string[] paramNames;
        [Tooltip("Parameter types: 0 = float, 1 = int, 2 = bool.")]
        [SerializeField] private int[] paramTypes;
        [Tooltip("Default value for each parameter.")]
        [SerializeField] private float[] paramDefaults;
        [Tooltip("Whether each parameter is network-synced. Unsynced parameters are local to the owner.")]
        [SerializeField] private bool[] paramSynced;

        [Header("Menu")]
        [Tooltip("Start index into the control arrays for each menu; length = menu count + 1.")]
        [SerializeField] private int[] menuControlStart;
        [Tooltip("Control types: 0 = button, 1 = toggle, 2 = submenu, 3 = two-axis, 4 = four-axis, 5 = back.")]
        [SerializeField] private int[] controlTypes;
        [SerializeField] private string[] controlNames;
        [SerializeField] private Texture2D[] controlIcons;
        [Tooltip("Parameter index each control operates on, -1 for none.")]
        [SerializeField] private int[] controlParamIndex;
        [Tooltip("Value a button sets or a toggle activates.")]
        [SerializeField] private float[] controlValues;
        [Tooltip("Submenu index for submenu controls, -1 for none.")]
        [SerializeField] private int[] controlSubmenuIndex;
        [Tooltip("Start index into controlSubParams for each control's puppet sub-parameters, -1 for non-puppets.")]
        [SerializeField] private int[] controlSubParamStart;
        [Tooltip("Puppet sub-parameter parameter indices (flat), in the order the puppet emits them.")]
        [SerializeField] private int[] controlSubParams;

        [Tooltip("Radial menu view to populate from this controller's current menu level.")]
        [SerializeField] private RadialMenu menuView;
        [Tooltip("Whether interacting with this prop toggles the menu visibility.")]
        [SerializeField] private bool interactTogglesMenu = true;

        [Tooltip("World-space radial puppet control shown when a radial puppet menu item is opened.")]
        [SerializeField] private RadialPuppet radialPuppet;
        [Tooltip("World-space axis puppet control shown when a two- or four-axis puppet menu item is opened.")]
        [SerializeField] private AxisPuppet axisPuppet;
        [Tooltip("When on, a 'Hand Gestures' wedge is appended to the top menu level if the Animator uses GestureLeft or GestureRight.")]
        [SerializeField] private bool enableHandGestureEmulation;
        [Tooltip("World-space hand gesture menu shown when the Hand Gestures menu item is opened.")]
        [SerializeField] private HandGestureMenu handGestures;

        [SerializeField, HideInInspector] private RuntimeAnimatorController importedAnimatorController;
        [SerializeField, HideInInspector] private string importedMenuGuid;
        [SerializeField, HideInInspector] private string importedParametersGuid;
        [SerializeField, HideInInspector] private string generatedControllerGuid;
        [SerializeField, HideInInspector] private string generatedSourceGuid;
        [SerializeField, HideInInspector] private bool autoAddedAnimator;
        [SerializeField, HideInInspector] private string autoAddedHandGestureParams;

        [UdonSynced] private float[] _syncedValues = new float[0];
        private float[] _localValues = new float[0];
        private int[] _syncedSlot = new int[0];
        private int[] _localSlot = new int[0];
        private int[] _paramHashes = new int[0];

        private int _currentMenu;
        private int[] _menuStack = new int[MaxMenuStackDepth];
        private int _menuStackDepth;
        private int _activePuppetFlat = -1;
        private bool _activeHandGestures;

        private int _gestureLeftIndex = -1;
        private int _gestureRightIndex = -1;
        private bool _animatorUsesHandGestures;

        private void Start()
        {
            // value arrays get sized here, so nothing before this may read them
            _EnsureArrays();
            _InitParamSlots();

            // start from defaults; a deserialization replaces them with the owner's values
            _ApplyAllToAnimator();
            _RefreshMenuView();

            _InitHandGestures();

            // Start with a clean slate (no stale UI for late joiners or freshly worn props).
            _CloseAllMenus();
        }

        // the gestures wedge only appears when both the toggle and the Animator agree
        private void _InitHandGestures()
        {
            _gestureLeftIndex = _FindParamIndex(GestureLeftName);
            _gestureRightIndex = _FindParamIndex(GestureRightName);
            _animatorUsesHandGestures = _gestureLeftIndex >= 0 || _gestureRightIndex >= 0 || _GestureParamInAnimator();

            if (handGestures != null) handGestures.gameObject.SetActive(false);
        }

        private int _FindParamIndex(string name)
        {
            if (paramNames == null) return -1;
            for (var i = 0; i < paramNames.Length; i++)
            {
                if (paramNames[i] == name) return i;
            }
            return -1;
        }

        private bool _GestureParamInAnimator()
        {
            if (animator == null || animator.parameters == null) return false;
            foreach (var p in animator.parameters)
            {
                if (p.name == GestureLeftName || p.name == GestureRightName) return true;
            }
            return false;
        }

        // A half-imported prop can leave these null; treat missing arrays as empty.
        private void _EnsureArrays()
        {
            if (paramNames == null) paramNames = new string[0];
            if (paramTypes == null) paramTypes = new int[0];
            if (paramDefaults == null) paramDefaults = new float[0];
            if (paramSynced == null) paramSynced = new bool[0];
        }

        // Splits params into synced (owner-written, replicated) and local slots, then caches
        // their Animator hashes so the per-frame write path is pure array reads. Synced values
        // already delivered by the network (OnDeserialization can run before Start on late
        // joiners) are kept as-is instead of being overwritten with defaults.
        private void _InitParamSlots()
        {
            var count = paramNames.Length;
            // count first so both value arrays are allocated once, at the right size
            var syncedCount = 0;
            for (var i = 0; i < count; i++)
            {
                if (i < paramSynced.Length && paramSynced[i]) syncedCount++;
            }

            var preserveSynced = _syncedValues != null && _syncedValues.Length == syncedCount;
            if (!preserveSynced) _syncedValues = new float[syncedCount];
            _localValues = new float[count - syncedCount];
            _syncedSlot = new int[count];
            _localSlot = new int[count];
            _paramHashes = new int[count];

            var syncSlot = 0;
            var localSlot = 0;
            for (var i = 0; i < count; i++)
            {
                _paramHashes[i] = Animator.StringToHash(paramNames[i]);
                _syncedSlot[i] = -1;
                _localSlot[i] = -1;

                var def = i < paramDefaults.Length ? paramDefaults[i] : 0f;
                if (i < paramSynced.Length && paramSynced[i])
                {
                    _syncedSlot[i] = syncSlot;
                    if (!preserveSynced) _syncedValues[syncSlot] = def;
                    syncSlot++;
                }
                else
                {
                    _localSlot[i] = localSlot;
                    _localValues[localSlot] = def;
                    localSlot++;
                }
            }
        }

        /// <summary>Applies synced parameter values to the Animator when remote data arrives.</summary>
        public override void OnDeserialization()
        {
            // A late joiner can receive synced values before Start() has built the slot/hash
            // arrays; initialize them on demand so the apply below is index-safe and the
            // received values survive the Start() pass.
            if (paramNames == null || _paramHashes.Length != paramNames.Length)
            {
                _EnsureArrays();
                _InitParamSlots();
            }

            _ApplyAllToAnimator();
        }

        // Owner comes from VRChat's native per-object ownership (no custom synced state),
        // so late joiners and ownership reassignment converge automatically.
        private bool _IsOwner()
        {
            return Networking.IsOwner(gameObject);
        }

        /// <summary>Closes all open menu UI when the local player loses ownership.</summary>
        public override void OnOwnershipTransferred(VRCPlayerApi player)
        {
            // Ownership loss (drop, takeover, or the owner leaving) hides any open
            // menu/puppet/hand-gesture UI so a non-owner never sees or drives the prop.
            if (!_IsOwner()) _CloseAllMenus();
        }

        /// <summary>Sets a parameter by index using its float representation. Synced parameters are only written by the owner; non-owner writes are ignored.</summary>
        public void _SetParam(int index, float value)
        {
            if (paramNames == null || index < 0 || index >= paramNames.Length) return;

            if (index < paramSynced.Length && paramSynced[index])
            {
                // non-owner writes get overwritten by the owner's next sync, so drop them
                if (!Networking.IsOwner(gameObject)) return;
                _syncedValues[_syncedSlot[index]] = value;
                RequestSerialization();
            }
            else
            {
                _localValues[_localSlot[index]] = value;
            }

            // the owner drives its own Animator; remotes get theirs on deserialization
            _ApplyToAnimator(index, value);
        }

        /// <summary>Sets a float parameter by index.</summary>
        public void _SetFloatParam(int index, float value)
        {
            _SetParam(index, value);
        }

        /// <summary>Sets an int parameter by index.</summary>
        public void _SetIntParam(int index, int value)
        {
            _SetParam(index, value);
        }

        /// <summary>Sets a bool parameter by index (written as 1/0).</summary>
        public void _SetBoolParam(int index, bool value)
        {
            // every value is a float in the arrays, so bools become 1/0
            _SetParam(index, value ? 1f : 0f);
        }

        /// <summary>Returns the current value of a parameter as a float.</summary>
        public float _GetParam(int index)
        {
            if (paramNames == null || index < 0 || index >= paramNames.Length) return 0f;
            if (index < paramSynced.Length && paramSynced[index]) return _syncedValues[_syncedSlot[index]];
            return _localValues[_localSlot[index]];
        }

        /// <summary>Returns the number of expression parameters.</summary>
        public int _GetParamCount()
        {
            return paramNames == null ? 0 : paramNames.Length;
        }

        /// <summary>Resets all parameters to their defaults. Only the owner's writes are synced.</summary>
        public void _ResetParameters()
        {
            if (paramNames == null) return;

            var owner = Networking.IsOwner(gameObject);
            var changed = false;
            for (var i = 0; i < paramNames.Length; i++)
            {
                var def = i < paramDefaults.Length ? paramDefaults[i] : 0f;
                if (i < paramSynced.Length && paramSynced[i])
                {
                    // a non-owner still resets its local params, it just syncs nothing
                    if (!owner) continue;
                    _syncedValues[_syncedSlot[i]] = def;
                    changed = true;
                }
                else
                {
                    _localValues[_localSlot[i]] = def;
                }
            }

            // one sync for the whole reset; changed keeps a no-op reset off the wire
            if (owner && changed) RequestSerialization();
            _ApplyAllToAnimator();
        }

        /// <summary>Returns the number of menu levels (top level included).</summary>
        public int _GetMenuCount()
        {
            if (menuControlStart == null) return 0;
            return menuControlStart.Length > 0 ? menuControlStart.Length - 1 : 0;
        }

        /// <summary>Returns the index of the menu level currently displayed.</summary>
        public int _GetCurrentMenu()
        {
            return _currentMenu;
        }

        /// <summary>Returns how many controls the current menu level shows (including Back and the Hand Gestures wedge).</summary>
        public int _GetCurrentMenuControlCount()
        {
            var count = _TopLevelBaseControlCount();
            // the gestures wedge is appended here, it has no entry in controlTypes
            if (_currentMenu == 0 && _HandGesturesVisible()) count++;
            return count;
        }

        // Base control count for the current level, capped so the Back button and
        // Hand Gestures wedge never overflow the radial menu's segment array.
        private int _TopLevelBaseControlCount()
        {
            var count = _NextControlStart() - _CurrentControlStart();
            if (count < 0) count = 0;
            if (count > MaxMenuControls) count = MaxMenuControls;
            // Reserve a slot for the Hand Gestures wedge at the top level.
            if (_currentMenu == 0 && _HandGesturesVisible() && count > MaxMenuControls - 1) count = MaxMenuControls - 1;
            return count;
        }

        private bool _HandGesturesVisible()
        {
            return enableHandGestureEmulation && _animatorUsesHandGestures;
        }

        private bool _IsHandGesturesSlot(int controlIndex)
        {
            // the cap in _TopLevelBaseControlCount reserves exactly this index
            return _currentMenu == 0 && _HandGesturesVisible() && controlIndex == _TopLevelBaseControlCount();
        }

        /// <summary>Returns the display name of the control at the given (display) index.</summary>
        public string _GetControlName(int controlIndex)
        {
            if (_IsHandGesturesSlot(controlIndex)) return HandGesturesControlName;

            var flat = _DisplayFlat(controlIndex);
            if (controlNames == null || flat < 0 || flat >= controlNames.Length) return "";
            return controlNames[flat];
        }

        /// <summary>Returns the icon of the control at the given (display) index.</summary>
        public Texture2D _GetControlIcon(int controlIndex)
        {
            if (_IsHandGesturesSlot(controlIndex)) return null;

            var flat = _DisplayFlat(controlIndex);
            if (controlIcons == null || flat < 0 || flat >= controlIcons.Length) return null;
            return controlIcons[flat];
        }

        /// <summary>Navigates to the given menu level, pushing the current one onto the back stack.</summary>
        public void _OpenMenu(int menuIndex)
        {
            if (menuIndex < 0 || menuIndex >= _GetMenuCount()) return;

            // depth-capped so a menu that points at itself can't grow the stack forever
            if (_menuStackDepth < MaxMenuStackDepth)
            {
                _menuStack[_menuStackDepth] = _currentMenu;
                _menuStackDepth++;
            }

            _currentMenu = menuIndex;
            _RefreshMenuView();
        }

        /// <summary>Returns to the previous menu level (top level when the back stack is empty).</summary>
        public void _Back()
        {
            // an empty stack means we are already at the top, so Back is a reset to level 0
            if (_menuStackDepth <= 0)
            {
                _currentMenu = 0;
                _RefreshMenuView();
                return;
            }

            _menuStackDepth--;
            _currentMenu = _menuStack[_menuStackDepth];
            _RefreshMenuView();
        }

        private void _RefreshMenuView()
        {
            if (menuView == null) return;

            // Already clamped to [0, MaxMenuControls] by _GetCurrentMenuControlCount.
            var count = _GetCurrentMenuControlCount();

            var names = new string[count];
            var icons = new Texture2D[count];
            // display indices, not flat ones, since Back and the gestures wedge shift them
            for (var i = 0; i < count; i++)
            {
                names[i] = _GetControlName(i);
                icons[i] = _GetControlIcon(i);
            }

            menuView.SetContent(names, icons);
        }

        /// <summary>Shows or hides the menu view, repositioning it in front of the player when shown.</summary>
        public void _SetMenuVisible(bool visible)
        {
            if (menuView == null) return;
            // place before showing, or it pops in at the prop's origin for a frame
            if (visible) _PlaceMenuView();
            menuView._SetVisible(visible);
        }

        /// <summary>Toggles the menu view (owner only; dismisses an open puppet or gesture panel first).</summary>
        public void _ToggleMenu()
        {
            if (!_IsOwner()) return;

            // a puppet or gesture panel sits over the menu, so toggle dismisses it first
            if (_activePuppetFlat >= 0 || _activeHandGestures)
            {
                _OnPuppetClose();
                return;
            }

            if (menuView == null) return;
            var wasVisible = menuView.gameObject.activeSelf;
            if (!wasVisible) _PlaceMenuView();
            menuView._ToggleVisible();
            // Closing returns to the top level so the next open starts fresh.
            if (wasVisible) _ResetMenuNavigation();
        }

        // Like the puppet controls, the menu sits in front of the player's head so it is
        // visible immediately instead of at the prop's origin. Nothing to do before a local player.
        private void _PlaceMenuView()
        {
            if (menuView == null) return;
            var player = Networking.LocalPlayer;
            if (player == null) return;

            var head = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            var pos = head.position + head.rotation * Vector3.forward * 1.0f;
            pos.y -= 0.5f;

            menuView.transform.position = pos;
            menuView.transform.rotation = Quaternion.LookRotation(pos - head.position, Vector3.up);
        }

        /// <summary>Toggles the menu when the local player interacts (owner only).</summary>
        public override void Interact()
        {
            if (!interactTogglesMenu) return;
            if (!_IsOwner()) return;
            _ToggleMenu();
        }

        /// <summary>Handles a press on a control within the current menu level (owner only).</summary>
        public override void _OnControlPressed(int controlIndex)
        {
            if (!_IsOwner()) return;

            var count = _GetCurrentMenuControlCount();
            if (controlIndex < 0 || controlIndex >= count) return;

            if (_IsHandGesturesSlot(controlIndex))
            {
                _OpenHandGestureMenu();
                return;
            }

            var flat = _DisplayFlat(controlIndex);
            if (controlTypes == null || flat >= controlTypes.Length) return;

            var type = controlTypes[flat];
            if (type == ControlButton)
            {
                var param = _ControlParam(flat);
                if (param >= 0) _SetParam(param, _ControlValue(flat));
            }
            else if (type == ControlToggle)
            {
                var param = _ControlParam(flat);
                if (param < 0) return;

                var value = _ControlValue(flat);
                // pressing the active toggle restores the param default, VRChat's toggle-off
                var current = _GetParam(param);
                if (Mathf.Abs(current - value) < ToggleEpsilon)
                    _SetParam(param, param < paramDefaults.Length ? paramDefaults[param] : 0f);
                else
                    _SetParam(param, value);
            }
            else if (type == ControlSubMenu)
            {
                var subMenu = _ControlSubmenu(flat);
                if (subMenu >= 0) _OpenMenu(subMenu);
            }
            else if (type == ControlBack)
            {
                _Back();
            }
            else if (type == ControlTwoAxis || type == ControlFourAxis || type == ControlRadialPuppet)
            {
                // puppets write their params through callbacks, not from this press
                _OpenPuppet(flat);
            }
        }

        // Hides the menu and lets the puppet take the spot in front of the player's head.
        private void _OpenPuppet(int flat)
        {
            if (!_IsOwner()) return;

            var type = controlTypes != null && flat < controlTypes.Length ? controlTypes[flat] : -1;
            UdonSharpBehaviour puppet = null;
            // radial and axis are separate objects; only the chosen one is left enabled
            if (type == ControlRadialPuppet) puppet = radialPuppet;
            else if (type == ControlTwoAxis || type == ControlFourAxis) puppet = axisPuppet;
            if (puppet == null) return;

            // flat index, not display: puppet callbacks resolve sub-params from it
            _activePuppetFlat = flat;
            _SetMenuVisible(false);

            if (radialPuppet != null) radialPuppet.gameObject.SetActive(puppet == radialPuppet);
            if (axisPuppet != null) axisPuppet.gameObject.SetActive(puppet == axisPuppet);

            var name = controlNames != null && flat < controlNames.Length ? controlNames[flat] : "";

            if (type == ControlRadialPuppet)
            {
                var radial = (RadialPuppet)puppet;
                if (radial != null)
                {
                    radial.Label = name;
                    var p0 = _PuppetSubParam(flat, 0);
                    if (p0 >= 0) radial.Value = _GetParam(p0);
                }
            }
            else
            {
                var axis = (AxisPuppet)puppet;
                if (axis != null)
                {
                    axis.Label = name;
                    axis.AxisPuppetType = type == ControlTwoAxis ? AxisPuppetType.Two : AxisPuppetType.Four;

                    if (type == ControlTwoAxis)
                    {
                        var px = _PuppetSubParam(flat, 0);
                        var py = _PuppetSubParam(flat, 1);
                        // the control works 0..1, the params it drives are -1..1
                        if (px >= 0 && py >= 0)
                            axis.PuppetValue = new Vector2((_GetParam(px) + 1f) * 0.5f, (_GetParam(py) + 1f) * 0.5f);
                    }
                    else
                    {
                        // four directionals are four params, so the control just starts centered
                        axis.PuppetValue = new Vector2(0.5f, 0.5f);
                    }
                }
            }

            var player = Networking.LocalPlayer;
            if (player != null)
            {
                var head = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
                var pos = head.position + head.rotation * Vector3.forward * 0.8f;
                pos.y -= 0.4f;
                puppet.transform.position = pos;
                // World-space UI reads from its -Z face, so point +Z away from the user's head.
                puppet.transform.rotation = Quaternion.LookRotation(pos - head.position, Vector3.up);
            }
        }

        /// <summary>Writes a radial puppet value into the active puppet's sub-parameter (owner only).</summary>
        public override void _OnPuppetRadial(float value)
        {
            if (!_IsOwner()) return;
            _WritePuppetSubParam(0, value);
        }

        /// <summary>Writes two-axis puppet values into the active puppet's sub-parameters (owner only).</summary>
        public override void _OnPuppetTwo(float x, float y)
        {
            if (!_IsOwner()) return;
            _WritePuppetSubParam(0, x);
            _WritePuppetSubParam(1, y);
        }

        /// <summary>Writes four-axis puppet values into the active puppet's sub-parameters (owner only).</summary>
        public override void _OnPuppetFour(float negX, float posX, float negY, float posY)
        {
            if (!_IsOwner()) return;
            _WritePuppetSubParam(0, negX);
            _WritePuppetSubParam(1, posX);
            _WritePuppetSubParam(2, negY);
            _WritePuppetSubParam(3, posY);
        }

        /// <summary>Closes open puppet/gesture panels and brings the menu back (owner only).</summary>
        public override void _OnPuppetClose()
        {
            if (!_IsOwner()) return;

            _activePuppetFlat = -1;
            _activeHandGestures = false;
            if (radialPuppet != null) radialPuppet.gameObject.SetActive(false);
            if (axisPuppet != null) axisPuppet.gameObject.SetActive(false);
            if (handGestures != null) handGestures.gameObject.SetActive(false);
            // the menu was hidden when the puppet opened, so bring it back
            _SetMenuVisible(true);
        }

        // Hides every piece of menu UI and resets navigation so the next open starts fresh.
        // Called when the local player loses ownership so a non-owner never sees or drives the prop.
        private void _CloseAllMenus()
        {
            _activePuppetFlat = -1;
            _activeHandGestures = false;
            _SetMenuVisible(false);
            if (radialPuppet != null) radialPuppet.gameObject.SetActive(false);
            if (axisPuppet != null) axisPuppet.gameObject.SetActive(false);
            if (handGestures != null) handGestures.gameObject.SetActive(false);
            _ResetMenuNavigation();
        }

        private void _ResetMenuNavigation()
        {
            _currentMenu = 0;
            _menuStackDepth = 0;
            _RefreshMenuView();
        }

        private void _OpenHandGestureMenu()
        {
            if (!_IsOwner()) return;
            if (handGestures == null) return;

            _activeHandGestures = true;
            _SetMenuVisible(false);

            // one panel at a time, all three share the spot in front of the player
            if (radialPuppet != null) radialPuppet.gameObject.SetActive(false);
            if (axisPuppet != null) axisPuppet.gameObject.SetActive(false);
            handGestures.gameObject.SetActive(true);

            // open on the live gesture rather than a blank panel
            if (_gestureLeftIndex >= 0) handGestures.LeftGesture = Mathf.RoundToInt(_GetParam(_gestureLeftIndex));
            if (_gestureRightIndex >= 0) handGestures.RightGesture = Mathf.RoundToInt(_GetParam(_gestureRightIndex));

            var player = Networking.LocalPlayer;
            if (player != null)
            {
                var head = player.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
                var pos = head.position + head.rotation * Vector3.forward * 0.8f;
                pos.y -= 0.4f;
                handGestures.transform.position = pos;
                // World-space UI reads from its -Z face, so point +Z away from the user's head.
                handGestures.transform.rotation = Quaternion.LookRotation(pos - head.position, Vector3.up);
            }
        }

        /// <summary>Writes the selected left/right gestures into the synced GestureLeft/GestureRight params (owner only).</summary>
        public override void _OnHandGesture(int left, int right)
        {
            if (!_IsOwner()) return;
            if (_gestureLeftIndex >= 0) _SetIntParam(_gestureLeftIndex, left);
            if (_gestureRightIndex >= 0) _SetIntParam(_gestureRightIndex, right);
        }

        private void _WritePuppetSubParam(int index, float value)
        {
            // index is positional: the order the puppet emits values fixes the param
            var param = _PuppetSubParam(_activePuppetFlat, index);
            if (param >= 0) _SetParam(param, value);
        }

        private int _PuppetSubParam(int flat, int index)
        {
            if (flat < 0 || controlSubParamStart == null || controlSubParams == null) return -1;
            if (flat >= controlSubParamStart.Length) return -1;
            if (index >= _PuppetSubParamCount(flat)) return -1;

            var start = controlSubParamStart[flat];
            if (start < 0 || start + index >= controlSubParams.Length) return -1;
            return controlSubParams[start + index];
        }

        private int _PuppetSubParamCount(int flat)
        {
            if (controlTypes == null || flat < 0 || flat >= controlTypes.Length) return 0;
            var type = controlTypes[flat];
            if (type == ControlTwoAxis) return 2;
            if (type == ControlFourAxis) return 4;
            if (type == ControlRadialPuppet) return 1;
            return 0;
        }

        private int _CurrentControlStart()
        {
            if (menuControlStart == null) return 0;
            return _currentMenu < menuControlStart.Length ? menuControlStart[_currentMenu] : 0;
        }

        private int _NextControlStart()
        {
            if (menuControlStart == null) return 0;
            var next = _currentMenu + 1;
            return next < menuControlStart.Length ? menuControlStart[next] : 0;
        }

        // Maps a displayed wedge index to its flat control index. A submenu's Back control is
        // always shown first (display index 0), matching VRChat's expressions menu.
        private int _DisplayFlat(int controlIndex)
        {
            var start = _CurrentControlStart();
            var end = _NextControlStart();
            if (end < start) end = start;

            var displayCount = end - start;
            if (displayCount > MaxMenuControls) displayCount = MaxMenuControls;

            // locate Back so it can be displayed first instead of in its authored position
            var backFlat = -1;
            if (controlTypes != null)
            {
                for (var f = start; f < start + displayCount; f++)
                {
                    if (f >= controlTypes.Length) break;
                    if (controlTypes[f] == ControlBack) { backFlat = f; break; }
                }
            }

            if (backFlat < 0) return start + controlIndex;

            if (controlIndex == 0) return backFlat;

            // everything else shifts up one display slot to close the Back gap
            var seen = 0;
            for (var f = start; f < start + displayCount; f++)
            {
                if (f == backFlat) continue;
                if (seen == controlIndex - 1) return f;
                seen++;
            }

            return start + controlIndex;
        }

        private int _ControlParam(int flat)
        {
            return controlParamIndex != null && flat < controlParamIndex.Length ? controlParamIndex[flat] : -1;
        }

        private float _ControlValue(int flat)
        {
            return controlValues != null && flat < controlValues.Length ? controlValues[flat] : 0f;
        }

        private int _ControlSubmenu(int flat)
        {
            return controlSubmenuIndex != null && flat < controlSubmenuIndex.Length ? controlSubmenuIndex[flat] : -1;
        }

        // Pushes every param's current value into the Animator. Used on start and whenever a
        // sync arrives so remote clients mirror the owner's values.
        private void _ApplyAllToAnimator()
        {
            if (animator == null || paramNames == null) return;

            for (var i = 0; i < paramNames.Length; i++)
            {
                var synced = i < paramSynced.Length && paramSynced[i];
                var value = synced ? _syncedValues[_syncedSlot[i]] : _localValues[_localSlot[i]];
                _ApplyToAnimator(i, value);
            }
        }

        private void _ApplyToAnimator(int index, float value)
        {
            if (animator == null || index >= _paramHashes.Length) return;

            var type = index < paramTypes.Length ? paramTypes[index] : ParamTypeFloat;
            // one float per param, so int and bool are converted on the way into the Animator
            var hash = _paramHashes[index];
            if (type == ParamTypeInt) animator.SetInteger(hash, (int)value);
            // bools are stored as 1/0, so anything past half reads as true
            else if (type == ParamTypeBool) animator.SetBool(hash, value > 0.5f);
            else animator.SetFloat(hash, value);
        }

#if !COMPILER_UDONSHARP && UNITY_EDITOR
        private void OnValidate()
        {
            if (animator == null) animator = GetComponentInChildren<Animator>();
        }
#endif
    }
}
