using Godot;
using System.Collections.Generic;
using SplineSculptor.Interaction;
using SplineSculptor.Rendering;

namespace SplineSculptor.VR
{
    /// <summary>
    /// Attached to each XRController3D. Manages the grab state machine for one hand.
    ///
    ///  States: Idle → GrabbingTarget | GrabbingWorld
    ///          GrabbingTarget → Idle | TwoHandObject
    ///          GrabbingWorld  → Idle | TwoHandWorld
    /// </summary>
    [GlobalClass]
    public partial class ControllerHand : Node3D
    {
        [Export] public string GripAction    { get; set; } = "grip";
        [Export] public string TriggerAction { get; set; } = "trigger";
        [Export] public string PrimaryAction { get; set; } = "primary";
        [Export] public string MenuAction    { get; set; } = "menu";

        // Hover detection sphere radius (world units)
        [Export] public float HoverRadius { get; set; } = 0.08f;

        // Reference to the other hand (set by WorldNavigator or VRManager)
        public ControllerHand? OtherHand { get; set; }

        private enum HandState { Idle, GrabbingTarget, GrabbingWorld, TwoHandObject, TwoHandWorld }
        private HandState _state = HandState.Idle;

        private IGrabTarget? _currentTarget;
        private IGrabTarget? _hoveredTarget;

        private XRController3D? _controller;
        private Node3D?         _sceneRoot;

        public override void _Ready()
        {
            _controller = GetParent<XRController3D>();
            // SceneRoot is set externally by VRManager after ready
        }

        public void SetSceneRoot(Node3D root) => _sceneRoot = root;

        public override void _PhysicsProcess(double delta)
        {
            UpdateHover();
            HandleInput();
        }

        // ─── Hover detection ──────────────────────────────────────────────────────

        private void UpdateHover()
        {
            if (_sceneRoot == null) return;

            var controllerPos = GlobalPosition;
            IGrabTarget? closest = null;
            float closestDist = HoverRadius;

            // Collect all handles from all PolysurfaceNodes in the scene root
            foreach (var child in _sceneRoot.GetChildren())
            {
                if (child is not PolysurfaceNode polyNode) continue;
                foreach (var handle in polyNode.AllHandles())
                {
                    float dist = controllerPos.DistanceTo(handle.GlobalGrabPosition);
                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closest = handle;
                    }
                }
            }

            // Update hover state
            if (_hoveredTarget != closest)
            {
                if (_hoveredTarget != null) _hoveredTarget.IsHovered = false;
                _hoveredTarget = closest;
                if (_hoveredTarget != null) _hoveredTarget.IsHovered = true;
            }
        }

        // ─── Input / state machine ────────────────────────────────────────────────

        private void HandleInput()
        {
            if (_controller == null) return;

            bool gripPressed = _controller.IsButtonPressed(JoyButton.LeftShoulder);
            // In OpenXR action map the grip is mapped; use the abstract button for now
            // (actual binding is in the action map asset)

            switch (_state)
            {
                case HandState.Idle:
                    if (gripPressed)
                    {
                        if (_hoveredTarget != null)
                            EnterGrabbingTarget();
                        else
                            EnterGrabbingWorld();
                    }
                    break;

                case HandState.GrabbingTarget:
                    if (!gripPressed)
                    {
                        ExitGrabbingTarget();
                        _state = HandState.Idle;
                    }
                    else
                    {
                        _currentTarget?.OnGrabMove(GlobalPosition);
                    }
                    break;

                case HandState.GrabbingWorld:
                    if (!gripPressed)
                        _state = HandState.Idle;
                    break;
            }
        }

        private void EnterGrabbingTarget()
        {
            _currentTarget = _hoveredTarget;
            _currentTarget?.OnGrabStart(this);
            _state = HandState.GrabbingTarget;
        }

        private void ExitGrabbingTarget()
        {
            _currentTarget?.OnGrabEnd(this);
            _currentTarget = null;
        }

        private void EnterGrabbingWorld()
        {
            _state = HandState.GrabbingWorld;
            // WorldNavigator handles the actual scene movement
        }

        // ─── Menu button → undo ───────────────────────────────────────────────────

        private bool _menuWasDown = false;

        private void CheckMenuButton()
        {
            if (_controller == null) return;
            bool menuDown = _controller.IsButtonPressed(JoyButton.Start);
            if (menuDown && !_menuWasDown)
            {
                // Undo on menu press
                SplineSculptor.Interaction.ControlPointHandle.SceneRef?.UndoStack.Undo();
            }
            _menuWasDown = menuDown;
        }
    }
}
