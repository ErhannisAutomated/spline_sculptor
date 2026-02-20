using Godot;
using SplineSculptor.Math;
using SplineSculptor.Model;
using SplineSculptor.Interaction;

namespace SplineSculptor.VR
{
    /// <summary>
    /// Root manager for the scene. Handles:
    ///   - OpenXR initialisation (or desktop fallback)
    ///   - Creating the initial scene (default Bezier patch polysurface)
    ///   - Undo/redo keyboard input (desktop) and menu button (VR)
    /// </summary>
    [GlobalClass]
    public partial class VRManager : Node3D
    {
        [Export] public NodePath VRRigPath       { get; set; } = "VRRig";
        [Export] public NodePath SceneRootPath   { get; set; } = "SceneRoot";
        [Export] public NodePath DesktopCamPath  { get; set; } = "DesktopCamera";

        private SculptScene _scene = new();
        private Node3D?     _sceneRoot;

        // Exposed so ControlPointHandle can reach the scene's undo stack
        public SculptScene Scene => _scene;

        public override void _Ready()
        {
            _sceneRoot = GetNodeOrNull<Node3D>(SceneRootPath);

            // Wire global scene reference for ControlPointHandles
            ControlPointHandle.SceneRef = _scene;

            bool vrAvailable = InitXR();
            if (!vrAvailable)
                EnableDesktopFallback();

            // Create a default polysurface with a single Bezier patch
            BuildDefaultScene();
        }

        private bool InitXR()
        {
            var xrInterface = XRServer.FindInterface("OpenXR");
            if (xrInterface == null || !xrInterface.IsInitialized())
            {
                GD.Print("[VRManager] No OpenXR interface found — using desktop mode.");
                return false;
            }

            if (xrInterface.Initialize())
            {
                GD.Print("[VRManager] OpenXR initialised.");
                GetViewport().UseXR = true;
                return true;
            }

            GD.PrintErr("[VRManager] OpenXR initialisation failed.");
            return false;
        }

        private void EnableDesktopFallback()
        {
            GetNodeOrNull<Node3D>(VRRigPath)?.Hide();

            var cam = GetNodeOrNull<Camera3D>(DesktopCamPath);
            if (cam == null)
            {
                cam = new Camera3D
                {
                    Position = new Vector3(0, 1.5f, 3f),
                };
                cam.LookAt(Vector3.Zero, Vector3.Up);
                AddChild(cam);
            }
            cam.MakeCurrent();

            // Add desktop mouse interaction
            var desktop = new DesktopInteraction();
            desktop.Scene = _scene;
            desktop.SceneRoot = _sceneRoot;
            AddChild(desktop);
        }

        private void BuildDefaultScene()
        {
            if (_sceneRoot == null) return;

            var poly = new Model.Polysurface { Name = "Default Patch" };
            var surf = new Model.SculptSurface(Math.NurbsSurface.CreateBezierPatch());
            poly.AddSurface(surf);
            _scene.InternalAdd(poly);

            var polyNode = new Rendering.PolysurfaceNode();
            _sceneRoot.AddChild(polyNode);
            polyNode.Init(poly);
        }

        public override void _Input(InputEvent @event)
        {
            // Desktop keyboard undo/redo
            if (@event is InputEventKey key && key.Pressed && !key.Echo)
            {
                bool ctrl = key.CtrlPressed;
                if (ctrl && key.Keycode == Key.Z && !key.ShiftPressed)
                    _scene.UndoStack.Undo();
                else if (ctrl && (key.Keycode == Key.Y || (key.Keycode == Key.Z && key.ShiftPressed)))
                    _scene.UndoStack.Redo();
            }
        }
    }
}
