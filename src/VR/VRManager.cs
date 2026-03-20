using System;
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
	/// VR controls:
	///   RIGHT trigger   — grab nearest control-point handle
	///   RIGHT trackpad  — radial tool menu (Up=Auto Right=Point Down=Edge Left=Surface)
	///   RIGHT menu btn  — Redo
	///   LEFT trigger    — Undo
	///   LEFT trackpad   — radial ops menu (Up=Attach Right=G1 Down=Delete Left=Save)
	///   LEFT menu btn   — Undo
	///   BOTH grips      — two-hand pan / scale / rotate scene
	///
	/// Desktop keyboard shortcuts:
	///   Ctrl+Z / Ctrl+Y  — undo / redo
	///   P                — add a new standalone Bezier patch to the scene
	///   1 / 2 / 3 / 4   — attach patch to UMin / UMax / VMin / VMax of selected surface
	///   G                — toggle G0/G1 continuity on selected edge(s)
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

		// Kept so TrackerAdded can update them at runtime
		private XRController3D? _leftCtrl;
		private XRController3D? _rightCtrl;

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

			// Subscribe before anything XR-related — TrackerAdded can fire during Initialize()
			XRServer.TrackerAdded += OnTrackerAdded;

			// Build VR rig FIRST so XRController3D nodes exist in the scene tree when
			// OpenXR initialises and registers trackers. godot-xr-tools does the same:
			// nodes live in the .tscn file and are therefore present before Initialize().
			var xrInterface = XRServer.FindInterface("OpenXR");
			if (xrInterface != null)
			{
				// Connect SessionFocussed BEFORE Initialize() — the signal can fire
				// synchronously during Initialize() on some runtimes (Monado/SteamVR).
				if (xrInterface is OpenXRInterface openxr)
					openxr.SessionFocussed += OnXRSessionFocussed;

				BuildVRRig();

				if (xrInterface.Initialize())
				{
					GD.Print("[VRManager] OpenXR initialised.");
					DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
				}
				else
				{
					GD.Print("[VRManager] OpenXR init failed — desktop mode.");
					// Remove rig nodes and fall back
					foreach (var c in GetChildren())
						if (c is XROrigin3D) c.QueueFree();
					EnableDesktopFallback();
				}
			}
			else
			{
				GD.Print("[VRManager] OpenXR interface not found — desktop mode.");
				EnableDesktopFallback();
			}

			SpawnPatch("Default Patch");
		}

		private void OnXRSessionFocussed()
		{
			GD.Print("[VRManager] XR session focussed — enabling XR viewport.");
			GetViewport().UseXR = true;

			// Scan trackers that registered before this point
			var existing = XRServer.GetTrackers(255);
			foreach (var entry in existing)
				OnTrackerAdded(entry.Key.AsStringName(), 0);

			CallDeferred(MethodName.DebugXRState);
		}

		// ─── VR rig (created in code, no scene file needed) ──────────────────────

		private void BuildVRRig()
		{
			var origin = new XROrigin3D { Name = "XROrigin3D" };
			AddChild(origin);

			var camera = new XRCamera3D { Name = "XRCamera3D" };
			origin.AddChild(camera);

			// Left hand — navigation + commands
			// Tracker name: "left_hand" works in Godot ≤4.3; later versions may use the full
			// OpenXR path. OnTrackerAdded detects whichever name the runtime actually registers.
			_leftCtrl = new XRController3D { Name = "LeftController", Tracker = "left_hand" };
			origin.AddChild(_leftCtrl);
			var leftHand = new ControllerHand { Name = "LeftHand", IsLeft = true };
			_leftCtrl.AddChild(leftHand);
			leftHand.SceneRoot = _sceneRoot!;
			leftHand.OnUndo        = () => { GD.Print($"[Undo] {_scene.UndoStack.UndoDescription ?? "(nothing)"}"); _scene.UndoStack.Undo(); };
			leftHand.OnRedo        = () => { GD.Print($"[Redo] {_scene.UndoStack.RedoDescription ?? "(nothing)"}"); _scene.UndoStack.Redo(); };
			leftHand.OnSpawnAttach = AttachOrSpawn;
			leftHand.OnToggleG1    = ToggleEdgeConstraint;
			leftHand.OnDelete      = DeleteSelected;
			leftHand.OnSave        = SaveScene;

			// Right hand — sculpt
			_rightCtrl = new XRController3D { Name = "RightController", Tracker = "right_hand" };
			origin.AddChild(_rightCtrl);
			var rightHand = new ControllerHand { Name = "RightHand", IsLeft = false };
			_rightCtrl.AddChild(rightHand);
			rightHand.SceneRoot  = _sceneRoot!;
			rightHand.Selection  = _selection;
			rightHand.OnUndo     = leftHand.OnUndo;
			rightHand.OnRedo     = leftHand.OnRedo;

			leftHand.OtherHand  = rightHand;
			rightHand.OtherHand = leftHand;

			// World navigator watches both grips for two-hand pan/scale/rotate
			var nav = new WorldNavigator(_sceneRoot!, _leftCtrl, _rightCtrl);
			origin.AddChild(nav);

			// Tracker assignment happens via OnTrackerAdded / OnXRSessionFocussed
		}

		/// <summary>
		/// Called when the XR runtime registers a new tracker.
		/// Logs the name and reassigns XRController3D.Tracker to the exact runtime name.
		/// </summary>
		private void OnTrackerAdded(StringName name, long type)
		{
			GD.Print($"[XR] TrackerAdded: '{name}'  type={type}");
			string n = name.ToString().ToLower();
			if (n.Contains("left")  && _leftCtrl  != null) { _leftCtrl.Tracker  = name; GD.Print("[XR] → assigned to left controller."); }
			if (n.Contains("right") && _rightCtrl != null) { _rightCtrl.Tracker = name; GD.Print("[XR] → assigned to right controller."); }
		}

		/// <summary>Deferred one-frame diagnostic dump.</summary>
		private void DebugXRState()
		{
			GD.Print("[XR] DebugXRState:");
			GD.Print($"  left  tracker='{_leftCtrl?.Tracker}'  pos={_leftCtrl?.GlobalPosition}");
			GD.Print($"  right tracker='{_rightCtrl?.Tracker}'  pos={_rightCtrl?.GlobalPosition}");
			var trackers = XRServer.GetTrackers(255);
			GD.Print($"  XRServer has {trackers.Count} tracker(s):");
			foreach (var entry in trackers)
				GD.Print($"    '{entry.Key}'");
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

			// Diagnostic: log every Ctrl+key event so we can confirm they reach this handler.
			// If Ctrl+S / Ctrl+O never appear here, the editor is consuming them — use F2/F3.
			if (ctrl)
				GD.Print($"[VRManager] Ctrl+{key.Keycode}");

			if (ctrl && key.Keycode == Key.Z && !key.ShiftPressed)
			{
				GD.Print($"[Undo] {_scene.UndoStack.UndoDescription ?? "(nothing to undo)"}");
				_scene.UndoStack.Undo();
			}
			else if (ctrl && (key.Keycode == Key.Y || (key.Keycode == Key.Z && key.ShiftPressed)))
			{
				GD.Print($"[Redo] {_scene.UndoStack.RedoDescription ?? "(nothing to redo)"}");
				_scene.UndoStack.Redo();
			}
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
			else if (!ctrl && key.Keycode == Key.G)
				ToggleEdgeConstraint();
			else if (!ctrl && key.Keycode == Key.Delete)
				DeleteSelected();
			else if ((ctrl && key.Keycode == Key.S) || key.Keycode == Key.F2)
				SaveScene();
			else if ((ctrl && key.Keycode == Key.O) || key.Keycode == Key.F3)
				LoadScene();
		}

		// ─── Surface operations ───────────────────────────────────────────────────

		/// <summary>
		/// If one or more edges are selected, attach a patch to each.
		/// Otherwise spawn a standalone patch.
		/// </summary>
		private void AttachOrSpawn()
		{
			var edges = _selection.SelectedEdges;
			if (edges.Count > 0)
			{
				foreach (var er in edges)
				{
					var cmd = new AttachPatchCommand(er.Poly, er.Surface, er.Edge);
					_scene.UndoStack.Execute(cmd);
					GD.Print($"[VRManager] Attached patch to {er.Edge} of '{er.Poly.Name}'.");
				}
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

		private void ToggleEdgeConstraint()
		{
			var edges = _selection.SelectedEdges;
			if (edges.Count == 0)
			{
				GD.Print("[VRManager] No edge selected — select an edge first.");
				return;
			}

			foreach (var er in edges)
			{
				// Find the constraint that links this surface+edge pair
				EdgeConstraint? found = null;
				foreach (var c in er.Poly.Constraints)
				{
					if ((c.SurfaceA == er.Surface && c.EdgeA == er.Edge) ||
						(c.SurfaceB == er.Surface && c.EdgeB == er.Edge))
					{
						found = c;
						break;
					}
				}

				if (found == null)
				{
					GD.Print($"[VRManager] No constraint on this edge — attach a patch first.");
					continue;
				}

				var newType = found.Type == Continuity.G0 ? Continuity.G1 : Continuity.G0;
				var cmd = new SetConstraintTypeCommand(er.Poly, found, newType);
				_scene.UndoStack.Execute(cmd);
				GD.Print($"[VRManager] Edge constraint → {newType}");
			}
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
			GD.Print($"[Save] Saving {_scene.Polysurfaces.Count} polysurface(s) → {path}");
			try
			{
				Rhino3dmIO.Save(_scene, path);
				_scene.FilePath = path;
				GD.Print($"[Save] Done.");
			}
			catch (Exception e)
			{
				GD.PrintErr($"[Save] FAILED: {e.GetType().Name}: {e.Message}");
			}
		}

		private void LoadScene()
		{
			string path = _scene.FilePath
				?? ProjectSettings.GlobalizePath("user://scene.3dm");
			GD.Print($"[Load] Loading ← {path}");

			SculptScene? loaded;
			try
			{
				loaded = Rhino3dmIO.Load(path);
			}
			catch (Exception e)
			{
				GD.PrintErr($"[Load] FAILED: {e.GetType().Name}: {e.Message}");
				return;
			}

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
