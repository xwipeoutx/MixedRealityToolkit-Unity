﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.MixedReality.Toolkit.Input;
using Microsoft.MixedReality.Toolkit.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Microsoft.MixedReality.Toolkit.UI
{
    /// <summary>
    /// Uses input and action data to declare a set of states
    /// Maintains a collection of themes that react to state changes and provide sensory feedback
    /// Passes state information and input data on to receivers that detect patterns and does stuff.
    /// </summary>
    // TODO: Make sure all shader values are batched by theme

    [System.Serializable]

    public class Interactable :
        MonoBehaviour,
        IMixedRealityFocusChangedHandler,
        IMixedRealityFocusHandler,
        IMixedRealityInputHandler,
        IMixedRealitySpeechHandler,
        IMixedRealityTouchHandler,
        IMixedRealityInputHandler<Vector2>,
        IMixedRealityInputHandler<Vector3>,
        IMixedRealityInputHandler<MixedRealityPose>
    {
        /// <summary>
        /// Setup the input system
        /// </summary>
        private static IMixedRealityInputSystem inputSystem = null;
        protected static IMixedRealityInputSystem InputSystem
        {
            get
            {
                if (inputSystem == null)
                {
                    MixedRealityServiceRegistry.TryGetService<IMixedRealityInputSystem>(out inputSystem);
                }
                return inputSystem;
            }
        }

        protected readonly List<IMixedRealityPointer> focusingPointers = new List<IMixedRealityPointer>();
        /// <summary>
        /// Pointers that are focusing the interactable
        /// </summary>
        public List<IMixedRealityPointer> FocusingPointers => focusingPointers;

        protected readonly HashSet<IMixedRealityInputSource> pressingInputSources = new HashSet<IMixedRealityInputSource>();
        /// <summary>
        /// Input sources that are pressing the interactable
        /// </summary>
        public HashSet<IMixedRealityInputSource> PressingInputSources => pressingInputSources;

        // is the interactable enabled?
        public bool Enabled = true;
        // a collection of states and basic state logic
        public States States;
        // the state logic for comparing state
        public InteractableStates StateManager;
        // which action is this interactable listening for
        public MixedRealityInputAction InputAction;

        // the id of the selected inputAction, for serialization
        [HideInInspector]
        public int InputActionId;
        // is the interactable listening to global events
        public bool IsGlobal = false;
        // a way of adding more layers of states for toggles
        public int Dimensions = 1;
        // is the interactive selectable
        public bool CanSelect = true;
        // can deselect a toggle, a radial button or tab would set this to false
        public bool CanDeselect = true;
        // a voice command to fire a click event
        public string VoiceCommand = "";
        // does the voice command require this to have focus?
        public bool RequiresFocus = true;

        /// <summary>
        /// Does this interactable require focus
        /// </summary>
        public bool FocusEnabled { get { return !IsGlobal; } set { IsGlobal = !value; } }

        // list of profiles can match themes with gameObjects
        public List<InteractableProfileItem> Profiles = new List<InteractableProfileItem>();
        // Base onclick event
        public UnityEvent OnClick = new UnityEvent();
        // list of events added to this interactable
        public List<InteractableEvent> Events = new List<InteractableEvent>();
        // the list of running theme instances to receive state changes
        public List<InteractableThemeBase> runningThemesList = new List<InteractableThemeBase>();

        // the list of profile settings, so theme values are not directly effected
        protected List<ProfileSettings> runningProfileSettings = new List<ProfileSettings>();
        // directly manipulate a theme value, skip blending
        protected bool forceUpdate = false;

        // basic button states
        public bool HasFocus { get; private set; }
        public bool HasPress { get; private set; }
        public bool IsDisabled { get; private set; }

        // advanced button states from InteractableStates.InteractableStateEnum
        public bool IsTargeted { get; private set; }
        public bool IsInteractive { get; private set; }
        public bool HasObservationTargeted { get; private set; }
        public bool HasObservation { get; private set; }
        public bool IsVisited { get; private set; }
        public bool IsToggled { get; private set; }
        public bool HasGesture { get; private set; }
        public bool HasGestureMax { get; private set; }
        public bool HasCollision { get; private set; }
        public bool HasVoiceCommand { get; private set; }
        public bool HasPhysicalTouch { get; private set; }
        public bool HasCustom { get; private set; }
        public bool HasGrab { get; private set; }

        // internal cached states
        protected State lastState;
        protected bool wasDisabled = false;

        // check for isGlobal or RequiresFocus changes
        protected bool requiresFocusValueCheck;
        protected bool isGlobalValueCheck;

        // cache of current dimension
        protected int dimensionIndex = 0;

        // allows for switching colliders without firing a lose focus immediately
        // for advanced controls like drop-downs
        protected float rollOffTime = 0.25f;
        protected float rollOffTimer = 0.25f;

        // cache voice commands
        protected string[] voiceCommands;

        // IInteractableEvents
        protected List<IInteractableHandler> handlers = new List<IInteractableHandler>();

        protected Coroutine globalTimer;

        // 
        // Clicking
        //

        // A click must occur within this many seconds after an input down
        protected float clickTime = 1.5f;
        protected Coroutine clickValidTimer;
        // how many clicks does it take?
        protected int clickCount = 0;
        /// <summary>
        /// how many times this interactable was clicked
        /// good for checking when a click event occurs.
        /// </summary>
        public int ClickCount => clickCount;

        // 
        // Variables for determining gesture state
        //

        /// <summary>
        /// The position of the controller when input down occurs.
        /// Used to determine when controller has moved far enough to trigger gesture
        /// </summary>
        protected Vector3? dragStartPosition = null;
        // Input must move at least this distance before a gesture is considered started, for 2D input like thumbstick
        static readonly float gestureStartThresholdVector2 = 0.1f;
        // Input must move at least this distance before a gesture is considered started, for 3D input
        static readonly float gestureStartThresholdVector3 = 0.05f;
        // Input must move at least this distance before a gesture is considered started, for
        // mixed reality pose input. This is the distance and hand or controller needs to move
        static readonly float gestureStartThresholdMixedRealityPose = 0.1f;


        public void AddHandler(IInteractableHandler handler)
        {
            if (!handlers.Contains(handler))
            {
                handlers.Add(handler);
            }
        }

        public void RemoveHandler(IInteractableHandler handler)
        {
            if (handlers.Contains(handler))
            {
                handlers.Remove(handler);
            }
        }

        #region InspectorHelpers
        public static bool TryGetInputActions(out string[] descriptionsArray)
        {
            if (!MixedRealityToolkit.ConfirmInitialized() || !MixedRealityToolkit.Instance.HasActiveProfile)
            {
                descriptionsArray = null;
                return false;
            }

            MixedRealityInputAction[] actions = InputSystem.InputSystemProfile.InputActionsProfile.InputActions;

            descriptionsArray = new string[actions.Length];
            for (int i = 0; i < actions.Length; i++)
            {
                descriptionsArray[i] = actions[i].Description;
            }

            return true;
        }

        /// <summary>
        /// Returns a list of states assigned to the Interactable
        /// </summary>
        /// <returns></returns>
        public State[] GetStates()
        {
            if (States != null)
            {
                return States.GetStates();
            }

            return new State[0];
        }
        #endregion InspectorHelpers

        #region MonoBehaviorImplementation

        protected virtual void Awake()
        {

            if (States == null)
            {
                States = States.GetDefaultInteractableStates();
            }
            InputAction = ResolveInputAction(InputActionId);
            SetupEvents();
            SetupThemes();
            SetupStates();
        }

        private void OnEnable()
        {
            if (IsGlobal || RequiresFocus)
            {
                InputSystem.Register(gameObject);
            }

            requiresFocusValueCheck = RequiresFocus;
            isGlobalValueCheck = IsGlobal;

            focusingPointers.RemoveAll((focusingPointer) => (Interactable)focusingPointer.FocusTarget != this);

            if (focusingPointers.Count == 0)
            {
                ResetBaseStates();
            }
        }

        private void OnDisable()
        {
            if (IsGlobal || RequiresFocus)
            {
                InputSystem.Unregister(gameObject);
            }
        }

        private void RegisterGlobalListeners(bool register)
        {
            if (register)
            {
                InputSystem.Register(gameObject);
            }
            else
            {
                InputSystem.Unregister(gameObject);
            }
        }

        protected virtual void Update()
        {
            if (rollOffTimer < rollOffTime && HasPress)
            {
                rollOffTimer += Time.deltaTime;

                if (rollOffTimer >= rollOffTime)
                {
                    SetPress(false);
                }
            }

            for (int i = 0; i < Events.Count; i++)
            {
                if (Events[i].Receiver != null)
                {
                    Events[i].Receiver.OnUpdate(StateManager, this);
                }
            }

            for (int i = 0; i < runningThemesList.Count; i++)
            {
                if (runningThemesList[i].Loaded)
                {
                    runningThemesList[i].OnUpdate(StateManager.CurrentState().ActiveIndex, this, forceUpdate);
                }
            }

            if (lastState != StateManager.CurrentState())
            {
                for (int i = 0; i < handlers.Count; i++)
                {
                    if (handlers[i] != null)
                    {
                        handlers[i].OnStateChange(StateManager, this);
                    }
                }
            }

            if (forceUpdate)
            {
                forceUpdate = false;
            }

            if (IsDisabled == Enabled)
            {
                SetDisabled(!Enabled);
            }

            lastState = StateManager.CurrentState();
            
            if (requiresFocusValueCheck != RequiresFocus || isGlobalValueCheck != IsGlobal)
            {
                requiresFocusValueCheck = RequiresFocus;
                isGlobalValueCheck = IsGlobal;

                RegisterGlobalListeners(RequiresFocus || IsGlobal);
            }
        }

        #endregion MonoBehaviorImplimentation

        #region InteractableInitiation

        /// <summary>
        /// starts the StateManager
        /// </summary>
        protected virtual void SetupStates()
        {
            StateManager = States.SetupLogic();
        }

        /// <summary>
        /// Creates the event receiver instances from the Events list
        /// </summary>
        protected virtual void SetupEvents()
        {
            InteractableTypesContainer interactableTypes = InteractableEvent.GetEventTypes();

            for (int i = 0; i < Events.Count; i++)
            {
                Events[i].Receiver = InteractableEvent.GetReceiver(Events[i], interactableTypes);
                Events[i].Receiver.Host = this;
            }
        }

        /// <summary>
        /// Creates the list of theme instances based on all the theme settings
        /// </summary>
        protected virtual void SetupThemes()
        {
            runningThemesList = new List<InteractableThemeBase>();
            runningProfileSettings = new List<ProfileSettings>();
            for (int i = 0; i < Profiles.Count; i++)
            {
                ProfileSettings profileSettings = new ProfileSettings();
                List<ThemeSettings> themeSettingsList = new List<ThemeSettings>();
                for (int j = 0; j < Profiles[i].Themes.Count; j++)
                {
                    Theme theme = Profiles[i].Themes[j];
                    ThemeSettings themeSettings = new ThemeSettings();
                    if (Profiles[i].Target != null && theme != null)
                    {
                        List<InteractableThemePropertySettings> tempSettings = new List<InteractableThemePropertySettings>();
                        for (int n = 0; n < theme.Settings.Count; n++)
                        {
                            InteractableThemePropertySettings settings = theme.Settings[n];
                            settings.Theme = InteractableProfileItem.GetTheme(settings, Profiles[i].Target);

                            // add themes to theme list based on dimension
                            if (j == dimensionIndex)
                            {
                                runningThemesList.Add(settings.Theme);
                            }

                            tempSettings.Add(settings);
                        }

                        themeSettings.Settings = tempSettings;
                        themeSettingsList.Add(themeSettings);
                    }
                }

                profileSettings.ThemeSettings = themeSettingsList;
                runningProfileSettings.Add(profileSettings);
            }
        }

        #endregion InteractableInitiation

        #region SetButtonStates

        /// <summary>
        /// Grabs the state value index
        /// </summary>
        /// <param name="state"></param>
        /// <returns></returns>
        public int GetStateValue(InteractableStates.InteractableStateEnum state)
        {
            if (StateManager != null)
            {
                return StateManager.GetStateValue((int)state);
            }

            return 0;
        }

        /// <summary>
        /// Handle focus state changes
        /// </summary>
        /// <param name="focus"></param>
        public virtual void SetFocus(bool focus)
        {
            HasFocus = focus;
            if (!focus && HasPress)
            {
                rollOffTimer = 0;
            }
            else
            {
                rollOffTimer = rollOffTime;
            }

            SetState(InteractableStates.InteractableStateEnum.Focus, focus);
        }

        public virtual void SetPress(bool press)
        {
            HasPress = press;
            SetState(InteractableStates.InteractableStateEnum.Pressed, press);
        }

        public virtual void SetDisabled(bool disabled)
        {
            IsDisabled = disabled;
            Enabled = !disabled;
            SetState(InteractableStates.InteractableStateEnum.Disabled, disabled);
        }

        public virtual void SetTargeted(bool targeted)
        {
            IsTargeted = targeted;
            SetState(InteractableStates.InteractableStateEnum.Targeted, targeted);
        }

        public virtual void SetInteractive(bool interactive)
        {
            IsInteractive = interactive;
            SetState(InteractableStates.InteractableStateEnum.Interactive, interactive);
        }

        public virtual void SetObservationTargeted(bool targeted)
        {
            HasObservationTargeted = targeted;
            SetState(InteractableStates.InteractableStateEnum.ObservationTargeted, targeted);
        }

        public virtual void SetObservation(bool observation)
        {
            HasObservation = observation;
            SetState(InteractableStates.InteractableStateEnum.Observation, observation);
        }

        public virtual void SetVisited(bool visited)
        {
            IsVisited = visited;
            SetState(InteractableStates.InteractableStateEnum.Visited, visited);
        }

        public virtual void SetToggled(bool toggled)
        {
            IsToggled = toggled;
            SetState(InteractableStates.InteractableStateEnum.Toggled, toggled);
        }

        public virtual void SetGesture(bool gesture)
        {
            HasGesture = gesture;
            SetState(InteractableStates.InteractableStateEnum.Gesture, gesture);
        }

        public virtual void SetGestureMax(bool gesture)
        {
            HasGestureMax = gesture;
            SetState(InteractableStates.InteractableStateEnum.GestureMax, gesture);
        }

        public virtual void SetCollision(bool collision)
        {
            HasCollision = collision;
            SetState(InteractableStates.InteractableStateEnum.Collision, collision);
        }

        public virtual void SetCustom(bool custom)
        {
            HasCustom = custom;
            SetState(InteractableStates.InteractableStateEnum.Custom, custom);
        }

        public virtual void SetVoiceCommand(bool voice)
        {
            HasVoiceCommand = voice;
            SetState(InteractableStates.InteractableStateEnum.VoiceCommand, voice);
        }

        public virtual void SetPhysicalTouch(bool touch)
        {
            HasPhysicalTouch = touch;
            SetState(InteractableStates.InteractableStateEnum.PhysicalTouch, touch);
        }

        public virtual void SetGrab(bool grab)
        {
            HasGrab = grab;
            SetState(InteractableStates.InteractableStateEnum.Grab, grab);
        }

        /// <summary>
        /// a public way to set state directly
        /// </summary>
        /// <param name="state"></param>
        /// <param name="value"></param>
        public void SetState(InteractableStates.InteractableStateEnum state, bool value)
        {
            if (StateManager != null)
            {
                StateManager.SetStateValue(state, value ? 1 : 0);
            }

            UpdateState();
        }

        /// <summary>
        /// runs the state logic and sets state based on the current state values
        /// </summary>
        protected virtual void UpdateState()
        {
            StateManager.CompareStates();
        }

        /// <summary>
        /// Reset the basic interaction states
        /// </summary>
        public void ResetBaseStates()
        {
            // reset states
            SetFocus(false);
            SetPress(false);
            SetPhysicalTouch(false);
            SetGrab(false);
            SetGesture(false);
            SetGestureMax(false);
            SetVoiceCommand(false);

            if (globalTimer != null)
            {
                StopCoroutine(globalTimer);
                globalTimer = null;
            }

            dragStartPosition = null;
        }

        /// <summary>
        /// Reset all states in the Interactable and pointer information
        /// </summary>
        public void ResetAllStates()
        {
            focusingPointers.Clear();
            pressingInputSources.Clear();
            ResetBaseStates();
            SetCollision(false);
            SetCustom(false);
            SetObservation(false);
            SetObservationTargeted(false);
            SetInteractive(false);
            SetCustom(false);
            SetTargeted(false);
            SetToggled(false);
            SetVisited(false);
        }

        #endregion SetButtonStates

        #region PointerManagement

        #endregion PointerManagement

        #region MixedRealityFocusChangedHandlers

        public void OnBeforeFocusChange(FocusEventData eventData)
        {
            if (!CanInteract())
            {
                return;
            }

            if (eventData.NewFocusedObject == null)
            {
                focusingPointers.Remove(eventData.Pointer);
            }
            else if (eventData.NewFocusedObject.transform.IsChildOf(gameObject.transform))
            {
                if (!focusingPointers.Contains(eventData.Pointer))
                {
                    focusingPointers.Add(eventData.Pointer);
                }
            }
            else if (eventData.OldFocusedObject.transform.IsChildOf(gameObject.transform))
            {
                focusingPointers.Remove(eventData.Pointer);
            }
        }

        public void OnFocusChanged(FocusEventData eventData) {}

        #endregion MixedRealityFocusChangedHandlers

        #region MixedRealityFocusHandlers

        public void OnFocusEnter(FocusEventData eventData)
        {
            Debug.Assert(focusingPointers.Count > 0, "OnFocusEnter called but focusingPointers == 0");
            if (CanInteract())
            {
                SetFocus(true);
            }
        }

        public void OnFocusExit(FocusEventData eventData)
        {
            if (!CanInteract() && !HasFocus)
            {
                return;
            }

            SetFocus(focusingPointers.Count > 0);
        }

        #endregion MixedRealityFocusHandlers

        /// <summary>
        /// Starts a timer to check if input is in progress
        ///  - Make sure global pointer events are not double firing
        ///  - Make sure Global Input events are not double firing
        ///  - Make sure pointer events are not duplicating an input event
        /// </summary>
        /// <param name="isFromInputDown"></param>
        protected void StartClickTimer(bool isFromInputDown = false)
        {
            if (IsGlobal || isFromInputDown)
            {
                if (clickValidTimer != null)
                {
                    StopClickTimer();
                }

                clickValidTimer = StartCoroutine(InputDownTimer(clickTime));
            }
        }

        protected void StopClickTimer()
        {
            Debug.Assert(clickValidTimer != null, "StopClickTimer called but no click timer is running");
            StopCoroutine(clickValidTimer);
            clickValidTimer = null;
        }

        /// <summary>
        /// Return true if the interactable can fire a click event.
        /// Clicks can only occur within a short duration of an input down firing.
        /// </summary>
        /// <returns></returns>
        private bool CanFireClick()
        {
            return clickValidTimer != null;
        }

        #region MixedRealityInputHandlers

        public void OnPositionInputChanged(InputEventData<Vector2> eventData)
        {
            // ignore
        }

        #endregion MixedRealityInputHandlers

        #region DimensionsUtilities

        /// <summary>
        /// A public way to access the current dimension
        /// </summary>
        /// <returns></returns>
        public int GetDimensionIndex()
        {
            return dimensionIndex;
        }

        /// <summary>
        /// a public way to increase a dimension, for cycle button
        /// </summary>
        public void IncreaseDimension()
        {
            IncreaseDimensionIndex();
        }

        /// <summary>
        /// a public way to decrease the dimension
        /// </summary>
        public void DecreaseDimension()
        {
            int index = dimensionIndex;
            if (index > 0)
            {
                index--;
            }
            else
            {
                index = Dimensions - 1;
            }

            SetDimensionIndex(index);
        }

        /// <summary>
        /// a public way to set the dimension index
        /// </summary>
        /// <param name="index"></param>
        public void SetDimensionIndex(int index)
        {
            int currentIndex = dimensionIndex;
            if (index < Dimensions)
            {
                dimensionIndex = index;

                if (currentIndex != dimensionIndex)
                {
                    FilterThemesByDimensions();
                    forceUpdate = true;
                }
            }
        }

        /// <summary>
        /// internal dimension cycling
        /// </summary>
        protected void IncreaseDimensionIndex()
        {
            int currentIndex = dimensionIndex;

            if (dimensionIndex < Dimensions - 1)
            {
                dimensionIndex++;
            }
            else
            {
                dimensionIndex = 0;
            }

            if (currentIndex != dimensionIndex)
            {
                FilterThemesByDimensions();
                forceUpdate = true;
            }
        }

        #endregion DimensionsUtilities

        #region InteractableUtilities

        /// <summary>
        /// Assigns the InputAction based on the InputActionId
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public static MixedRealityInputAction ResolveInputAction(int index)
        {
            MixedRealityInputAction[] actions = InputSystem.InputSystemProfile.InputActionsProfile.InputActions;
            index = Mathf.Clamp(index, 0, actions.Length - 1);
            return actions[index];
        }

        /// <summary>
        /// Get the themes based on the current dimesionIndex
        /// </summary>
        protected void FilterThemesByDimensions()
        {
            runningThemesList = new List<InteractableThemeBase>();

            for (int i = 0; i < runningProfileSettings.Count; i++)
            {
                ProfileSettings settings = runningProfileSettings[i];
                ThemeSettings themeSettings = settings.ThemeSettings[dimensionIndex];
                for (int j = 0; j < themeSettings.Settings.Count; j++)
                {
                    runningThemesList.Add(themeSettings.Settings[j].Theme);
                }
            }
        }

        /// <summary>
        /// Based on inputAction and state, should interactable listen to this up/down event.
        /// </summary>
        /// <param name="action"></param>
        /// <returns></returns>
        protected virtual bool ShouldListenToUpDownEvent(InputEventData data)
        {
            if (!(HasFocus || IsGlobal))
            {
                return false;
            }

            if (data.MixedRealityInputAction != InputAction)
            {
                return false;
            }

            // Special case: Make sure that we are not being focused only by a PokePointer, since PokePointer
            // dispatches touch events and should not be dispatching button presses like select, grip, menu, etc.
            int focusingPointerCount = 0;
            int focusingPokePointerCount = 0;
            for (int i = 0; i < focusingPointers.Count; i++)
            {
                if(focusingPointers[i].InputSourceParent.SourceId == data.SourceId)
                {
                    focusingPointerCount++;
                    if (focusingPointers[i] is PokePointer)
                    {
                        focusingPokePointerCount++;
                    }
                }
            }

            if (focusingPointerCount > 0 && focusingPointerCount == focusingPokePointerCount)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns true if the inputeventdata is being dispatched from a near pointer
        /// </summary>
        /// <param name="eventData"></param>
        /// <returns></returns>
        private bool IsInputFromNearInteraction(InputEventData eventData)
        {
            bool isAnyNearpointerFocusing = false;
            for (int i = 0; i < focusingPointers.Count; i++)
            {
                if (focusingPointers[i].InputSourceParent.SourceId == eventData.InputSource.SourceId && focusingPointers[i] is IMixedRealityNearPointer)
                {
                    isAnyNearpointerFocusing = true;
                    break;
                }
            }
            return isAnyNearpointerFocusing;
        }

        /// <summary>
        /// Based on button settings and state, should this button listen to input?
        /// </summary>
        /// <returns></returns>
        protected virtual bool CanInteract()
        {
            if (!Enabled)
            {
                return false;
            }

            if (Dimensions > 1 && ((dimensionIndex != Dimensions - 1 & !CanSelect) || (dimensionIndex == Dimensions - 1 & !CanDeselect)))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// A public way to trigger or route an onClick event from an external source, like PressableButton
        /// </summary>
        public void TriggerOnClick()
        {
            IncreaseDimensionIndex();
            SendOnClick(null);
            SetVisited(true);
        }

        /// <summary>
        /// call onClick methods on receivers or IInteractableHandlers
        /// </summary>
        protected void SendOnClick(IMixedRealityPointer pointer)
        {
            OnClick.Invoke();
            clickCount++;

            for (int i = 0; i < Events.Count; i++)
            {
                if (Events[i].Receiver != null)
                {
                    Events[i].Receiver.OnClick(StateManager, this, pointer);
                }
            }

            for (int i = 0; i < handlers.Count; i++)
            {
                if (handlers[i] != null)
                {
                    handlers[i].OnClick(StateManager, this, pointer);
                }
            }
        }

        /// <summary>
        /// sets some visual states for automating button events like clicks from a keyword
        /// </summary>
        /// <param name="voiceCommand"></param>
        protected void StartGlobalVisual(bool voiceCommand = false)
        {
            if (voiceCommand)
            {
                StateManager.SetStateValue(InteractableStates.InteractableStateEnum.VoiceCommand, 1);
            }

            SetVisited(true);
            StateManager.SetStateValue(InteractableStates.InteractableStateEnum.Focus, 1);
            StateManager.SetStateValue(InteractableStates.InteractableStateEnum.Pressed, 1);
            UpdateState();

            if (globalTimer != null)
            {
                StopCoroutine(globalTimer);
            }

            globalTimer = StartCoroutine(GlobalVisualReset(clickTime));
        }

        /// <summary>
        /// Clears up any automated visual states
        /// </summary>
        /// <param name="time"></param>
        /// <returns></returns>
        protected IEnumerator GlobalVisualReset(float time)
        {
            yield return new WaitForSeconds(time);

            StateManager.SetStateValue(InteractableStates.InteractableStateEnum.VoiceCommand, 0);
            if (!HasFocus)
            {
                StateManager.SetStateValue(InteractableStates.InteractableStateEnum.Focus, 0);
            }

            if (!HasPress)
            {
                StateManager.SetStateValue(InteractableStates.InteractableStateEnum.Pressed, 0);
            }

            UpdateState();

            globalTimer = null;
        }

        /// <summary>
        /// A timer for the MixedRealityInputHandlers, clicks should occur within a certain time.
        /// </summary>
        /// <param name="time"></param>
        /// <returns></returns>
        protected IEnumerator InputDownTimer(float time)
        {
            yield return new WaitForSeconds(time);
            clickValidTimer = null;
        }

        #endregion InteractableUtilities

        #region VoiceCommands

        /// <summary>
        /// Voice commands from MixedRealitySpeechCommandProfile, keyword recognized
        /// requires isGlobal
        /// </summary>
        /// <param name="eventData"></param>
        public void OnSpeechKeywordRecognized(SpeechEventData eventData)
        {
            if (eventData.Command.Keyword == VoiceCommand && (!RequiresFocus || HasFocus) && Enabled)
            {
                StartGlobalVisual(true);
                SetVoiceCommand(true);
                SendVoiceCommands(VoiceCommand, 0, 1);
                TriggerOnClick();
                eventData.Use();
            }
        }

        /// <summary>
        /// call OnVoinceCommand methods on receivers or IInteractableHandlers
        /// </summary>
        protected void SendVoiceCommands(string command, int index, int length)
        {
            for (int i = 0; i < Events.Count; i++)
            {
                if (Events[i].Receiver != null)
                {
                    Events[i].Receiver.OnVoiceCommand(StateManager, this, command, index, length);
                }
            }

            for (int i = 0; i < handlers.Count; i++)
            {
                if (handlers[i] != null)
                {
                    handlers[i].OnVoiceCommand(StateManager, this, command, index, length);
                }
            }
        }

        /// <summary>
        /// checks the voiceCommand array for a keyword and returns it's index
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        protected int GetVoiceCommandIndex(string command)
        {
            if (voiceCommands.Length > 1)
            {
                for (int i = 0; i < voiceCommands.Length; i++)
                {
                    if (command == voiceCommands[i])
                    {
                        return i;
                    }
                }
            }

            return 0;
        }

        #endregion VoiceCommands

        #region TouchHandlers

        public void OnTouchStarted(HandTrackingInputEventData eventData)
        {
            SetPress(true);
            SetPhysicalTouch(true);
            eventData.Use();
        }

        public void OnTouchCompleted(HandTrackingInputEventData eventData)
        {
            SetPress(false);
            SetPhysicalTouch(false);
            eventData.Use();
        }

        public void OnTouchUpdated(HandTrackingInputEventData eventData){}
        #endregion TouchHandlers

        #region InputHandlers
        public void OnInputUp(InputEventData eventData)
        {
            if ((!CanInteract() && !HasPress))
            {
                return;
            }

            if (ShouldListenToUpDownEvent(eventData))
            {
                SetInputUp();
                if (IsInputFromNearInteraction(eventData))
                {
                    // TODO:what if we have two hands grabbing?
                    SetGrab(false);
                }

                eventData.Use();
            }
            pressingInputSources.Remove(eventData.InputSource);
        }

        public void OnInputDown(InputEventData eventData)
        {
            if (!CanInteract())
            {
                return;
            }

            if (ShouldListenToUpDownEvent(eventData))
            {
                pressingInputSources.Add(eventData.InputSource);
                SetInputDown();
                SetGrab(IsInputFromNearInteraction(eventData));

                eventData.Use();
            }
        }

        /// <summary>
        /// Public method that can be used to set state of interactable
        /// corresponding to an input going down (select button, menu button, touch) 
        /// </summary>
        public void SetInputDown()
        {
            if (!CanInteract())
            {
                return;
            }
            dragStartPosition = null;

            SetPress(true);
            StartClickTimer(true);
        }

        /// <summary>
        /// Public method that can be used to set state of interactable
        /// corresponding to an input going up.
        /// </summary>
        public void SetInputUp()
        {
            if (!CanInteract())
            {
                return;
            }

            SetPress(false);
            SetGesture(false);

            if (CanFireClick())
            {
                StopClickTimer();

                TriggerOnClick();
                SetVisited(true);
            }
        }

        private void OnInputChangedHelper<T>(InputEventData<T> eventData, Vector3 inputPosition, float gestureDeadzoneThreshold)
        {
            if (!CanInteract())
            {
                return;
            }

            if (ShouldListenToMoveEvent(eventData))
            {
                if (dragStartPosition == null)
                {
                    dragStartPosition = inputPosition;
                }
                else if (!HasGesture)
                {
                    if (Vector3.Distance(dragStartPosition.Value, inputPosition) > gestureStartThresholdVector2)
                    {
                        SetGesture(true);
                    }
                }
            }
        }

        public void OnInputChanged(InputEventData<Vector2> eventData)
        {
            OnInputChangedHelper(eventData, eventData.InputData, gestureStartThresholdVector2);
        }


        public void OnInputChanged(InputEventData<Vector3> eventData)
        {
            OnInputChangedHelper(eventData, eventData.InputData, gestureStartThresholdVector3);
        }

        public void OnInputChanged(InputEventData<MixedRealityPose> eventData)
        {
            OnInputChangedHelper(eventData, eventData.InputData.Position, gestureStartThresholdMixedRealityPose);
        }

        private bool ShouldListenToMoveEvent<T>(InputEventData<T> eventData)
        {
            if (!(HasFocus || IsGlobal))
            {
                return false;
            }

            if (!HasPress)
            {
                return false;
            }

            // Ensure that this move event is from a pointer that is pressing the interactable
            int matchingPointerCount = 0;
            foreach (var pressingInputSource in pressingInputSources)
            {
                if (pressingInputSource == eventData.InputSource)
                {
                    matchingPointerCount++;
                }
            }

            return matchingPointerCount > 0;
        }
        #endregion InputHandlers
    }
}
