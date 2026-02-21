using Godot;
using SplineSculptor.Math;
using SplineSculptor.Model;
using SplineSculptor.Model.Undo;
using SplineSculptor.Interaction;
using SplineSculptor.Rendering;
using SplineSculptor.IO;

namespace SplineSculptor.VR
{
    /// <summary>
    /// Root manager. Initialises OpenXR (or desktop fallback), builds the VR rig
    /// and initial scene entirely in code — no child scene instancing required.
    ///
    /// Desktop keyboard shortcuts:
    ///   Ctrl+Z / Ctrl+Y  — undo / redo
    ///   P                — add a new standalone Bezier patch to the scene
    ///   1 / 2 / 3 / 4   — attach patch to UMin / UMax / VMin / VMax of selected surface
    ///   Delete           — delete the selected surface
    ///   Ctrl+S           — save scene to .3dm
    ///   Ctrl+O           — load scene from .3dm
    /// </summary>
    [GlobalClass]
    public partial class VRManager : Node3D
    {
        private SculptScene         _scene     = new();
        private Node3D?             _sceneRoot;
        private DesktopInteraction? _desktop;
        private SelectionManager    _selection = new();

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
                Selection = _selection,
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
                AttachOrSpawn();
            else if (!ctrl && key.Keycode == Key.Key1)
                AttachToSelected(SurfaceEdge.UMin);
            else if (!ctrl && key.Keycode == Key.Key2)
                AttachToSelected(SurfaceEdge.UMax);
            else if (!ctrl && key.Keycode == Key.Key3)
                AttachToSelected(SurfaceEdge.VMin);
            else if (!ctrl && key.Keycode == Key.Key4)
                AttachToSelected(SurfaceEdge.VMax);
            else if (!ctrl && key.Keycode == Key.Delete)
                DeleteSelected();
            else if (ctrl && key.Keycode == Key.S)
                SaveScene();
            else if (ctrl && key.Keycode == Key.O)
                LoadScene();
        }

        // ─── Surface operations ───────────────────────────────────────────────────

        /// <summary>
        /// If an edge is selected, attach to it. Otherwise spawn a standalone patch.
        /// </summary>
        private void AttachOrSpawn()
        {
            if (_selection.SelectedEdge.HasValue)
            {
                var er  = _selection.SelectedEdge.Value;
                var cmd = new AttachPatchCommand(er.Poly, er.Surface, er.Edge);
                _scene.UndoStack.Execute(cmd);
                GD.Print($"[VRManager] Attached patch to {er.Edge} of '{er.Poly.Name}'.");
            }
            else
                SpawnPatch($"Patch {_scene.Polysurfaces.Count + 1}");
        }

        private void AttachToSelected(SurfaceEdge edge)
        {
            var surf = GetSelectedSurface(out var poly);
            if (surf == null || poly == null)
            {
                GD.Print("[VRManager] No surface selected — click a surface first.");
                return;
            }

            var cmd = new AttachPatchCommand(poly, surf, edge);
            _scene.UndoStack.Execute(cmd);
            GD.Print($"[VRManager] Attached patch to {edge} of '{poly.Name}'.");
        }

        private void DeleteSelected()
        {
            var surf = GetSelectedSurface(out var poly);
            if (surf == null || poly == null)
            {
                GD.Print("[VRManager] No surface selected.");
                return;
            }

            _selection.ClearAll();
            var cmd = new DeleteSurfaceCommand(poly, surf);
            _scene.UndoStack.Execute(cmd);
            GD.Print($"[VRManager] Deleted surface from '{poly.Name}'.");
        }

        private SculptSurface? GetSelectedSurface(out Polysurface? poly)
        {
            poly = null;
            SculptSurface? surf = null;
            foreach (var s in _selection.SelectedSurfaces) { surf = s; break; }
            if (surf == null) return null;

            foreach (var p in _scene.Polysurfaces)
            {
                if (p.Surfaces.Contains(surf))
                {
                    poly = p;
                    return surf;
                }
            }
            return null;
        }

        // ─── Save / Load ──────────────────────────────────────────────────────────

        private void SaveScene()
        {
            string path = _scene.FilePath
                ?? ProjectSettings.GlobalizePath("user://scene.3dm");
            Rhino3dmIO.Save(_scene, path);
            _scene.FilePath = path;
        }

        private void LoadScene()
        {
            string path = _scene.FilePath
                ?? ProjectSettings.GlobalizePath("user://scene.3dm");

            var loaded = Rhino3dmIO.Load(path);
            if (loaded == null) return;

            // Remove all existing PolysurfaceNodes from the scene tree
            if (_sceneRoot != null)
            {
                foreach (var child in _sceneRoot.GetChildren())
                    child.QueueFree();
            }

            _scene = loaded;
            ControlPointHandle.SceneRef = _scene;
            _selection.ClearAll();

            if (_desktop != null)
            {
                _desktop.Scene     = _scene;
                _desktop.SceneRoot = _sceneRoot;
                _desktop.Selection = _selection;
            }

            // Rebuild Godot nodes for every polysurface in the loaded scene
            foreach (var poly in _scene.Polysurfaces)
            {
                var polyNode = new PolysurfaceNode();
                _sceneRoot!.AddChild(polyNode);
                polyNode.Init(poly);
            }

            GD.Print("[VRManager] Scene loaded and nodes rebuilt.");
        }
    }
}
