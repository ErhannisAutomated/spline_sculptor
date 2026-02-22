using System.Collections.Generic;
using Godot;
using SplineSculptor.Interaction;
using SplineSculptor.Model;
using SplineSculptor.Model.Undo;
using SplineSculptor.Rendering;

namespace SplineSculptor.VR
{
    /// <summary>
    /// Desktop fallback interaction: mouse-ray hover/drag of control point handles,
    /// surface and edge selection, orbit camera.
    ///
    /// Tool switching:  Q=Auto  W=Point  E=Edge  R=Surface
    ///
    /// Selection modifiers:
    ///   (none)     = Replace    Shift      = Add
    ///   Ctrl       = XOR        Ctrl+Shift = Remove
    ///
    /// Transforms (handles must be selected):
    ///   Left-drag     = translate
    ///   Middle-drag   = scale around CoM  (200 px right = 2×)
    ///   Right-drag    = rotate around first-click surface point
    ///   Right-drag (nothing selected) = orbit camera
    ///
    /// Double-click on surface = move orbit pivot to hit point.
    /// </summary>
    [GlobalClass]
    public partial class DesktopInteraction : Node
    {
        public SculptScene?      Scene         { get; set; }
        public Node3D?           SceneRoot     { get; set; }
        public SelectionManager? Selection     { get; set; }

        private Camera3D?           _camera;
        private ControlPointHandle? _hoveredHandle;

        // Surface / edge tracking
        private SurfaceNode? _selectedSurfaceNode;
        private SurfaceNode? _edgeHoverSurfNode;
        private SurfaceEdge  _edgeHoverEdge;
        private readonly List<(SurfaceNode node, SurfaceEdge edge)> _selectedEdgeNodes = new();

        // Tool state
        private SelectionTool _currentTool = SelectionTool.Auto;

        // Orbit
        private bool    _orbiting    = false;
        private Basis   _orbitBasis  = Basis.Identity;
        private float   _orbitDist   = 3.0f;
        private Vector3 _orbitTarget = Vector3.Zero;

        // ─── Click / drag state machine ───────────────────────────────────────────

        private enum DragState
        {
            Idle,
            Threshold,         // any button: waiting to exceed pixel threshold
            DraggingHandles,   // left-drag: translate selected handles
            EmptyDrag,         // left-drag on empty space: suppress click on release
            ScalingHandles,    // middle-drag: scale selected handles around CoM
            RotatingHandles,   // right-drag (with selection): rotate selected handles
        }

        private DragState    _dragState      = DragState.Idle;
        private MouseButton  _thresholdButton = MouseButton.Left;
        private Vector2      _mouseDownPos;
        private SelectionModifier _clickModifier;
        private const float  DragPixelThresh = 6f;

        // Snapshot of what was under cursor at left-button-down
        private ControlPointHandle? _clickTargetHandle;
        private SurfaceNode?        _clickTargetEdgeSurf;
        private SurfaceEdge         _clickTargetEdge;

        // Active multi-drag
        private readonly List<ControlPointHandle> _multiDragHandles = new();
        private Plane   _dragPlane;
        private Vector3 _dragOriginHit;  // translate: initial hit on drag plane
        private Vector3 _scaleCoM;       // scale: pivot = center of mass
        private Vector3 _rotatePivot;    // rotate: pivot = surface hit point

        private static readonly SurfaceEdge[] AllEdges =
            { SurfaceEdge.UMin, SurfaceEdge.UMax, SurfaceEdge.VMin, SurfaceEdge.VMax };

        public override void _Ready()
        {
            _camera = GetViewport().GetCamera3D();
            if (_camera != null)
            {
                _orbitBasis = _camera.Basis;
                _orbitDist  = _camera.Position.Length();
            }
        }

