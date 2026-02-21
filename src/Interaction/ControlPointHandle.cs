using Godot;
using SplineSculptor.Model;
using SplineSculptor.Model.Undo;
using SplineSculptor.Rendering;

namespace SplineSculptor.Interaction
{
    /// <summary>
    /// Node3D attached to each control point sphere.
    /// Implements IGrabTarget so ControllerHand can drag it.
    /// On drag end, wraps the move in a MoveControlPointCommand for undo/redo.
    ///
    /// Group-drag API (used by DesktopInteraction for multi-select):
    ///   StartGroupDrag / MoveGroupDrag / EndGroupDrag / TriggerConstraintEnforcement
    /// </summary>
    [GlobalClass]
    public partial class ControlPointHandle : Node3D, IGrabTarget
    {
        private SculptSurface? _surface;
        private Polysurface?   _polysurface;
        private int _u;
        private int _v;

        private Vector3 _dragStartPos;
        private bool    _isDragging = false;

        // Reference to the scene's undo stack (injected by PolysurfaceNode / Main)
        public static SculptScene? SceneRef { get; set; }

        public bool IsHovered  { get; set; } = false;
        public bool IsSelected { get; set; } = false;

        private MeshInstance3D? _meshInstance;

        public void Init(SculptSurface surface, int u, int v, Polysurface? polysurface = null)
        {
            _surface     = surface;
            _u           = u;
            _v           = v;
            _polysurface = polysurface;
        }

        public override void _Ready()
        {
            _meshInstance = GetChildOrNull<MeshInstance3D>(0);
        }

        public Vector3 GlobalGrabPosition => GlobalPosition;

        // ─── VR single-handle grab API ────────────────────────────────────────────

        public void OnGrabStart(Node grabber)
        {
            if (_surface == null) return;
            _dragStartPos = _surface.Geometry.ControlPoints[_u, _v];
            _isDragging   = true;
            GD.Print($"[Drag] Start CP[{_u},{_v}] at {_dragStartPos:F3}");

            if (GetParent()?.GetParent() is PolysurfaceNode polyNode)
                polyNode.BeginDrag();
        }

        public void OnGrabMove(Vector3 controllerWorldPos)
        {
            if (!_isDragging || _surface == null) return;
            _surface.ApplyControlPointMove(_u, _v, controllerWorldPos);
            GlobalPosition = controllerWorldPos;
        }

        public void OnGrabEnd(Node grabber)
        {
            if (!_isDragging || _surface == null) return;
            _isDragging = false;

            Vector3 newPos = _surface.Geometry.ControlPoints[_u, _v];

            if (SceneRef != null && newPos != _dragStartPos)
            {
                var cmd = new MoveControlPointCommand(
                    _surface, _u, _v, _dragStartPos, newPos, _polysurface);
                SceneRef.UndoStack.Execute(new AlreadyAppliedCommand(cmd));
            }

            float moved = newPos.DistanceTo(_dragStartPos);
            GD.Print($"[Drag] End   CP[{_u},{_v}] → {newPos:F3}  (Δ={moved:F3})");

            _polysurface?.EnforceConstraints(_surface);

            if (GetParent()?.GetParent() is PolysurfaceNode polyNode)
                polyNode.EndDrag();
        }

        // ─── Desktop multi-select group-drag API ──────────────────────────────────

        /// <summary>Record start position and switch parent polysurface to low-res preview.</summary>
        public void StartGroupDrag()
        {
            if (_surface == null) return;
            _dragStartPos = _surface.Geometry.ControlPoints[_u, _v];
            _isDragging   = true;
            if (GetParent()?.GetParent() is PolysurfaceNode pn)
                pn.BeginDrag();
        }

        /// <summary>Move this handle by delta from its drag-start position.</summary>
        public void MoveGroupDrag(Vector3 delta)
        {
            if (!_isDragging || _surface == null) return;
            var newPos = _dragStartPos + delta;
            _surface.ApplyControlPointMove(_u, _v, newPos);
            GlobalPosition = newPos;
        }

        /// <summary>
        /// Finish the drag. Returns data the caller needs to build a
        /// MultiMoveControlPointCommand. Switches back to high-res.
        /// </summary>
        public (SculptSurface? surf, int u, int v, Vector3 startPos, Vector3 endPos, Polysurface? poly)
            EndGroupDrag()
        {
            _isDragging = false;
            var endPos = _surface?.Geometry.ControlPoints[_u, _v] ?? _dragStartPos;
            if (GetParent()?.GetParent() is PolysurfaceNode pn)
                pn.EndDrag();
            return (_surface, _u, _v, _dragStartPos, endPos, _polysurface);
        }

        /// <summary>Run constraint enforcement after a group drag completes.</summary>
        public void TriggerConstraintEnforcement()
        {
            if (_surface != null)
                _polysurface?.EnforceConstraints(_surface);
        }

        // ─── Visual state ─────────────────────────────────────────────────────────

        public override void _Process(double delta)
        {
            if (_meshInstance == null || _meshInstance.MaterialOverride is not StandardMaterial3D mat)
                return;

            if (IsSelected && IsHovered)
            {
                // Selected + hovered: cyan
                mat.AlbedoColor = new Color(0.3f, 1f, 1f);
                mat.Emission    = new Color(0.1f, 0.6f, 0.6f);
            }
            else if (IsSelected)
            {
                // Selected only: green
                mat.AlbedoColor = new Color(0.3f, 1f, 0.4f);
                mat.Emission    = new Color(0.1f, 0.6f, 0.2f);
            }
            else if (IsHovered)
            {
                // Hovered only: bright yellow
                mat.AlbedoColor = new Color(1f, 1f, 0.3f);
                mat.Emission    = new Color(0.8f, 0.8f, 0.1f);
            }
            else
            {
                // Default: orange
                mat.AlbedoColor = new Color(1f, 0.8f, 0.2f);
                mat.Emission    = new Color(0.5f, 0.4f, 0.1f);
            }
        }
    }

    /// <summary>
    /// Wraps a command that has already been applied so that the initial Execute()
    /// is a no-op (the move happened interactively). Subsequent Execute() calls
    /// (i.e., redo) delegate to the inner command normally.
    /// </summary>
    internal class AlreadyAppliedCommand : ICommand
    {
        private readonly ICommand _inner;
        private bool _firstExecute = true;

        public string Description => _inner.Description;
        public AlreadyAppliedCommand(ICommand inner) => _inner = inner;

        public void Execute()
        {
            if (_firstExecute) { _firstExecute = false; return; }
            _inner.Execute(); // redo path
        }

        public void Undo() => _inner.Undo();
    }
}
