using System;
using Godot;
using SplineSculptor.Interaction;
using SplineSculptor.Model;
using SplineSculptor.Rendering;

namespace SplineSculptor.VR
{
    /// <summary>
    /// Attached as a child of each XRController3D.
    ///
    /// RIGHT hand (IsLeft = false):
    ///   Trigger         — grab the nearest control-point handle
    ///   Grip            — world-grab (WorldNavigator handles two-grip pan/scale/rotate)
    ///   Trackpad touch  — open tool-select radial menu (finger on pad)
    ///   Trackpad click  — activate highlighted menu item
    ///                     Up=Auto  Right=Point  Down=Edge  Left=Surface
    ///   Menu button     — Redo
    ///
    /// LEFT hand (IsLeft = true):
    ///   Trigger         — Undo
    ///   Grip            — world-grab
    ///   Trackpad touch  — open operations radial menu
    ///   Trackpad click  — activate highlighted item
    ///                     Up=Attach Patch  Right=Toggle G1  Down=Delete  Left=Save
    ///   Menu button     — Undo
    ///
    /// Operations are injected as Actions by VRManager so this class stays decoupled.
    /// </summary>
    [GlobalClass]
    public partial class ControllerHand : Node3D
    {
        // ─── Configuration ────────────────────────────────────────────────────────

        [Export] public bool   IsLeft          { get; set; } = false;
        [Export] public string TriggerAction   { get; set; } = "trigger";
        [Export] public string GripAction      { get; set; } = "grip";
        [Export] public string PrimaryAction   { get; set; } = "primary";      // trackpad click (bool)
        [Export] public string Primary2DAction { get; set; } = "primary_2d";  // trackpad position (Vector2)
        [Export] public string TouchAction     { get; set; } = "primary_touch"; // trackpad touch (bool)
        [Export] public string MenuAction      { get; set; } = "menu";
        [Export] public float  HoverRadius     { get; set; } = 0.08f;

        // ─── Wired in by VRManager ────────────────────────────────────────────────

        public ControllerHand? OtherHand  { get; set; }
        public Node3D?          SceneRoot  { get; set; }
        /// <summary>
        /// When the right hand releases a grabbed handle its parent surface is auto-selected here,
        /// so that left-hand menu operations (Attach, Delete, ToggleG1) have a target.
        /// </summary>
        public SelectionManager? Selection  { get; set; }

        /// <summary>Operations injected by VRManager. ControllerHand calls these blindly.</summary>
        public Action? OnUndo        { get; set; }
        public Action? OnRedo        { get; set; }
        public Action? OnSpawnAttach { get; set; }
        public Action? OnToggleG1    { get; set; }
        public Action? OnDelete      { get; set; }
        public Action? OnSave        { get; set; }

        // ─── Shared state (both hands see the same tool) ──────────────────────────

        /// <summary>Currently active selection tool. Updated by right-hand trackpad.</summary>
        public static SelectionTool ActiveTool { get; private set; } = SelectionTool.Auto;

        // ─── Grab state ───────────────────────────────────────────────────────────

        /// <summary>True while this hand is holding a control-point handle.</summary>
        public bool IsGrabbingTarget => _state == HandState.GrabbingTarget;

        // ─── Private ──────────────────────────────────────────────────────────────

        private enum HandState { Idle, GrabbingTarget }
        private HandState _state = HandState.Idle;

        private IGrabTarget?    _hoveredTarget;
        private IGrabTarget?    _currentTarget;
        private XRController3D? _controller;
        private VRRadialMenu?   _radialMenu;
        private MeshInstance3D? _pointerRay;
        private MeshInstance3D? _tipSphere;

        // Edge-detection flags
        private bool _triggerWasDown;
        private bool _menuWasDown;
        private bool _primaryWasDown;
        private bool _menuShowing;

        // ─── Lifecycle ────────────────────────────────────────────────────────────

        public override void _Ready()
        {
            _controller = GetParent<XRController3D>();
            BuildControllerVisuals();
            BuildRadialMenu();
        }

        private void BuildControllerVisuals()
        {
            // Controller body — a small dark box representative of a handheld wand
            var bodyMat = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.14f, 0.14f, 0.14f),
                Roughness   = 0.75f,
                Metallic    = 0.25f,
            };
            var body = new MeshInstance3D
            {
                Mesh             = new BoxMesh { Size = new Vector3(0.030f, 0.022f, 0.120f) },
                MaterialOverride = bodyMat,
                // Slightly behind the tracker origin (grip point is ~3 cm from tip)
                Position         = new Vector3(0f, -0.010f, -0.025f),
            };
            AddChild(body);

