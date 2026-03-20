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
    ///   Trigger         — action depends on ActiveTool:
    ///                       Auto / Point  → grab nearest control-point handle
    ///                       Edge          → select the nearest hovered edge
    ///                       Surface       → select the nearest surface
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
        public SelectionManager? Selection  { get; set; }

        public Action? OnUndo        { get; set; }
        public Action? OnRedo        { get; set; }
        public Action? OnSpawnAttach { get; set; }
        public Action? OnToggleG1    { get; set; }
        public Action? OnDelete      { get; set; }
        public Action? OnSave        { get; set; }

        // ─── Shared state (both hands see the same tool) ──────────────────────────

        public static SelectionTool ActiveTool { get; private set; } = SelectionTool.Auto;

        // ─── Grab state ───────────────────────────────────────────────────────────

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

        // Edge hover / selection state
        private SurfaceNode? _hoveredEdgeSurfNode;
        private SurfaceEdge  _hoveredEdge;
        private SurfaceNode? _selectedEdgeSurfNode;  // tracks visual so we can clear it
        private SurfaceEdge  _selectedEdgeValue;

        private static readonly SurfaceEdge[] AllEdges =
            { SurfaceEdge.UMin, SurfaceEdge.UMax, SurfaceEdge.VMin, SurfaceEdge.VMax };

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
                Position         = new Vector3(0f, -0.010f, -0.025f),
            };
            AddChild(body);

            // Pointer tip sphere — interaction origin (10 cm forward from controller)
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
                Position         = new Vector3(0f, 0f, -0.10f),
            };
            AddChild(_tipSphere);

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
                    Rotation         = new Vector3(Mathf.Pi / 2f, 0f, 0f),
                    Position         = new Vector3(0f, 0f, -(0.40f + 0.06f)),
                };
                AddChild(_pointerRay);
            }
        }

        private void BuildRadialMenu()
        {
            _radialMenu = new VRRadialMenu
            {
                Position = new Vector3(0f, 0.045f, -0.04f),
                Rotation = new Vector3(Mathf.DegToRad(-60f), 0f, 0f),
            };
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

            var pos = _tipSphere != null ? _tipSphere.GlobalPosition : GlobalPosition;

            bool wantHandles  = ActiveTool is SelectionTool.Auto or SelectionTool.Point;
            bool wantEdges    = ActiveTool == SelectionTool.Edge;

            // ── Control-point hover (Point / Auto) ────────────────────────────────
            IGrabTarget? closest    = null;
            float        closestDist = HoverRadius;

            if (wantHandles)
            {
                foreach (var child in SceneRoot.GetChildren())
                {
                    if (child is not PolysurfaceNode polyNode) continue;
                    foreach (var handle in polyNode.AllHandles())
                    {
                        float d = pos.DistanceTo(handle.GlobalGrabPosition);
                        if (d < closestDist) { closestDist = d; closest = handle; }
                    }
                }
            }

            if (_hoveredTarget != closest)
            {
                if (_hoveredTarget != null) _hoveredTarget.IsHovered = false;
                _hoveredTarget = closest;
                if (_hoveredTarget != null) _hoveredTarget.IsHovered = true;
            }

            // ── Edge hover (Edge mode) ────────────────────────────────────────────
            SurfaceNode? edgeNode = null;
            SurfaceEdge  edgeEdge = SurfaceEdge.UMin;
            float        edgeDist = HoverRadius;

            if (wantEdges)
            {
                foreach (var child in SceneRoot.GetChildren())
                {
                    if (child is not PolysurfaceNode polyNode) continue;
                    foreach (var surfNode in polyNode.AllSurfaceNodes())
                    {
                        foreach (var edge in AllEdges)
                        {
                            int count = surfNode.GetEdgePointCount(edge);
                            for (int i = 0; i < count - 1; i++)
                            {
                                var a = surfNode.GetEdgeWorldPoint(edge, i);
                                var b = surfNode.GetEdgeWorldPoint(edge, i + 1);
                                float d = PointSegmentDist(pos, a, b);
                                if (d < edgeDist) { edgeDist = d; edgeNode = surfNode; edgeEdge = edge; }
                            }
                        }
                    }
                }
            }

            bool edgeChanged = edgeNode != _hoveredEdgeSurfNode ||
                               (edgeNode != null && edgeEdge != _hoveredEdge);
            if (edgeChanged)
            {
                _hoveredEdgeSurfNode?.SetHoveredEdge(null);
                _hoveredEdgeSurfNode = edgeNode;
                _hoveredEdge         = edgeEdge;
                _hoveredEdgeSurfNode?.SetHoveredEdge(edgeEdge);
            }

            // Dim the pointer ray when something is in range
            if (_pointerRay != null)
                _pointerRay.Visible = (_hoveredTarget == null &&
                                       _hoveredEdgeSurfNode == null &&
                                       _state == HandState.Idle);
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
                if (triggerDown && !_triggerWasDown) OnUndo?.Invoke();
                _triggerWasDown = triggerDown;
                return;
            }

            var tipPos = _tipSphere != null ? _tipSphere.GlobalPosition : GlobalPosition;

            switch (_state)
            {
                case HandState.Idle:
                    if (triggerDown && !_triggerWasDown)
                    {
                        if (ActiveTool is SelectionTool.Auto or SelectionTool.Point)
                        {
                            if (_hoveredTarget != null)
                            {
                                _currentTarget = _hoveredTarget;
                                _currentTarget.OnGrabStart(this);
                                _state = HandState.GrabbingTarget;
                            }
                        }
                        else if (ActiveTool == SelectionTool.Edge)
                        {
                            SelectHoveredEdge();
                        }
                        else if (ActiveTool == SelectionTool.Surface)
                        {
                            SelectNearestSurface(tipPos);
                        }
                    }
                    break;

                case HandState.GrabbingTarget:
                    if (triggerDown)
                        _currentTarget?.OnGrabMove(tipPos);
                    else
                    {
                        _currentTarget?.OnGrabEnd(this);
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

        // ─── Edge selection ───────────────────────────────────────────────────────

        private void SelectHoveredEdge()
        {
            if (_hoveredEdgeSurfNode == null) return;
            var polyNode = _hoveredEdgeSurfNode.GetParent() as PolysurfaceNode;
            if (polyNode?.Data == null || _hoveredEdgeSurfNode.Surface == null) return;

            // Clear previous VR-selected edge visual
            _selectedEdgeSurfNode?.SetSelectedEdge(null);
            _selectedEdgeSurfNode = _hoveredEdgeSurfNode;
            _selectedEdgeValue    = _hoveredEdge;
            _selectedEdgeSurfNode.SetSelectedEdge(_hoveredEdge);

            var er = new EdgeRef(_hoveredEdgeSurfNode.Surface, polyNode.Data, _hoveredEdge);
            Selection?.ModifyEdgeSelection(er, SelectionModifier.Replace);
            Selection?.SelectSurface(_hoveredEdgeSurfNode.Surface);
            Selection?.SelectPolysurface(polyNode.Data);
            GD.Print($"[VR Select] Edge {_hoveredEdge} on '{polyNode.Data.Name}'");
        }

        // ─── Surface selection ────────────────────────────────────────────────────

        private void SelectNearestSurface(Vector3 pos)
        {
            if (SceneRoot == null) return;

            SurfaceNode?     bestSurf = null;
            PolysurfaceNode? bestPoly = null;
            float bestDist = HoverRadius;

            foreach (var child in SceneRoot.GetChildren())
            {
                if (child is not PolysurfaceNode polyNode) continue;
                foreach (var surfNode in polyNode.AllSurfaceNodes())
                {
                    // Use edge boundary points as a proximity proxy for the surface
                    foreach (var edge in AllEdges)
                    {
                        int count = surfNode.GetEdgePointCount(edge);
                        for (int i = 0; i < count; i++)
                        {
                            float d = pos.DistanceTo(surfNode.GetEdgeWorldPoint(edge, i));
                            if (d < bestDist) { bestDist = d; bestSurf = surfNode; bestPoly = polyNode; }
                        }
                    }
                }
            }

            if (bestSurf?.Surface == null || bestPoly?.Data == null) return;

            Selection?.SelectSurface(bestSurf.Surface);
            Selection?.SelectPolysurface(bestPoly.Data);
            GD.Print($"[VR Select] Surface in '{bestPoly.Data.Name}'");
        }

        // ─── Menu button ──────────────────────────────────────────────────────────

        private void HandleMenuButton(bool menuDown)
        {
            if (menuDown && !_menuWasDown)
            {
                if (IsLeft) OnUndo?.Invoke();
                else        OnRedo?.Invoke();
            }
            _menuWasDown = menuDown;
        }

        // ─── Trackpad radial menu ─────────────────────────────────────────────────

        private void HandlePrimaryPad(bool touchDown, bool primaryDown, Vector2 padVec)
        {
            bool shouldShow = touchDown || primaryDown;

            if (shouldShow && !_menuShowing)
            {
                _menuShowing = true;
                if (_radialMenu != null) { _radialMenu.ResetHighlight(); _radialMenu.Visible = true; }
            }
            else if (!shouldShow && _menuShowing)
            {
                _menuShowing = false;
                if (_radialMenu != null) _radialMenu.Visible = false;
            }

            if (_menuShowing && _radialMenu != null)
                _radialMenu.UpdateHighlight(GetSector(padVec));

            if (_menuShowing && !primaryDown && _primaryWasDown)
            {
                int sector = GetSector(padVec);
                if (sector >= 0) ExecuteSector(sector);
            }

            _primaryWasDown = primaryDown;
        }

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
                    case 0: OnSpawnAttach?.Invoke(); break;
                    case 1: OnToggleG1?.Invoke();    break;
                    case 2: OnDelete?.Invoke();      break;
                    case 3: OnSave?.Invoke();        break;
                }
            }
            else
            {
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

        // ─── Point-to-segment distance ────────────────────────────────────────────

        private static float PointSegmentDist(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float len2 = ab.LengthSquared();
            if (len2 < 1e-10f) return p.DistanceTo(a);
            float t = Mathf.Clamp((p - a).Dot(ab) / len2, 0f, 1f);
            return p.DistanceTo(a + t * ab);
        }
    }
}
