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
    /// </summary>
    [GlobalClass]
    public partial class ControlPointHandle : Node3D, IGrabTarget
    {
        private SculptSurface? _surface;
        private Polysurface? _polysurface;
        private int _u;
        private int _v;

        private Vector3 _dragStartPos;
        private bool _isDragging = false;

        // Reference to the scene's undo stack (injected by PolysurfaceNode / Main)
        public static SculptScene? SceneRef { get; set; }

        public bool IsHovered { get; set; } = false;

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

        public void OnGrabStart(Node grabber)
        {
            if (_surface == null) return;
            _dragStartPos = _surface.Geometry.ControlPoints[_u, _v];
            _isDragging = true;
            GD.Print($"[Drag] Start CP[{_u},{_v}] at {_dragStartPos:F3}");

            // Switch parent PolysurfaceNode to low-res preview
            if (GetParent()?.GetParent() is Rendering.PolysurfaceNode polyNode)
                polyNode.BeginDrag();
        }

        public void OnGrabMove(Vector3 controllerWorldPos)
        {
            if (!_isDragging || _surface == null) return;

            // Move to controller position, realtime (no undo yet)
            _surface.ApplyControlPointMove(_u, _v, controllerWorldPos);
            GlobalPosition = controllerWorldPos;
        }

        public void OnGrabEnd(Node grabber)
        {
            if (!_isDragging || _surface == null) return;
            _isDragging = false;

            Vector3 newPos = _surface.Geometry.ControlPoints[_u, _v];

            // Record undo command
            if (SceneRef != null && newPos != _dragStartPos)
            {
                var cmd = new MoveControlPointCommand(
                    _surface, _u, _v, _dragStartPos, newPos, _polysurface);
                // Execute without re-applying (position already moved); push directly.
                SceneRef.UndoStack.Execute(new AlreadyAppliedCommand(cmd));
            }

            float moved = newPos.DistanceTo(_dragStartPos);
            GD.Print($"[Drag] End   CP[{_u},{_v}] → {newPos:F3}  (Δ={moved:F3})");

            // AlreadyAppliedCommand.Execute() is a no-op on first call, so EnforceConstraints
            // was never run for the live drag. Call it explicitly now.
            _polysurface?.EnforceConstraints(_surface);

            // Switch back to high-res
            if (GetParent()?.GetParent() is Rendering.PolysurfaceNode polyNode)
                polyNode.EndDrag();
        }

        public override void _Process(double delta)
        {
            // Visual feedback for hover state
            if (_meshInstance != null && _meshInstance.MaterialOverride is StandardMaterial3D mat)
            {
                if (IsHovered)
                {
                    mat.AlbedoColor = new Color(1f, 1f, 0.3f);
                    mat.Emission    = new Color(0.8f, 0.8f, 0.1f);
                }
                else
                {
                    mat.AlbedoColor = new Color(1f, 0.8f, 0.2f);
                    mat.Emission    = new Color(0.5f, 0.4f, 0.1f);
                }
            }
        }
    }

    /// <summary>
    /// Wraps a command that has already been applied so that Execute() is a no-op.
    /// Used when a drag has already been applied interactively.
    /// </summary>
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
