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
    /// surface and edge selection, right-click orbit camera.
    ///
    /// Tool switching (keyboard):
    ///   Q = Auto   (point > edge > surface priority)
    ///   W = Point  (only control points)
    ///   E = Edge   (only surface boundary edges)
    ///   R = Surface (only surfaces)
    ///
    /// Selection modifiers:
    ///   (none)       = Replace
    ///   Shift        = Add
    ///   Ctrl         = XOR (toggle)
    ///   Ctrl+Shift   = Remove
    ///
    /// Double-click on a surface moves the orbit pivot to the hit point.
    /// </summary>
    [GlobalClass]
    public partial class DesktopInteraction : Node
    {
        public SculptScene?      Scene         { get; set; }
        public Node3D?           SceneRoot     { get; set; }
        public SelectionManager? Selection     { get; set; }

        private Camera3D?           _camera;
        private ControlPointHandle? _hoveredHandle;

        // Surface selection tracking
        private SurfaceNode? _selectedSurfaceNode;

        // Edge hover tracking
        private SurfaceNode? _edgeHoverSurfNode;
        private SurfaceEdge  _edgeHoverEdge;

        // Edge selection visual state (mirrors SelectionManager.SelectedEdges)
        private readonly List<(SurfaceNode node, SurfaceEdge edge)> _selectedEdgeNodes = new();

        // Tool state
        private SelectionTool _currentTool = SelectionTool.Auto;

        // Orbit state — free rotation (no up-vector lock)
        private bool    _orbiting    = false;
        private Basis   _orbitBasis  = Basis.Identity;
        private float   _orbitDist   = 3.0f;
        private Vector3 _orbitTarget = Vector3.Zero;

        // ─── Click / drag state machine ───────────────────────────────────────────

        private enum DragState { Idle, Threshold, DraggingHandles }

        private DragState         _dragState      = DragState.Idle;
        private Vector2           _mouseDownPos;
        private SelectionModifier _clickModifier;
        private const float       DragPixelThresh = 6f;

        // Snapshot of what was under the cursor when left button went down
        private ControlPointHandle? _clickTargetHandle;
        private SurfaceNode?        _clickTargetEdgeSurf;
        private SurfaceEdge         _clickTargetEdge;

        // Multi-drag state
        private readonly List<ControlPointHandle> _multiDragHandles = new();
        private Plane   _dragPlane;
        private Vector3 _dragOriginHit;

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
                // ── Double-click: move orbit pivot (check before regular press) ────
                if (mb.ButtonIndex == MouseButton.Left && mb.Pressed && mb.DoubleClick)
                {
                    HandleDoubleClick(mb.Position);
                    return;
                }

                // ── Orbit / zoom ──────────────────────────────────────────────────
                if (mb.ButtonIndex == MouseButton.Right)
                    _orbiting = mb.Pressed;

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

                // ── Left mouse down: start threshold watch ────────────────────────
                if (mb.ButtonIndex == MouseButton.Left && mb.Pressed)
                {
                    _mouseDownPos        = mb.Position;
                    _clickModifier       = GetModifier();
                    _clickTargetHandle   = _hoveredHandle;
                    _clickTargetEdgeSurf = _edgeHoverSurfNode;
                    _clickTargetEdge     = _edgeHoverEdge;
                    _dragState           = DragState.Threshold;
                    return;
                }

                // ── Left mouse up: commit click or finalize drag ───────────────────
                if (mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
                {
                    if (_dragState == DragState.Threshold)        HandleClick();
                    if (_dragState == DragState.DraggingHandles)  FinalizeDrag();
                    _dragState = DragState.Idle;
                    return;
                }
            }

            // ── Mouse motion ──────────────────────────────────────────────────────
            if (@event is InputEventMouseMotion mm)
            {
                // Orbit rotation
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

                // Drag threshold check
                if (_dragState == DragState.Threshold)
                {
                    if ((mm.Position - _mouseDownPos).Length() >= DragPixelThresh)
                        TryStartDrag();
                }
            }
        }

        public override void _Process(double delta)
        {
            if (_camera == null || SceneRoot == null) return;

            var mousePos  = GetViewport().GetMousePosition();
            var rayDir    = _camera.ProjectRayNormal(mousePos);
            var rayOrigin = _camera.GlobalPosition;

            // ── Active multi-drag: move all selected handles ───────────────────────
            if (_dragState == DragState.DraggingHandles && _multiDragHandles.Count > 0)
            {
                var hit = _dragPlane.IntersectsRay(rayOrigin, rayDir);
                if (hit.HasValue)
                {
                    var moveDelta = hit.Value - _dragOriginHit;
                    foreach (var h in _multiDragHandles)
                        h.MoveGroupDrag(moveDelta);
                }
                return; // skip hover detection while dragging
            }

            // ── Hover detection ───────────────────────────────────────────────────

            bool wantHandles = _currentTool is SelectionTool.Auto or SelectionTool.Point;
            bool wantEdges   = _currentTool is SelectionTool.Auto or SelectionTool.Edge;

            // Find closest handle
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

            // Find closest edge — in Auto mode only when no handle is hovered
            SurfaceNode? edgeNode = null;
            SurfaceEdge  edgeEdge = SurfaceEdge.UMin;

            if (wantEdges && (_currentTool == SelectionTool.Edge || closestHandle == null))
                (edgeNode, edgeEdge) = FindHoveredEdge(rayOrigin, rayDir);

            UpdateHandleHover(closestHandle);
            UpdateEdgeHover(edgeNode, edgeEdge);
        }

        // ─── Modifier key helper ──────────────────────────────────────────────────

        private static SelectionModifier GetModifier()
        {
            bool shift = Input.IsKeyPressed(Key.Shift);
            bool ctrl  = Input.IsKeyPressed(Key.Ctrl);
            if (ctrl && shift) return SelectionModifier.Remove;
            if (ctrl)          return SelectionModifier.XOR;
            if (shift)         return SelectionModifier.Add;
            return SelectionModifier.Replace;
        }

        // ─── Tool switching ───────────────────────────────────────────────────────

        private void SetTool(SelectionTool tool)
        {
            _currentTool = tool;
            GD.Print($"[Tool] {tool}  (Q=Auto  W=Point  E=Edge  R=Surface)");
        }

        // ─── Click: selection (no drag threshold exceeded) ────────────────────────

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
                // Empty-space click with no modifier: try surface, else clear
                TrySelectSurface(rayOrigin, rayDir);
            }
        }

        // ─── Drag start ───────────────────────────────────────────────────────────

        private void TryStartDrag()
        {
            if (_camera == null || _clickTargetHandle == null) return; // future: lasso

            // If the clicked handle isn't selected yet, apply the modifier to select it
            bool alreadySelected =
                Selection?.SelectedHandles.Contains(_clickTargetHandle) ?? false;
            if (!alreadySelected)
                Selection?.ModifyHandleSelection(_clickTargetHandle, _clickModifier);

            if (Selection == null || Selection.SelectedHandles.Count == 0) return;

            // Build a drag plane perpendicular to the view ray at the primary handle
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

        // ─── Drag finalize ────────────────────────────────────────────────────────

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
            SurfaceNode? bestNode  = null;
            SurfaceEdge  bestEdge  = SurfaceEdge.UMin;
            float        bestDist  = 0.10f;
            float        bestT     = float.MaxValue;

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
                                bestDist  = dist;
                                bestT     = t;
                                bestNode  = surfNode;
                                bestEdge  = edge;
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

            // Clicked empty space — deselect everything
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

        /// <summary>
        /// Returns the closest distance between a ray (origin + t*dir, t≥0) and
        /// a finite segment [a, b]. Also outputs the ray parameter t at closest point.
        /// Returns float.MaxValue if the segment is entirely behind the ray.
        /// </summary>
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
