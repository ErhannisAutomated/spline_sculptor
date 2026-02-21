using Godot;
using SplineSculptor.Interaction;
using SplineSculptor.Model;
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
    /// </summary>
    [GlobalClass]
    public partial class DesktopInteraction : Node
    {
        public SculptScene?      Scene         { get; set; }
        public Node3D?           SceneRoot     { get; set; }
        public SelectionManager? Selection     { get; set; }

        private Camera3D?           _camera;
        private ControlPointHandle? _hoveredHandle;
        private ControlPointHandle? _dragHandle;
        private Plane               _dragPlane;
        private Vector3             _dragOffset;

        // Surface selection tracking
        private SurfaceNode? _selectedSurfaceNode;

        // Edge hover / selection tracking
        private SurfaceNode? _edgeHoverSurfNode;
        private SurfaceEdge  _edgeHoverEdge;
        private SurfaceNode? _selectedEdgeSurfNode;

        // Tool state
        private SelectionTool _currentTool = SelectionTool.Auto;

        // Orbit state — free rotation (no up-vector lock)
        private bool    _orbiting    = false;
        private Basis   _orbitBasis  = Basis.Identity;
        private float   _orbitDist   = 3.0f;
        private Vector3 _orbitTarget = Vector3.Zero;

        // Detect just-pressed vs held
        private bool _leftWasPressed = false;

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

            // ── Orbit / zoom ──────────────────────────────────────────────────────
            if (@event is InputEventMouseButton mb)
            {
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
            }

            if (@event is InputEventMouseMotion mm && _orbiting && _camera != null)
            {
                float dx = mm.Relative.X * 0.005f;
                float dy = mm.Relative.Y * 0.005f;

                _orbitBasis = _orbitBasis.Rotated(_orbitBasis.Y.Normalized(), -dx);
                _orbitBasis = _orbitBasis.Rotated(_orbitBasis.X.Normalized(), -dy);
                _orbitBasis = _orbitBasis.Orthonormalized();

                _camera.Position = _orbitTarget + _orbitBasis.Z * _orbitDist;
                _camera.Basis    = _orbitBasis;
            }
        }

        public override void _Process(double delta)
        {
            if (_camera == null || SceneRoot == null) return;

            var mousePos  = GetViewport().GetMousePosition();
            var rayDir    = _camera.ProjectRayNormal(mousePos);
            var rayOrigin = _camera.GlobalPosition;

            // ── Active drag ───────────────────────────────────────────────────────
            if (_dragHandle != null)
            {
                if (Input.IsMouseButtonPressed(MouseButton.Left))
                {
                    var hit = _dragPlane.IntersectsRay(rayOrigin, rayDir);
                    if (hit.HasValue)
                        _dragHandle.OnGrabMove(hit.Value - _dragOffset);
                }
                else
                {
                    _dragHandle.OnGrabEnd(this);
                    _dragHandle = null;
                }
                _leftWasPressed = true;
                return;
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

            // Update hover visuals
            UpdateHandleHover(closestHandle);
            UpdateEdgeHover(edgeNode, edgeEdge);

            // ── Click detection ───────────────────────────────────────────────────
            bool leftDown       = Input.IsMouseButtonPressed(MouseButton.Left);
            bool leftJustPressed = leftDown && !_leftWasPressed;
            _leftWasPressed = leftDown;

            if (!leftJustPressed || _orbiting) return;

            switch (_currentTool)
            {
                case SelectionTool.Auto:
                    if (_hoveredHandle != null)
                        StartDrag(rayOrigin, rayDir);
                    else if (_edgeHoverSurfNode != null)
                        SelectHoveredEdge();
                    else
                        TrySelectSurface(rayOrigin, rayDir);
                    break;

                case SelectionTool.Point:
                    if (_hoveredHandle != null)
                        StartDrag(rayOrigin, rayDir);
                    break;

                case SelectionTool.Edge:
                    if (_edgeHoverSurfNode != null)
                        SelectHoveredEdge();
                    else
                        ClearEdgeSelection();
                    break;

                case SelectionTool.Surface:
                    TrySelectSurface(rayOrigin, rayDir);
                    break;
            }
        }

        // ─── Tool switching ───────────────────────────────────────────────────────

        private void SetTool(SelectionTool tool)
        {
            _currentTool = tool;
            GD.Print($"[Tool] {tool}  (Q=Auto  W=Point  E=Edge  R=Surface)");
        }

        // ─── Handle drag ──────────────────────────────────────────────────────────

        private void StartDrag(Vector3 rayOrigin, Vector3 rayDir)
        {
            if (_hoveredHandle == null) return;
            _dragHandle = _hoveredHandle;
            _dragHandle.OnGrabStart(this);
            _dragPlane  = new Plane(-rayDir, _dragHandle.GlobalPosition);
            var hit     = _dragPlane.IntersectsRay(rayOrigin, rayDir);
            _dragOffset = hit.HasValue ? hit.Value - _dragHandle.GlobalPosition : Vector3.Zero;
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

        private (SurfaceNode? node, SurfaceEdge edge) FindHoveredEdge(Vector3 origin, Vector3 dir)
        {
            SurfaceNode? bestNode  = null;
            SurfaceEdge  bestEdge  = SurfaceEdge.UMin;
            float        bestDist  = 0.10f;   // world-space threshold
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

        // ─── Edge selection ───────────────────────────────────────────────────────

        private void SelectHoveredEdge()
        {
            if (_edgeHoverSurfNode == null) return;
            var polyNode = _edgeHoverSurfNode.GetParent() as PolysurfaceNode;
            if (polyNode?.Data == null || _edgeHoverSurfNode.Surface == null) return;

            // Clear old selection visual
            if (_selectedEdgeSurfNode != null && _selectedEdgeSurfNode != _edgeHoverSurfNode)
                _selectedEdgeSurfNode.SetSelectedEdge(null);

            _selectedEdgeSurfNode = _edgeHoverSurfNode;
            _selectedEdgeSurfNode.SetSelectedEdge(_edgeHoverEdge);

            var edgeRef = new EdgeRef(_edgeHoverSurfNode.Surface, polyNode.Data, _edgeHoverEdge);
            Selection?.SelectEdge(edgeRef);

            // Also highlight the owning surface for context
            SetSelectedSurfaceNode(_edgeHoverSurfNode);
            Selection?.SelectSurface(_edgeHoverSurfNode.Surface);

            GD.Print($"[Select] Edge {_edgeHoverEdge} on '{polyNode.Data.Name}'");
        }

        private void ClearEdgeSelection()
        {
            _selectedEdgeSurfNode?.SetSelectedEdge(null);
            _selectedEdgeSurfNode = null;
            Selection?.DeselectEdge();
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
                    return;
                }
            }

            // Clicked empty space — deselect everything
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
            Vector3 ab   = b - a;
            Vector3 oa   = a - ro;
            float rdab   = rd.Dot(ab);
            float oaab   = oa.Dot(ab);
            float oard   = oa.Dot(rd);
            float abab   = ab.Dot(ab);
            float denom  = abab - rdab * rdab;

            float s = Mathf.Abs(denom) < 1e-8f
                ? 0f
                : Mathf.Clamp((oard * rdab - oaab) / denom, 0f, 1f);

            rayT = oard + s * rdab;
            if (rayT < 0f) { rayT = float.MaxValue; return float.MaxValue; }

            return (ro + rayT * rd - (a + s * ab)).Length();
        }
    }
}
