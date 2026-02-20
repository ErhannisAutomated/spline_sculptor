using Godot;
using SplineSculptor.Math;
using SplineSculptor.Model;
using SplineSculptor.Interaction;
using SplineSculptor.Rendering;

namespace SplineSculptor.VR
{
    /// <summary>
    /// Root manager. Initialises OpenXR (or desktop fallback), builds the VR rig
    /// and initial scene entirely in code — no child scene instancing required.
    ///
    /// Desktop keyboard shortcuts:
    ///   Ctrl+Z / Ctrl+Y  — undo / redo
    ///   P                — add a new standalone Bezier patch to the scene
    /// </summary>
    [GlobalClass]
    public partial class VRManager : Node3D
    {
        private SculptScene _scene    = new();
        private Node3D?     _sceneRoot;
        private DesktopInteraction? _desktop;

        public SculptScene Scene => _scene;

        public override void _Ready()
        {
            // SceneRoot is declared in Main.tscn
            _sceneRoot = GetNodeOrNull<Node3D>("SceneRoot");
            if (_sceneRoot == null)
            {
                GD.PrintErr("[VRManager] SceneRoot node not found in scene tree.");
                return;
            }

            ControlPointHandle.SceneRef = _scene;

            bool vrAvailable = InitXR();
            if (vrAvailable)
                BuildVRRig();
            else
                EnableDesktopFallback();

            SpawnPatch("Default Patch");
        }

        // ─── XR init ─────────────────────────────────────────────────────────────

        private bool InitXR()
        {
            var xrInterface = XRServer.FindInterface("OpenXR");
            if (xrInterface == null)
            {
                GD.Print("[VRManager] OpenXR interface not found — desktop mode.");
                return false;
            }

            if (xrInterface.Initialize())
            {
                GD.Print("[VRManager] OpenXR initialised.");
                GetViewport().UseXR = true;
                return true;
            }

            GD.Print("[VRManager] OpenXR init failed — desktop mode.");
            return false;
        }

        // ─── VR rig (created in code, no scene file needed) ──────────────────────

        private void BuildVRRig()
        {
            var origin = new XROrigin3D { Name = "XROrigin3D" };
            AddChild(origin);

            var camera = new XRCamera3D { Name = "XRCamera3D" };
            origin.AddChild(camera);

            var leftCtrl = new XRController3D { Name = "LeftController", Tracker = "left_hand" };
            origin.AddChild(leftCtrl);
            var leftHand = new ControllerHand { Name = "LeftHand" };
            leftCtrl.AddChild(leftHand);
            leftHand.SetSceneRoot(_sceneRoot!);

            var rightCtrl = new XRController3D { Name = "RightController", Tracker = "right_hand" };
            origin.AddChild(rightCtrl);
            var rightHand = new ControllerHand { Name = "RightHand" };
            rightCtrl.AddChild(rightHand);
            rightHand.SetSceneRoot(_sceneRoot!);

            leftHand.OtherHand  = rightHand;
            rightHand.OtherHand = leftHand;

            // World navigator watches both controllers directly
            var nav = new WorldNavigator(_sceneRoot!, leftCtrl, rightCtrl);
            origin.AddChild(nav);
        }

        // ─── Desktop fallback ─────────────────────────────────────────────────────

        private void EnableDesktopFallback()
        {
            var cam = new Camera3D
            {
                Name     = "DesktopCamera",
                Position = new Vector3(0, 1.2f, 2.5f),
            };
            AddChild(cam);
            cam.LookAt(Vector3.Zero, Vector3.Up);
            cam.MakeCurrent();

            _desktop = new DesktopInteraction
            {
                Scene     = _scene,
                SceneRoot = _sceneRoot,
            };
            AddChild(_desktop);
        }

        // ─── Scene building ───────────────────────────────────────────────────────

        /// <summary>Spawn a new standalone Bezier patch Polysurface and add its node to the scene.</summary>
        private void SpawnPatch(string name)
        {
            if (_sceneRoot == null) return;

            var poly = new Polysurface { Name = name };
            var surf = new SculptSurface(NurbsSurface.CreateBezierPatch());
            poly.AddSurface(surf);
            _scene.InternalAdd(poly);

            var polyNode = new PolysurfaceNode();
            _sceneRoot.AddChild(polyNode);
            polyNode.Init(poly);

            // Make sure DesktopInteraction knows about the new node
            if (_desktop != null)
                _desktop.SceneRoot = _sceneRoot;

            GD.Print($"[VRManager] Spawned patch '{name}'. Total patches: {_scene.Polysurfaces.Count}");
        }

        // ─── Input ────────────────────────────────────────────────────────────────

        public override void _Input(InputEvent @event)
        {
            if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

            bool ctrl = key.CtrlPressed;

            if (ctrl && key.Keycode == Key.Z && !key.ShiftPressed)
                _scene.UndoStack.Undo();
            else if (ctrl && (key.Keycode == Key.Y || (key.Keycode == Key.Z && key.ShiftPressed)))
                _scene.UndoStack.Redo();
            else if (!ctrl && key.Keycode == Key.P)
                SpawnPatch($"Patch {_scene.Polysurfaces.Count + 1}");
        }
    }
}
