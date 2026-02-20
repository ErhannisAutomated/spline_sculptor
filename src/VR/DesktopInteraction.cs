using Godot;
using System.Collections.Generic;
using SplineSculptor.Interaction;
using SplineSculptor.Model;
using SplineSculptor.Model.Undo;
using SplineSculptor.Rendering;

namespace SplineSculptor.VR
{
    /// <summary>
    /// Desktop fallback: mouse-ray hover + left-click-drag of control point handles.
    /// Right-click-drag orbits the camera around the scene origin.
    /// </summary>
    [GlobalClass]
    public partial class DesktopInteraction : Node
    {
        public SculptScene? Scene    { get; set; }
        public Node3D?      SceneRoot { get; set; }

        private Camera3D? _camera;
        private ControlPointHandle? _hoveredHandle;
        private ControlPointHandle? _dragHandle;
        private Plane  _dragPlane;
        private Vector3 _dragOffset;

        // Orbit state
        private bool  _orbiting = false;
        private Vector2 _orbitStart;
        private Vector3 _cameraOrbitStart;
        private float _orbitYaw   = 0;
        private float _orbitPitch = -0.3f;
        private float _orbitDist  = 3.0f;

        public override void _Ready()
        {
            _camera = GetViewport().GetCamera3D();
        }

        public override void _Input(InputEvent @event)
        {
            // Orbit: right mouse drag
            if (@event is InputEventMouseButton mb)
            {
                if (mb.ButtonIndex == MouseButton.Right)
                    _orbiting = mb.Pressed;
                if (mb.ButtonIndex == MouseButton.WheelUp)
                    _orbitDist = Mathf.Max(0.5f, _orbitDist - 0.3f);
                if (mb.ButtonIndex == MouseButton.WheelDown)
                    _orbitDist += 0.3f;
            }

            if (@event is InputEventMouseMotion mm && _orbiting && _camera != null)
            {
                _orbitYaw   -= mm.Relative.X * 0.005f;
                _orbitPitch -= mm.Relative.Y * 0.005f;
                _orbitPitch = Mathf.Clamp(_orbitPitch, -1.4f, 1.4f);

                float x = _orbitDist * Mathf.Cos(_orbitPitch) * Mathf.Sin(_orbitYaw);
                float y = _orbitDist * Mathf.Sin(_orbitPitch);
                float z = _orbitDist * Mathf.Cos(_orbitPitch) * Mathf.Cos(_orbitYaw);
                _camera.Position = new Vector3(x, y, z);
                _camera.LookAt(Vector3.Zero, Vector3.Up);
            }
        }

        public override void _Process(double delta)
        {
            if (_camera == null || SceneRoot == null) return;

            var mousePos = GetViewport().GetMousePosition();
            var ray = _camera.ProjectRayNormal(mousePos);
            var rayOrigin = _camera.GlobalPosition;

            if (_dragHandle != null)
            {
                // While dragging: move handle along its drag plane
                if (Input.IsMouseButtonPressed(MouseButton.Left))
                {
                    var hit = _dragPlane.IntersectsRay(rayOrigin, ray);
                    if (hit.HasValue)
                    {
                        _dragHandle.OnGrabMove(hit.Value - _dragOffset);
                    }
                }
                else
                {
                    _dragHandle.OnGrabEnd(this);
                    _dragHandle = null;
                }
                return;
            }

            // Hover detection via raycast against handle positions (simple distance test)
            ControlPointHandle? closest = null;
            float closestDist = 0.08f; // max hover distance
            float closestT = float.MaxValue;

            foreach (var child in SceneRoot.GetChildren())
            {
                if (child is not PolysurfaceNode polyNode) continue;
                foreach (var handle in polyNode.AllHandles())
                {
                    // Distance from ray to handle centre
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

            // Start drag on left-click
            if (Input.IsMouseButtonPressed(MouseButton.Left) && _hoveredHandle != null)
            {
                _dragHandle = _hoveredHandle;
                _dragHandle.OnGrabStart(this);

                // Drag plane: camera-facing plane through the handle
                _dragPlane = new Plane(-ray, _dragHandle.GlobalPosition);
                var hit = _dragPlane.IntersectsRay(rayOrigin, ray);
                _dragOffset = hit.HasValue ? hit.Value - _dragHandle.GlobalPosition : Vector3.Zero;
            }
        }
    }
}