            // Pointer tip — small translucent sphere used as the interaction origin.
            // Hover detection and grab moves use this position rather than the controller
            // origin, giving a natural "point at it" feel.
            var tipMat = new StandardMaterial3D
            {
                AlbedoColor     = new Color(0.70f, 0.90f, 1.00f, 0.50f),
                EmissionEnabled = true,
                Emission        = new Color(0.30f, 0.60f, 1.00f, 0.25f),
                Transparency    = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode     = BaseMaterial3D.ShadingModeEnum.Unshaded,
                NoDepthTest     = true,
            };
            _tipSphere = new MeshInstance3D
            {
                Mesh             = new SphereMesh { Radius = 0.005f, Height = 0.010f, RadialSegments = 8, Rings = 4 },
                MaterialOverride = tipMat,
                // 10 cm forward from the controller origin along -Z
                Position         = new Vector3(0f, 0f, -0.10f),
            };
            AddChild(_tipSphere);

            // Pointer ray — right hand only, 80 cm thin blue-white line
            if (!IsLeft)
            {
                var rayMat = new StandardMaterial3D
                {
                    AlbedoColor     = new Color(0.75f, 0.85f, 1.00f, 0.55f),
                    EmissionEnabled = true,
                    Emission        = new Color(0.40f, 0.55f, 1.00f, 0.30f),
                    Transparency    = BaseMaterial3D.TransparencyEnum.Alpha,
                    ShadingMode     = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    NoDepthTest     = true,
                };
                _pointerRay = new MeshInstance3D
                {
                    Mesh             = new CylinderMesh
                    {
                        TopRadius      = 0.0018f,
                        BottomRadius   = 0.0018f,
                        Height         = 0.80f,
                        RadialSegments = 6,
                    },
                    MaterialOverride = rayMat,
                    // CylinderMesh is along Y; rotate 90° so it points along -Z (controller forward)
                    Rotation         = new Vector3(Mathf.Pi / 2f, 0f, 0f),
                    // Centre of the cylinder sits 0.4 m in front of the controller origin
                    Position         = new Vector3(0f, 0f, -(0.40f + 0.06f)),
                };
                AddChild(_pointerRay);
            }
        }

        private void BuildRadialMenu()
        {
            _radialMenu = new VRRadialMenu
            {
                // Slightly in front of and above the controller (palm-side when held naturally).
                // Rotated 60° around X so the disc face
                // ends up angled away from the player rather than seen edge-on.
                Position = new Vector3(0f, 0.045f, -0.04f),
                Rotation = new Vector3(Mathf.DegToRad(-60f), 0f, 0f),
            };

            // AddChild first — VRRadialMenu._Ready() initialises _labels, which SetOptions needs.
            AddChild(_radialMenu);

            if (IsLeft)
                _radialMenu.SetOptions("Attach Patch", "Toggle G1", "Delete", "Save");
            else
                _radialMenu.SetOptions("Auto", "Point", "Edge", "Surface");
        }

        // ─── Per-frame ────────────────────────────────────────────────────────────

        public override void _PhysicsProcess(double delta)
        {
            UpdateHover();
            HandleInput();
        }

        // ─── Hover detection ──────────────────────────────────────────────────────

        private void UpdateHover()
        {
            if (SceneRoot == null) return;

            // Use the tip sphere position as the interaction origin so that
            // the user can aim precisely rather than relying on the controller body.
            var pos = _tipSphere != null ? _tipSphere.GlobalPosition : GlobalPosition;

            IGrabTarget? closest    = null;
            float        closestDist = HoverRadius;

            foreach (var child in SceneRoot.GetChildren())
            {
                if (child is not PolysurfaceNode polyNode) continue;
                foreach (var handle in polyNode.AllHandles())
                {
                    float d = pos.DistanceTo(handle.GlobalGrabPosition);
                    if (d < closestDist)
                    {
                        closestDist = d;
                        closest     = handle;
                    }
                }
            }

            if (_hoveredTarget != closest)
            {
                if (_hoveredTarget != null) _hoveredTarget.IsHovered = false;
                _hoveredTarget = closest;
                if (_hoveredTarget != null) _hoveredTarget.IsHovered = true;
            }

            // Dim the pointer ray when a handle is close enough to grab
            if (_pointerRay != null)
                _pointerRay.Visible = (_hoveredTarget == null && _state == HandState.Idle);
        }

        // ─── Input dispatch ───────────────────────────────────────────────────────

        private void HandleInput()
        {
            if (_controller == null) return;

            bool trigger = _controller.IsButtonPressed(TriggerAction);
            bool menu    = _controller.IsButtonPressed(MenuAction);
            bool primary = _controller.IsButtonPressed(PrimaryAction);
            bool touch   = _controller.IsButtonPressed(TouchAction);
            var  padVec  = _controller.GetVector2(Primary2DAction);

            HandleTrigger(trigger);
            HandleMenuButton(menu);
            HandlePrimaryPad(touch, primary, padVec);
        }

        // ─── Trigger ──────────────────────────────────────────────────────────────

        private void HandleTrigger(bool triggerDown)
        {
            if (IsLeft)
            {
                // Left trigger rising edge → undo
                if (triggerDown && !_triggerWasDown)
                    OnUndo?.Invoke();
                _triggerWasDown = triggerDown;
                return;
            }

            // Right trigger → grab / release control point
            var tipPos = _tipSphere != null ? _tipSphere.GlobalPosition : GlobalPosition;

            switch (_state)
            {
                case HandState.Idle:
                    if (triggerDown && !_triggerWasDown && _hoveredTarget != null)
                    {
                        _currentTarget = _hoveredTarget;
                        _currentTarget.OnGrabStart(this);
                        _state = HandState.GrabbingTarget;
                    }
                    break;

                case HandState.GrabbingTarget:
                    if (triggerDown)
                        _currentTarget?.OnGrabMove(tipPos);
                    else
                    {
                        _currentTarget?.OnGrabEnd(this);
                        // Auto-select this surface so left-hand menu operations have a target
                        if (_currentTarget is ControlPointHandle cph && Selection != null)
                        {
                            if (cph.Surface != null)
                                Selection.SelectSurface(cph.Surface);
                            if (cph.OwnerPoly != null)
                                Selection.SelectPolysurface(cph.OwnerPoly);
                        }
                        _currentTarget = null;
                        _state         = HandState.Idle;
                    }
                    break;
            }

            _triggerWasDown = triggerDown;
        }

        // ─── Menu button ──────────────────────────────────────────────────────────

        private void HandleMenuButton(bool menuDown)
        {
            if (menuDown && !_menuWasDown)
            {
                if (IsLeft)
                    OnUndo?.Invoke();
                else
                    OnRedo?.Invoke();
            }
            _menuWasDown = menuDown;
        }

        // ─── Trackpad radial menu ─────────────────────────────────────────────────

        private void HandlePrimaryPad(bool touchDown, bool primaryDown, Vector2 padVec)
        {
            // Show menu when finger touches the pad (even before clicking).
            // Fall back to showing on click-down if touch action isn't mapped.
            bool shouldShow = touchDown || primaryDown;

            if (shouldShow && !_menuShowing)
            {
                _menuShowing = true;
                if (_radialMenu != null)
                {
                    _radialMenu.ResetHighlight();
                    _radialMenu.Visible = true;
                }
            }
            else if (!shouldShow && _menuShowing)
            {
                // Finger lifted without clicking — dismiss without executing
                _menuShowing = false;
                if (_radialMenu != null)
                    _radialMenu.Visible = false;
            }

            // Update highlight while menu is showing
            if (_menuShowing && _radialMenu != null)
                _radialMenu.UpdateHighlight(GetSector(padVec));

            // Click falling edge while menu is showing → execute selected sector
            if (_menuShowing && !primaryDown && _primaryWasDown)
            {
                int sector = GetSector(padVec);
                if (sector >= 0)
                    ExecuteSector(sector);
                // Menu stays open until finger leaves (hide handled by !shouldShow above)
            }

            _primaryWasDown = primaryDown;
        }

        /// <summary>
        /// Map a trackpad/joystick Vector2 to a sector index.
        /// Returns -1 when the stick is close to centre (cancel).
        /// 0=Up  1=Right  2=Down  3=Left
        /// </summary>
        private static int GetSector(Vector2 v)
        {
            if (v.Length() < 0.40f) return -1;
            return Mathf.Abs(v.Y) >= Mathf.Abs(v.X)
                ? (v.Y > 0 ? 0 : 2)
                : (v.X > 0 ? 1 : 3);
        }

        private void ExecuteSector(int sector)
        {
            if (IsLeft)
            {
                switch (sector)
                {
                    case 0: OnSpawnAttach?.Invoke(); break;   // Up    = Attach / Spawn Patch
                    case 1: OnToggleG1?.Invoke();    break;   // Right = Toggle G1
                    case 2: OnDelete?.Invoke();      break;   // Down  = Delete surface
                    case 3: OnSave?.Invoke();        break;   // Left  = Save scene
                }
            }
            else
            {
                // Right hand: tool switch
                ActiveTool = sector switch
                {
                    0 => SelectionTool.Auto,
                    1 => SelectionTool.Point,
                    2 => SelectionTool.Edge,
                    3 => SelectionTool.Surface,
                    _ => ActiveTool,
                };
                GD.Print($"[VR Tool] {ActiveTool}");
            }
        }
    }
}