        public override void _Input(InputEvent @event)
        {
            // ── Tool switching ────────────────────────────────────────────────────
            if (@event is InputEventKey key && key.Pressed && !key.Echo)
            {
                switch (key.Keycode)
                {
                    case Key.Q: SetTool(SelectionTool.Auto);    return;
                    case Key.W: SetTool(SelectionTool.Point);   return;
                    case Key.E: SetTool(SelectionTool.Edge);    return;
                    case Key.R: SetTool(SelectionTool.Surface); return;
                }
            }

            if (@event is InputEventMouseButton mb)
            {
                // ── Double-click (before regular press) ───────────────────────────
                if (mb.ButtonIndex == MouseButton.Left && mb.Pressed && mb.DoubleClick)
                {
                    HandleDoubleClick(mb.Position);
                    return;
                }

                // ── Wheel zoom ────────────────────────────────────────────────────
                if (mb.ButtonIndex == MouseButton.WheelUp)
                {
                    _orbitDist = Mathf.Max(0.5f, _orbitDist - 0.3f);
                    if (_camera != null)
                        _camera.Position = _orbitTarget + _orbitBasis.Z * _orbitDist;
                }
                if (mb.ButtonIndex == MouseButton.WheelDown)
                {
                    _orbitDist += 0.3f;
                    if (_camera != null)
                        _camera.Position = _orbitTarget + _orbitBasis.Z * _orbitDist;
                }

                // ── Left button ───────────────────────────────────────────────────
                if (mb.ButtonIndex == MouseButton.Left && mb.Pressed)
                {
                    _mouseDownPos        = mb.Position;
                    _clickModifier       = GetModifier();
                    _clickTargetHandle   = _hoveredHandle;
                    _clickTargetEdgeSurf = _edgeHoverSurfNode;
                    _clickTargetEdge     = _edgeHoverEdge;
                    _thresholdButton     = MouseButton.Left;
                    _dragState           = DragState.Threshold;
                    return;
                }
                if (mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
                {
                    if (_dragState == DragState.Threshold)       { HandleClick(); _dragState = DragState.Idle; }
                    else if (_dragState == DragState.DraggingHandles) FinalizeDrag(); // sets Idle
                    else _dragState = DragState.Idle; // EmptyDrag or other
                    return;
                }

                // ── Middle button: scale ──────────────────────────────────────────
                if (mb.ButtonIndex == MouseButton.Middle && mb.Pressed)
                {
                    if (_dragState == DragState.Idle &&
                        (Selection?.SelectedHandles.Count ?? 0) > 0)
                    {
                        _mouseDownPos    = mb.Position;
                        _thresholdButton = MouseButton.Middle;
                        _dragState       = DragState.Threshold;
                    }
                    return;
                }
                if (mb.ButtonIndex == MouseButton.Middle && !mb.Pressed)
                {
                    if (_dragState == DragState.ScalingHandles)  FinalizeDrag(); // sets Idle
                    else if (_dragState == DragState.Threshold &&
                             _thresholdButton == MouseButton.Middle)
                        _dragState = DragState.Idle;
                    return;
                }

                // ── Right button: rotate (with selection) or orbit ────────────────
                if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
                {
                    if (_dragState == DragState.Idle &&
                        (Selection?.SelectedHandles.Count ?? 0) > 0)
                    {
                        _mouseDownPos    = mb.Position;
                        _thresholdButton = MouseButton.Right;
                        _dragState       = DragState.Threshold;
                    }
                    else
                    {
                        _orbiting = true;
                    }
                    return;
                }
                if (mb.ButtonIndex == MouseButton.Right && !mb.Pressed)
                {
                    _orbiting = false;
                    if (_dragState == DragState.RotatingHandles)     FinalizeDrag(); // sets Idle
                    else if (_dragState == DragState.Threshold &&
                             _thresholdButton == MouseButton.Right)
                        _dragState = DragState.Idle;
                    return;
                }
            }

            // ── Mouse motion ──────────────────────────────────────────────────────
            if (@event is InputEventMouseMotion mm)
            {
                if (_orbiting && _camera != null)
                {
                    float dx = mm.Relative.X * 0.005f;
                    float dy = mm.Relative.Y * 0.005f;
                    _orbitBasis = _orbitBasis.Rotated(_orbitBasis.Y.Normalized(), -dx);
                    _orbitBasis = _orbitBasis.Rotated(_orbitBasis.X.Normalized(), -dy);
                    _orbitBasis = _orbitBasis.Orthonormalized();
                    _camera.Position = _orbitTarget + _orbitBasis.Z * _orbitDist;
                    _camera.Basis    = _orbitBasis;
                }

                if (_dragState == DragState.Threshold &&
                    (mm.Position - _mouseDownPos).Length() >= DragPixelThresh)
                {
                    switch (_thresholdButton)
                    {
                        case MouseButton.Left:   TryStartDrag();   break;
                        case MouseButton.Middle: TryStartScale();  break;
                        case MouseButton.Right:  TryStartRotate(); break;
                    }
                }
            }
        }

        public override void _Process(double delta)
        {
            if (_camera == null || SceneRoot == null) return;

            var mousePos  = GetViewport().GetMousePosition();
            var rayDir    = _camera.ProjectRayNormal(mousePos);
            var rayOrigin = _camera.GlobalPosition;

            // ── Active transform drags (all skip hover detection) ──────────────────
            if (_multiDragHandles.Count > 0)
            {
                switch (_dragState)
                {
                    case DragState.DraggingHandles:
                        var hit = _dragPlane.IntersectsRay(rayOrigin, rayDir);
                        if (hit.HasValue)
                        {
                            var moveDelta = hit.Value - _dragOriginHit;
                            foreach (var h in _multiDragHandles) h.MoveGroupDrag(moveDelta);
                        }
                        return;

                    case DragState.ScalingHandles:
                        // Exponential: 200 px right = ×2, 200 px left = ×0.5
                        float scaleFactor = Mathf.Pow(2f,
                            (mousePos.X - _mouseDownPos.X) / 200f);
                        foreach (var h in _multiDragHandles)
                            h.ScaleGroupDrag(_scaleCoM, scaleFactor);
                        return;

                    case DragState.RotatingHandles:
                        // Horizontal = yaw around camera up, vertical = pitch around camera right
                        float rdx = (mousePos.X - _mouseDownPos.X) * 0.01f;
                        float rdy = (mousePos.Y - _mouseDownPos.Y) * 0.01f;
                        var rotY  = new Quaternion(_camera.Basis.Y.Normalized(), -rdx);
                        var rotX  = new Quaternion(_camera.Basis.X.Normalized(), -rdy);
                        var rot   = (rotY * rotX).Normalized();
                        foreach (var h in _multiDragHandles) h.RotateGroupDrag(_rotatePivot, rot);
                        return;
                }
            }

            if (_dragState == DragState.EmptyDrag) return;

            // ── Hover detection ───────────────────────────────────────────────────

            bool wantHandles = _currentTool is SelectionTool.Auto or SelectionTool.Point;
            bool wantEdges   = _currentTool is SelectionTool.Auto or SelectionTool.Edge;

            ControlPointHandle? closestHandle = null;
            float closestHandleDist = 0.08f;
            float closestHandleT    = float.MaxValue;

            if (wantHandles)
            {
                foreach (var child in SceneRoot.GetChildren())
                {
                    if (child is not PolysurfaceNode polyNode) continue;
                    foreach (var handle in polyNode.AllHandles())
                    {
                        float t = (handle.GlobalPosition - rayOrigin).Dot(rayDir);
                        if (t < 0) continue;
                        float dist = (handle.GlobalPosition - (rayOrigin + rayDir * t)).Length();
                        if (dist < closestHandleDist && t < closestHandleT)
                        {
                            closestHandleDist = dist;
                            closestHandleT    = t;
                            closestHandle     = handle;
                        }
                    }
                }
            }

            SurfaceNode? edgeNode = null;
            SurfaceEdge  edgeEdge = SurfaceEdge.UMin;
            if (wantEdges && (_currentTool == SelectionTool.Edge || closestHandle == null))
                (edgeNode, edgeEdge) = FindHoveredEdge(rayOrigin, rayDir);

            UpdateHandleHover(closestHandle);
            UpdateEdgeHover(edgeNode, edgeEdge);
        }

        // ─── Modifier helper ──────────────────────────────────────────────────────

        private static SelectionModifier GetModifier()
        {
            bool shift = Input.IsKeyPressed(Key.Shift);
            bool ctrl  = Input.IsKeyPressed(Key.Ctrl);
            if (ctrl && shift) return SelectionModifier.Remove;
            if (ctrl)          return SelectionModifier.XOR;
            if (shift)         return SelectionModifier.Add;
            return SelectionModifier.Replace;
        }

        private void SetTool(SelectionTool tool)
        {
            _currentTool = tool;
            GD.Print($"[Tool] {tool}  (Q=Auto  W=Point  E=Edge  R=Surface)");
        }

        // ─── Click (threshold not exceeded) ──────────────────────────────────────

        private void HandleClick()
        {
            if (_camera == null || _orbiting) return;

            var mousePos  = GetViewport().GetMousePosition();
            var rayDir    = _camera.ProjectRayNormal(mousePos);
            var rayOrigin = _camera.GlobalPosition;
            var mod       = _clickModifier;

            if (_clickTargetHandle != null)
            {
                Selection?.ModifyHandleSelection(_clickTargetHandle, mod);
                ClearEdgeSelection();
            }
            else if (_clickTargetEdgeSurf != null)
            {
                var polyNode = _clickTargetEdgeSurf.GetParent() as PolysurfaceNode;
                if (polyNode?.Data != null && _clickTargetEdgeSurf.Surface != null)
                {
                    var er = new EdgeRef(
                        _clickTargetEdgeSurf.Surface, polyNode.Data, _clickTargetEdge);
                    ApplyEdgeSelectionMod(_clickTargetEdgeSurf, _clickTargetEdge, er, mod);
                    SetSelectedSurfaceNode(_clickTargetEdgeSurf);
                    Selection?.SelectSurface(_clickTargetEdgeSurf.Surface);
                    GD.Print($"[Select] Edge {_clickTargetEdge} on '{polyNode.Data.Name}'");
                }
            }
            else if (mod == SelectionModifier.Replace)
            {
                TrySelectSurface(rayOrigin, rayDir);
            }
            // modifier key + click on empty space → don't touch selection
        }

        // ─── Translate drag (left) ────────────────────────────────────────────────

        private void TryStartDrag()
        {
            if (_camera == null || _clickTargetHandle == null)
            {
                _dragState = DragState.EmptyDrag; // suppress click-on-release
                return;
            }

            bool alreadySelected =
                Selection?.SelectedHandles.Contains(_clickTargetHandle) ?? false;
            if (!alreadySelected)
                Selection?.ModifyHandleSelection(_clickTargetHandle, _clickModifier);

            if (Selection == null || Selection.SelectedHandles.Count == 0) return;

            var rayDir     = _camera.ProjectRayNormal(_mouseDownPos);
            var rayOrigin  = _camera.GlobalPosition;
            _dragPlane     = new Plane(-rayDir, _clickTargetHandle.GlobalPosition);
            var hit        = _dragPlane.IntersectsRay(rayOrigin, rayDir);
            _dragOriginHit = hit ?? _clickTargetHandle.GlobalPosition;

            _multiDragHandles.Clear();
            foreach (var h in Selection.SelectedHandles)
            {
                h.StartGroupDrag();
                _multiDragHandles.Add(h);
            }
            _dragState = DragState.DraggingHandles;
        }

        // ─── Scale drag (middle) ──────────────────────────────────────────────────

        private void TryStartScale()
        {
            if (_camera == null || (Selection?.SelectedHandles.Count ?? 0) == 0)
            {
                _dragState = DragState.EmptyDrag;
                return;
            }

            _scaleCoM = GetSelectionCoM();

            _multiDragHandles.Clear();
            foreach (var h in Selection!.SelectedHandles)
            {
                h.StartGroupDrag();
                _multiDragHandles.Add(h);
            }
            _dragState = DragState.ScalingHandles;
        }

        // ─── Rotate drag (right, with selection) ──────────────────────────────────

        private void TryStartRotate()
        {
            if (_camera == null || (Selection?.SelectedHandles.Count ?? 0) == 0)
            {
                _dragState = DragState.EmptyDrag;
                return;
            }

            // Pivot: surface hit under right-button-down position, fallback to CoM
            _rotatePivot = GetPivotFromRaycast(_mouseDownPos);

            _multiDragHandles.Clear();
            foreach (var h in Selection!.SelectedHandles)
            {
                h.StartGroupDrag();
                _multiDragHandles.Add(h);
            }
            _dragState = DragState.RotatingHandles;
        }

        // ─── Finalize any active transform drag ───────────────────────────────────

        private void FinalizeDrag()
        {
            var entries = new List<(SculptSurface surf, int u, int v,
                                    Vector3 startPos, Vector3 endPos, Polysurface? poly)>();
            foreach (var h in _multiDragHandles)
            {
                var (surf, u, v, startPos, endPos, poly) = h.EndGroupDrag();
                if (surf != null && endPos != startPos)
                    entries.Add((surf, u, v, startPos, endPos, poly));
            }

            if (entries.Count > 0)
            {
                var cmd = new MultiMoveControlPointCommand(entries);
                ControlPointHandle.SceneRef?.UndoStack.Execute(new AlreadyAppliedCommand(cmd));
            }

            foreach (var h in _multiDragHandles)
                h.TriggerConstraintEnforcement();
            _multiDragHandles.Clear();
            _dragState = DragState.Idle;
        }

        // ─── Double-click: move orbit pivot ───────────────────────────────────────

        private void HandleDoubleClick(Vector2 clickPos)
        {
            if (_camera == null) return;
            var rayDir    = _camera.ProjectRayNormal(clickPos);
            var rayOrigin = _camera.GlobalPosition;

            var spaceState = GetViewport().GetCamera3D()?.GetWorld3D().DirectSpaceState;
            if (spaceState == null) return;

            var rayParams = PhysicsRayQueryParameters3D.Create(
                rayOrigin, rayOrigin + rayDir * 500f);
            rayParams.CollideWithBodies = true;

            var result = spaceState.IntersectRay(rayParams);
            if (result.Count > 0)
            {
                var hitPos   = result["position"].As<Vector3>();
                _orbitTarget = hitPos;
                _camera.Position = _orbitTarget + _orbitBasis.Z * _orbitDist;
                GD.Print($"[Orbit] Target → {hitPos:F3}");
            }
        }

        // ─── Pivot helpers ────────────────────────────────────────────────────────

        /// <summary>
        /// Raycast at screenPos and return the surface hit world position.
        /// Falls back to the CoM of selected handles if nothing is hit.
        /// </summary>
        private Vector3 GetPivotFromRaycast(Vector2 screenPos)
        {
            if (_camera == null) return GetSelectionCoM();

            var rayDir    = _camera.ProjectRayNormal(screenPos);
            var rayOrigin = _camera.GlobalPosition;

            var spaceState = GetViewport().GetCamera3D()?.GetWorld3D().DirectSpaceState;
            if (spaceState != null)
            {
                var rayParams = PhysicsRayQueryParameters3D.Create(
                    rayOrigin, rayOrigin + rayDir * 500f);
                rayParams.CollideWithBodies = true;
                var result = spaceState.IntersectRay(rayParams);
                if (result.Count > 0)
                    return result["position"].As<Vector3>();
            }
            return GetSelectionCoM();
        }

        private Vector3 GetSelectionCoM()
        {
            if (Selection == null || Selection.SelectedHandles.Count == 0)
                return Vector3.Zero;
            var com = Vector3.Zero;
            foreach (var h in Selection.SelectedHandles) com += h.GlobalPosition;
            return com / Selection.SelectedHandles.Count;
        }

        // ─── Edge selection (visual + data) ──────────────────────────────────────

        private void ApplyEdgeSelectionMod(
            SurfaceNode surfNode, SurfaceEdge edge, EdgeRef er, SelectionModifier mod)
        {
            switch (mod)
            {
                case SelectionModifier.Replace:
                    foreach (var (n, _) in _selectedEdgeNodes) n.SetSelectedEdge(null);
                    _selectedEdgeNodes.Clear();
                    surfNode.SetSelectedEdge(edge);
                    _selectedEdgeNodes.Add((surfNode, edge));
                    Selection?.ModifyEdgeSelection(er, SelectionModifier.Replace);
                    break;

                case SelectionModifier.Add:
                    surfNode.SetSelectedEdge(edge);
                    _selectedEdgeNodes.Add((surfNode, edge));
                    Selection?.ModifyEdgeSelection(er, SelectionModifier.Add);
                    break;

                case SelectionModifier.XOR:
                    int xi = _selectedEdgeNodes.FindIndex(
                        x => x.node == surfNode && x.edge == edge);
                    if (xi >= 0)
                    {
                        _selectedEdgeNodes[xi].node.SetSelectedEdge(null);
                        _selectedEdgeNodes.RemoveAt(xi);
                        Selection?.ModifyEdgeSelection(er, SelectionModifier.Remove);
                    }
                    else
                    {
                        surfNode.SetSelectedEdge(edge);
                        _selectedEdgeNodes.Add((surfNode, edge));
                        Selection?.ModifyEdgeSelection(er, SelectionModifier.Add);
                    }
                    break;

                case SelectionModifier.Remove:
                    int ri = _selectedEdgeNodes.FindIndex(
                        x => x.node == surfNode && x.edge == edge);
                    if (ri >= 0)
                    {
                        _selectedEdgeNodes[ri].node.SetSelectedEdge(null);
                        _selectedEdgeNodes.RemoveAt(ri);
                        Selection?.ModifyEdgeSelection(er, SelectionModifier.Remove);
                    }
                    break;
            }
        }

        private void ClearEdgeSelection()
        {
            foreach (var (node, _) in _selectedEdgeNodes)
                node.SetSelectedEdge(null);
            _selectedEdgeNodes.Clear();
            Selection?.ClearEdges();
        }

        // ─── Hover helpers ────────────────────────────────────────────────────────

        private void UpdateHandleHover(ControlPointHandle? closest)
        {
            if (_hoveredHandle == closest) return;
            if (_hoveredHandle != null) _hoveredHandle.IsHovered = false;
            _hoveredHandle = closest;
            if (_hoveredHandle != null) _hoveredHandle.IsHovered = true;
        }

        private void UpdateEdgeHover(SurfaceNode? node, SurfaceEdge edge)
        {
            bool same = node == _edgeHoverSurfNode && (node == null || edge == _edgeHoverEdge);
            if (same) return;
            _edgeHoverSurfNode?.SetHoveredEdge(null);
            _edgeHoverSurfNode = node;
            _edgeHoverEdge     = edge;
            _edgeHoverSurfNode?.SetHoveredEdge(edge);
        }

        // ─── Edge hover hit-test ──────────────────────────────────────────────────

        private (SurfaceNode? node, SurfaceEdge edge) FindHoveredEdge(
            Vector3 origin, Vector3 dir)
        {
            SurfaceNode? bestNode = null;
            SurfaceEdge  bestEdge = SurfaceEdge.UMin;
            float        bestDist = 0.10f;
            float        bestT    = float.MaxValue;

            foreach (var child in SceneRoot!.GetChildren())
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
                            float dist = RaySegmentDist(origin, dir, a, b, out float t);
                            if (dist < bestDist && t < bestT)
                            {
                                bestDist = dist;
                                bestT    = t;
                                bestNode = surfNode;
                                bestEdge = edge;
                            }
                        }
                    }
                }
            }
            return (bestNode, bestEdge);
        }

        // ─── Surface selection via physics raycast ────────────────────────────────

        private void TrySelectSurface(Vector3 rayOrigin, Vector3 rayDir)
        {
            var spaceState = GetViewport().GetCamera3D()?.GetWorld3D().DirectSpaceState;
            if (spaceState == null) return;

            var rayParams = PhysicsRayQueryParameters3D.Create(
                rayOrigin, rayOrigin + rayDir * 500f);
            rayParams.CollideWithBodies = true;

            var result = spaceState.IntersectRay(rayParams);
            if (result.Count > 0)
            {
                var collider = result["collider"].As<Node>();
                var surfNode = collider?.GetParent() as SurfaceNode;
                var polyNode = surfNode?.GetParent() as PolysurfaceNode;

                if (surfNode != null && polyNode != null && surfNode.Surface != null)
                {
                    ClearEdgeSelection();
                    SetSelectedSurfaceNode(surfNode);
                    Selection?.SelectSurface(surfNode.Surface);
                    if (polyNode.Data != null)
                        Selection?.SelectPolysurface(polyNode.Data);
                    GD.Print($"[Select] Surface in '{polyNode.Data?.Name ?? "?"}'");
                    return;
                }
            }

            GD.Print("[Select] Deselected (empty click)");
            ClearEdgeSelection();
            SetSelectedSurfaceNode(null);
            Selection?.ClearAll();
        }

        private void SetSelectedSurfaceNode(SurfaceNode? node)
        {
            if (_selectedSurfaceNode == node) return;
            if (_selectedSurfaceNode != null) _selectedSurfaceNode.IsSelected = false;
            _selectedSurfaceNode = node;
            if (_selectedSurfaceNode != null) _selectedSurfaceNode.IsSelected = true;
        }

        // ─── Ray–segment distance ─────────────────────────────────────────────────

        private static float RaySegmentDist(
            Vector3 ro, Vector3 rd, Vector3 a, Vector3 b, out float rayT)
        {
            Vector3 ab  = b - a;
            Vector3 oa  = a - ro;
            float rdab  = rd.Dot(ab);
            float oaab  = oa.Dot(ab);
            float oard  = oa.Dot(rd);
            float abab  = ab.Dot(ab);
            float denom = abab - rdab * rdab;

            float s = Mathf.Abs(denom) < 1e-8f
                ? 0f
                : Mathf.Clamp((oard * rdab - oaab) / denom, 0f, 1f);

            rayT = oard + s * rdab;
            if (rayT < 0f) { rayT = float.MaxValue; return float.MaxValue; }

            return (ro + rayT * rd - (a + s * ab)).Length();
        }
    }
}
