using System;
using System.Collections.Generic;
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
    ///   Trigger         — action depends on ActiveTool / ActiveSelectionMode:
    ///                       Paint mode         → paint-select handles near tip
    ///                       Auto / Point       → grab nearest control-point handle
    ///                       Edge               → select the nearest hovered edge
    ///                       Surface            → select the nearest surface
    ///   Grip            — world-grab (WorldNavigator handles two-grip pan/scale/rotate)
    ///   Trackpad touch  — open tool-select radial menu (finger on pad)
    ///   Trackpad click  — activate highlighted menu item
    ///                     Up=Auto  Right=Point  Down=Edge  Left=Surface
    ///   Menu button     — Redo
    ///
    /// LEFT hand (IsLeft = true):
    ///   Trigger         — Undo
    ///   Grip            — world-grab
    ///   Trackpad touch  — open nested ops menu
    ///   Trackpad click  — navigate / activate menu item; on Modifier page: hold to set modifier
    ///   Menu button     — pop menu level (at root → Undo)
    ///
    /// Left-hand menu tree:
    ///   Root:     Select →   Modify →   File →   (empty)
    ///   Select:   Paint       Hull(dim)  (empty)  (empty)
    ///   Modifier: XOR         Add        Replace   Remove  [hold click → modifier active]
    ///   Modify:   Attach      Toggle G1  Delete    (empty)
    ///   File:     Save        (empty)    (empty)   (empty)
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
        [Export] public string PrimaryAction   { get; set; } = "primary";
        [Export] public string Primary2DAction { get; set; } = "primary_2d";
        [Export] public string TouchAction     { get; set; } = "primary_touch";
        [Export] public string MenuAction      { get; set; } = "menu";
        [Export] public float  HoverRadius     { get; set; } = 0.08f;

        // ─── Wired in by VRManager ────────────────────────────────────────────────

        public ControllerHand?  OtherHand  { get; set; }
        public Node3D?           SceneRoot  { get; set; }
        public SelectionManager? Selection  { get; set; }

        public Action? OnUndo        { get; set; }
        public Action? OnRedo        { get; set; }
        public Action? OnSpawnAttach { get; set; }
        public Action? OnToggleG1    { get; set; }
        public Action? OnDelete      { get; set; }
        public Action? OnSave        { get; set; }

        // ─── Shared static state ──────────────────────────────────────────────────

        public static SelectionTool     ActiveTool            { get; private set; } = SelectionTool.Auto;
        public static SelectionMode     ActiveSelectionMode   { get; private set; } = SelectionMode.None;
        public static SelectionModifier ActiveSelectionModifier { get; private set; } = SelectionModifier.Replace;

        public enum SelectionMode { None, Paint, Hull }

        // ─── Grab state ───────────────────────────────────────────────────────────

        public bool IsGrabbingTarget => _state == HandState.GrabbingTarget;

        // ─── Left-hand menu page IDs ──────────────────────────────────────────────

        private enum PageId { Root, Select, Modifier, Modify, File }

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
        private SurfaceNode? _selectedEdgeSurfNode;
        private SurfaceEdge  _selectedEdgeValue;

        private static readonly SurfaceEdge[] AllEdges =
            { SurfaceEdge.UMin, SurfaceEdge.UMax, SurfaceEdge.VMin, SurfaceEdge.VMax };

        // Input edge-detection
        private bool _triggerWasDown;
        private bool _menuWasDown;
        private bool _primaryWasDown;
        private bool _menuShowing;

        // Left-hand menu stack (parallel to VRRadialMenu's page stack)
        private readonly Stack<PageId> _pageIds = new();
        private PageId CurrentPage => _pageIds.Count > 0 ? _pageIds.Peek() : PageId.Root;

        // Paint-select state (right hand)
        private readonly HashSet<ControlPointHandle> _paintedThisDrag = new();
        private bool _paintTriggerHeld = false;

        // Hull-select state (right hand)
        private readonly List<Vector3> _hullPath         = new();
        private bool    _hullTriggerHeld  = false;
        private Vector3 _hullLastPos      = Vector3.Zero;
        private const float HullRecordMinDist = 0.02f; // minimum tip movement (metres) before recording

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
                    Mesh = new CylinderMesh
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
            {
                PushMenuPage(PageId.Root);
            }
            else
            {
                // Right hand: flat single-level tool menu
                _radialMenu.PushPage(new[] { "Auto", "Point", "Edge", "Surface" });
            }
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

            bool wantHandles = ActiveTool is SelectionTool.Auto or SelectionTool.Point
                               || ActiveSelectionMode == SelectionMode.Paint;
            bool wantEdges   = ActiveTool == SelectionTool.Edge
                               && ActiveSelectionMode == SelectionMode.None;

            // ── Control-point hover ───────────────────────────────────────────────
            IGrabTarget? closest     = null;
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

            // ── Edge hover ────────────────────────────────────────────────────────
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

            // Selection modes override normal grab
            if (ActiveSelectionMode == SelectionMode.Paint)
            {
                HandlePaintSelect(triggerDown, tipPos);
                _triggerWasDown = triggerDown;
                return;
            }
            if (ActiveSelectionMode == SelectionMode.Hull)
            {
                HandleHullSelect(triggerDown, tipPos);
                _triggerWasDown = triggerDown;
                return;
            }

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
                            SelectHoveredEdge();
                        else if (ActiveTool == SelectionTool.Surface)
                            SelectNearestSurface(tipPos);
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
                            if (cph.Surface != null)   Selection.SelectSurface(cph.Surface);
                            if (cph.OwnerPoly != null)  Selection.SelectPolysurface(cph.OwnerPoly);
                        }
                        _currentTarget = null;
                        _state         = HandState.Idle;
                    }
                    break;
            }

            _triggerWasDown = triggerDown;
        }

        // ─── Paint-select ─────────────────────────────────────────────────────────

        private void HandlePaintSelect(bool triggerDown, Vector3 tipPos)
        {
            if (triggerDown && !_paintTriggerHeld)
            {
                // Stroke start
                _paintTriggerHeld = true;
                _paintedThisDrag.Clear();

                if (ActiveSelectionModifier == SelectionModifier.Replace)
                    Selection?.ClearHandles();
            }
            else if (!triggerDown && _paintTriggerHeld)
            {
                // Stroke end
                _paintTriggerHeld = false;
                _paintedThisDrag.Clear();
            }

            if (!_paintTriggerHeld || SceneRoot == null) return;

            // Each frame: select/deselect/toggle handles within hover radius of tip
            foreach (var child in SceneRoot.GetChildren())
            {
                if (child is not PolysurfaceNode polyNode) continue;
                foreach (var handle in polyNode.AllHandles())
                {
                    if (_paintedThisDrag.Contains(handle)) continue; // each handle once per stroke
                    if (tipPos.DistanceTo(handle.GlobalGrabPosition) > HoverRadius) continue;

                    _paintedThisDrag.Add(handle);

                    // Replace was handled at stroke start (clear); now just Add
                    var mod = ActiveSelectionModifier == SelectionModifier.Replace
                        ? SelectionModifier.Add
                        : ActiveSelectionModifier;
                    Selection?.ModifyHandleSelection(handle, mod);
                }
            }
        }

        // ─── Hull-select ──────────────────────────────────────────────────────────

        private void HandleHullSelect(bool triggerDown, Vector3 tipPos)
        {
            if (triggerDown && !_hullTriggerHeld)
            {
                _hullTriggerHeld = true;
                _hullPath.Clear();
                _hullPath.Add(tipPos);
                _hullLastPos = tipPos;
            }
            else if (!triggerDown && _hullTriggerHeld)
            {
                _hullTriggerHeld = false;
                ApplyHullSelection();
                _hullPath.Clear();
            }

            if (!_hullTriggerHeld) return;

            if (tipPos.DistanceTo(_hullLastPos) >= HullRecordMinDist)
            {
                _hullPath.Add(tipPos);
                _hullLastPos = tipPos;
            }
        }

        private void ApplyHullSelection()
        {
            if (_hullPath.Count < 4 || SceneRoot == null)
            {
                GD.Print("[VR Hull] Need at least 4 path points — trace a wider 3D path.");
                return;
            }

            var hull = Build3DConvexHull(_hullPath);
            if (hull == null)
            {
                GD.Print("[VR Hull] Path is too flat or collinear to form a 3D hull.");
                return;
            }

            if (ActiveSelectionModifier == SelectionModifier.Replace)
                Selection?.ClearHandles();

            var mod = ActiveSelectionModifier == SelectionModifier.Replace
                ? SelectionModifier.Add
                : ActiveSelectionModifier;

            int count = 0;
            foreach (var child in SceneRoot.GetChildren())
            {
                if (child is not PolysurfaceNode polyNode) continue;
                foreach (var handle in polyNode.AllHandles())
                {
                    if (!IsInsideConvexHull(hull, handle.GlobalPosition)) continue;
                    Selection?.ModifyHandleSelection(handle, mod);
                    count++;
                }
            }

            GD.Print($"[VR Hull] {_hullPath.Count} path pts → {hull.Count}-face hull → {count} handles.");
        }

        // ─── 3-D convex hull (incremental / gift-wrapping) ────────────────────────

        /// <summary>
        /// Build the 3-D convex hull of <paramref name="pts"/> using an incremental algorithm.
        /// Returns a list of outward-facing triangular faces, or null if the points are
        /// degenerate (collinear, coplanar, or fewer than 4 distinct positions).
        /// </summary>
        private static List<HullFace>? Build3DConvexHull(List<Vector3> pts)
        {
            if (pts.Count < 4) return null;

            // ── Find initial tetrahedron ──────────────────────────────────────────
            // Extremal pair along X
            int ia = 0, ib = 0;
            float minX = float.MaxValue, maxX = float.MinValue;
            for (int i = 0; i < pts.Count; i++)
            {
                if (pts[i].X < minX) { minX = pts[i].X; ia = i; }
                if (pts[i].X > maxX) { maxX = pts[i].X; ib = i; }
            }
            if (ia == ib) return null;

            // Furthest point from the ia–ib line
            var axisAB = (pts[ib] - pts[ia]).Normalized();
            int ic = -1; float maxLineDist = 0f;
            for (int i = 0; i < pts.Count; i++)
            {
                if (i == ia || i == ib) continue;
                var v = pts[i] - pts[ia];
                float d = (v - axisAB * v.Dot(axisAB)).Length();
                if (d > maxLineDist) { maxLineDist = d; ic = i; }
            }
            if (ic < 0 || maxLineDist < 1e-6f) return null;

            // Furthest point from the ia–ib–ic plane
            var triNorm = (pts[ib] - pts[ia]).Cross(pts[ic] - pts[ia]);
            if (triNorm.LengthSquared() < 1e-12f) return null;
            triNorm = triNorm.Normalized();
            int id = -1; float maxPlaneDist = 0f;
            for (int i = 0; i < pts.Count; i++)
            {
                if (i == ia || i == ib || i == ic) continue;
                float d = Mathf.Abs((pts[i] - pts[ia]).Dot(triNorm));
                if (d > maxPlaneDist) { maxPlaneDist = d; id = i; }
            }
            if (id < 0 || maxPlaneDist < 1e-6f) return null;

            // ── Build initial tetrahedron ─────────────────────────────────────────
            var centroid = (pts[ia] + pts[ib] + pts[ic] + pts[id]) * 0.25f;
            var faces    = new List<HullFace>();

            // Adds a face (a,b,c) with outward normal (flipping winding if necessary).
            void MakeFace(int a, int b, int c)
            {
                var n = (pts[b] - pts[a]).Cross(pts[c] - pts[a]);
                if (n.LengthSquared() < 1e-12f) return;
                n = n.Normalized();
                if (n.Dot(pts[a] - centroid) < 0) { n = -n; (b, c) = (c, b); }
                faces.Add(new HullFace(a, b, c, n, n.Dot(pts[a])));
            }

            MakeFace(ia, ib, ic);
            MakeFace(ia, ib, id);
            MakeFace(ia, ic, id);
            MakeFace(ib, ic, id);

            // ── Incremental expansion ─────────────────────────────────────────────
            for (int i = 0; i < pts.Count; i++)
            {
                if (i == ia || i == ib || i == ic || i == id) continue;
                var p = pts[i];

                // Collect faces visible from p
                var visIdx = new List<int>();
                for (int fi = 0; fi < faces.Count; fi++)
                    if (faces[fi].Normal.Dot(p) - faces[fi].Offset > 1e-6f)
                        visIdx.Add(fi);

                if (visIdx.Count == 0) continue; // p is already inside

                // Count how many visible faces share each edge
                var edgeUse = new Dictionary<(int, int), int>();
                foreach (int fi in visIdx)
                {
                    void Count(int a, int b)
                    {
                        var key = a < b ? (a, b) : (b, a);
                        edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
                    }
                    Count(faces[fi].A, faces[fi].B);
                    Count(faces[fi].B, faces[fi].C);
                    Count(faces[fi].C, faces[fi].A);
                }

                // Remove visible faces (reverse order so indices stay valid)
                for (int k = visIdx.Count - 1; k >= 0; k--)
                    faces.RemoveAt(visIdx[k]);

                // Stitch new faces from horizon edges (count == 1) to p
                foreach (var (edge, use) in edgeUse)
                {
                    if (use != 1) continue;
                    MakeFace(edge.Item1, edge.Item2, i);
                }
            }

            return faces.Count > 0 ? faces : null;
        }

        /// <summary>
        /// Returns true if <paramref name="p"/> is inside (or on the boundary of) the
        /// convex hull represented as a list of outward-facing triangular faces.
        /// </summary>
        private static bool IsInsideConvexHull(List<HullFace> hull, Vector3 p, float eps = 1e-4f)
        {
            foreach (var f in hull)
                if (f.Normal.Dot(p) - f.Offset > eps) return false;
            return true;
        }

        // ─── Edge selection ───────────────────────────────────────────────────────

        private void SelectHoveredEdge()
        {
            if (_hoveredEdgeSurfNode == null) return;
            var polyNode = _hoveredEdgeSurfNode.GetParent() as PolysurfaceNode;
            if (polyNode?.Data == null || _hoveredEdgeSurfNode.Surface == null) return;

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
                if (IsLeft)
                {
                    if (_radialMenu != null && !_radialMenu.IsAtRoot)
                    {
                        bool wasModifier = CurrentPage == PageId.Modifier;
                        _pageIds.Pop();
                        _radialMenu.PopPage();

                        if (wasModifier)
                        {
                            ActiveSelectionMode     = SelectionMode.None;
                            ActiveSelectionModifier = SelectionModifier.Replace;
                            _paintTriggerHeld       = false;
                            _paintedThisDrag.Clear();
                            _hullTriggerHeld        = false;
                            _hullPath.Clear();
                            GD.Print("[VR] Exited selection mode.");
                        }
                    }
                    else
                    {
                        OnUndo?.Invoke();
                    }
                }
                else
                {
                    OnRedo?.Invoke();
                }
            }
            _menuWasDown = menuDown;
        }

        // ─── Trackpad / radial menu ───────────────────────────────────────────────

        private void HandlePrimaryPad(bool touchDown, bool primaryDown, Vector2 padVec)
        {
            int sector = GetSector(padVec);

            // ── Modifier page (left hand, paint/hull mode active) ─────────────────
            if (IsLeft && CurrentPage == PageId.Modifier)
            {
                // Update the active modifier while click is held; reset when released
                ActiveSelectionModifier = (primaryDown && sector >= 0) ? sector switch
                {
                    0 => SelectionModifier.XOR,
                    1 => SelectionModifier.Add,
                    2 => SelectionModifier.Replace,
                    3 => SelectionModifier.Remove,
                    _ => SelectionModifier.Replace,
                } : SelectionModifier.Replace;

                // Show/hide menu on touch; highlight held direction
                bool shouldShow = touchDown || primaryDown;
                if (shouldShow && !_menuShowing)
                {
                    _menuShowing = true;
                    _radialMenu!.ResetHighlight();
                    _radialMenu.Visible = true;
                }
                else if (!shouldShow && _menuShowing)
                {
                    _menuShowing = false;
                    _radialMenu!.Visible = false;
                }
                if (_menuShowing && _radialMenu != null)
                    _radialMenu.UpdateHighlight(primaryDown ? sector : -1);

                _primaryWasDown = primaryDown;
                return;
            }

            // ── Normal menu behaviour ─────────────────────────────────────────────
            bool menuShouldShow = touchDown || primaryDown;
            if (menuShouldShow && !_menuShowing)
            {
                _menuShowing = true;
                _radialMenu?.ResetHighlight();
                if (_radialMenu != null) _radialMenu.Visible = true;
            }
            else if (!menuShouldShow && _menuShowing)
            {
                _menuShowing = false;
                if (_radialMenu != null) _radialMenu.Visible = false;
            }

            if (_menuShowing && _radialMenu != null)
                _radialMenu.UpdateHighlight(sector);

            // Click released while menu was showing → execute sector
            if (_menuShowing && !primaryDown && _primaryWasDown && sector >= 0)
                ExecuteSector(sector);

            _primaryWasDown = primaryDown;
        }

        // ─── Sector execution ─────────────────────────────────────────────────────

        private void ExecuteSector(int sector)
        {
            if (!(_radialMenu?.IsSelectable(sector) ?? false)) return;

            if (IsLeft)
            {
                switch (CurrentPage)
                {
                    case PageId.Root:
                        switch (sector)
                        {
                            case 0: PushMenuPage(PageId.Select); break; // Select →
                            case 1: PushMenuPage(PageId.Modify); break; // Modify →
                            case 2: PushMenuPage(PageId.File);   break; // File →
                        }
                        break;

                    case PageId.Select:
                        if (sector == 0) // Paint Select
                        {
                            ActiveSelectionMode = SelectionMode.Paint;
                            PushMenuPage(PageId.Modifier);
                            GD.Print("[VR] Paint Select: sweep trigger near handles. Hold left pad dir for modifier.");
                        }
                        else if (sector == 1) // Hull Select
                        {
                            ActiveSelectionMode = SelectionMode.Hull;
                            PushMenuPage(PageId.Modifier);
                            GD.Print("[VR] Hull Select: trace a path with trigger; release to select inside hull.");
                        }
                        break;

                    case PageId.Modifier:
                        // No click actions here; modifier is set by held-click in HandlePrimaryPad
                        break;

                    case PageId.Modify:
                        switch (sector)
                        {
                            case 0: OnSpawnAttach?.Invoke(); break;
                            case 1: OnToggleG1?.Invoke();    break;
                            case 2: OnDelete?.Invoke();      break;
                        }
                        break;

                    case PageId.File:
                        if (sector == 0) OnSave?.Invoke();
                        break;
                }
            }
            else
            {
                // Right hand: tool selection
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

        // ─── Menu page definitions ────────────────────────────────────────────────

        private void PushMenuPage(PageId page)
        {
            _pageIds.Push(page);
            switch (page)
            {
                case PageId.Root:
                    _radialMenu!.PushPage(
                        new[] { "Select", "Modify", "File", "" },
                        isSubmenu:  new[] { true,  true,  true,  false },
                        isDisabled: new[] { false, false, false, true  });
                    break;

                case PageId.Select:
                    _radialMenu!.PushPage(
                        new[] { "Paint Select", "Hull Select", "", "" },
                        isDisabled: new[] { false, false, true, true });
                    break;

                case PageId.Modifier:
                    _radialMenu!.PushPage(
                        new[] { "XOR", "Add", "Replace", "Remove" });
                    break;

                case PageId.Modify:
                    _radialMenu!.PushPage(
                        new[] { "Attach", "Toggle G1", "Delete", "" },
                        isDisabled: new[] { false, false, false, true });
                    break;

                case PageId.File:
                    _radialMenu!.PushPage(
                        new[] { "Save", "", "", "" },
                        isDisabled: new[] { false, true, true, true });
                    break;
            }
        }

        // ─── Sector helper ────────────────────────────────────────────────────────

        private static int GetSector(Vector2 v)
        {
            if (v.Length() < 0.40f) return -1;
            return Mathf.Abs(v.Y) >= Mathf.Abs(v.X)
                ? (v.Y > 0 ? 0 : 2)
                : (v.X > 0 ? 1 : 3);
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

        // ─── Hull face (used by Build3DConvexHull / IsInsideConvexHull) ───────────

        private readonly struct HullFace
        {
            public readonly int     A, B, C;   // indices into the original point list
            public readonly Vector3 Normal;    // outward-pointing unit normal
            public readonly float   Offset;    // Normal · any_vertex_on_face

            public HullFace(int a, int b, int c, Vector3 normal, float offset)
            {
                A = a; B = b; C = c; Normal = normal; Offset = offset;
            }
        }
    }
}
