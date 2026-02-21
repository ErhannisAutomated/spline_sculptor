using Godot;
using SplineSculptor.Interaction;
using SplineSculptor.Model;
using SplineSculptor.Rendering;

namespace SplineSculptor.VR
{
    /// <summary>
    /// Desktop fallback: mouse-ray hover + left-click-drag of control point handles.
    /// Left-click on surface (no handle nearby) selects it.
    /// Right-click-drag orbits the camera.
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
        private SurfaceNode?        _selectedSurfaceNode;

        // Orbit state — free rotation (no up-vector lock, pitch/roll unrestricted)
        private bool    _orbiting    = false;
        private Basis   _orbitBasis  = Basis.Identity;
        private float   _orbitDist   = 3.0f;
        private Vector3 _orbitTarget = Vector3.Zero;

        // Avoid re-triggering selection every frame the button is held
        private bool _leftWasPressed = false;

        public override void _Ready()
        {
            _camera = GetViewport().GetCamera3D();
            if (_camera != null)
            {
                // Seed free-orbit state from the camera's current transform
                _orbitBasis = _camera.Basis;
                _orbitDist  = _camera.Position.Length();
            }
        }

        public override void _Input(InputEvent @event)
        {
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

                // Rotate around camera-local axes — no up-vector lock, pitch/roll unrestricted
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
            var ray       = _camera.ProjectRayNormal(mousePos);
            var rayOrigin = _camera.GlobalPosition;

            // ── Active drag ───────────────────────────────────────────────────────
            if (_dragHandle != null)
            {
                if (Input.IsMouseButtonPressed(MouseButton.Left))
                {
                    var hit = _dragPlane.IntersectsRay(rayOrigin, ray);
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

            // ── Handle hover (ray-sphere distance) ────────────────────────────────
            ControlPointHandle? closest    = null;
            float closestDist = 0.08f;
            float closestT    = float.MaxValue;

            foreach (var child in SceneRoot.GetChildren())
            {
                if (child is not PolysurfaceNode polyNode) continue;
                foreach (var handle in polyNode.AllHandles())
                {
                    float t = (handle.GlobalPosition - rayOrigin).Dot(ray);
                    if (t < 0) continue;
                    float dist = (handle.GlobalPosition - (rayOrigin + ray * t)).Length();
                    if (dist < closestDist && t < closestT)
                    {
                        closestDist = dist;
                        closestT    = t;
                        closest     = handle;
                    }
                }
            }

            if (_hoveredHandle != closest)
            {
                if (_hoveredHandle != null) _hoveredHandle.IsHovered = false;
                _hoveredHandle = closest;
                if (_hoveredHandle != null) _hoveredHandle.IsHovered = true;
            }

            bool leftDown = Input.IsMouseButtonPressed(MouseButton.Left);
            bool leftJustPressed = leftDown && !_leftWasPressed;
            _leftWasPressed = leftDown;

            // ── Start handle drag ─────────────────────────────────────────────────
            if (leftJustPressed && _hoveredHandle != null)
            {
                _dragHandle = _hoveredHandle;
                _dragHandle.OnGrabStart(this);
                _dragPlane  = new Plane(-ray, _dragHandle.GlobalPosition);
                var hit     = _dragPlane.IntersectsRay(rayOrigin, ray);
                _dragOffset = hit.HasValue ? hit.Value - _dragHandle.GlobalPosition : Vector3.Zero;
                return;
            }

            // ── Surface selection (click with no handle nearby) ───────────────────
            if (leftJustPressed && _hoveredHandle == null && !_orbiting)
                TrySelectSurface(rayOrigin, ray);
        }

        // ─── Surface selection via physics raycast ────────────────────────────────

        private void TrySelectSurface(Vector3 rayOrigin, Vector3 rayDir)
        {
            var spaceState = GetViewport().GetCamera3D()?.GetWorld3D().DirectSpaceState;
            if (spaceState == null) return;

            var rayParams = PhysicsRayQueryParameters3D.Create(
                rayOrigin, rayOrigin + rayDir * 500f);
            rayParams.CollideWithAreas  = false;
            rayParams.CollideWithBodies = true;

            var result = spaceState.IntersectRay(rayParams);

            if (result.Count > 0)
            {
                var collider   = result["collider"].As<Node>();
                var surfNode   = collider?.GetParent() as SurfaceNode;
                var polyNode   = surfNode?.GetParent() as PolysurfaceNode;

                if (surfNode != null && polyNode != null && surfNode.Surface != null)
                {
                    SetSelectedSurfaceNode(surfNode);
                    Selection?.SelectSurface(surfNode.Surface);
                    if (polyNode.Data != null)
                        Selection?.SelectPolysurface(polyNode.Data);
                    return;
                }
            }

            // Clicked empty space — deselect
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
    }
}
