using Godot;
using System.Collections.Generic;
using SplineSculptor.Model;
using SplineSculptor.Interaction;

namespace SplineSculptor.Rendering
{
    /// <summary>
    /// Node3D that renders the control point grid for a SculptSurface:
    ///   - Sphere MeshInstance3Ds at each control point (with ControlPointHandle scripts)
    ///   - Lines connecting adjacent control points (ImmediateMesh)
    /// </summary>
    [GlobalClass]
    public partial class ControlNetNode : Node3D
    {
        public const float HandleRadius = 0.025f; // 2.5 cm in world units

        private SculptSurface? _surface;
        private readonly List<ControlPointHandle> _handles = new();
        private MeshInstance3D? _lineMeshInstance;
        private Polysurface? _polysurface;

        public IReadOnlyList<ControlPointHandle> Handles => _handles;

        public void Init(SculptSurface surface, Polysurface polysurface)
        {
            _surface = surface;
            _polysurface = polysurface;

            RebuildHandles();
            RebuildLines();

            _surface.GeometryChanged += OnGeometryChanged;
        }

        public override void _Ready()
        {
            _lineMeshInstance = new MeshInstance3D();
            AddChild(_lineMeshInstance);
        }

        private void OnGeometryChanged()
        {
            // Check if CP count changed (e.g., after knot insertion)
            if (_surface == null) return;
            int expectedU = _surface.Geometry.CpCountU;
            int expectedV = _surface.Geometry.CpCountV;

            if (_handles.Count != expectedU * expectedV)
                RebuildHandles();
            else
                UpdateHandlePositions();

            RebuildLines();
        }

        // ─── Handle creation ──────────────────────────────────────────────────────

        private void RebuildHandles()
        {
            if (_surface == null) return;

            // Remove existing
            foreach (var h in _handles)
                h.QueueFree();
            _handles.Clear();

            int cpU = _surface.Geometry.CpCountU;
            int cpV = _surface.Geometry.CpCountV;

            var sphere = new SphereMesh { Radius = HandleRadius, Height = HandleRadius * 2 };
            sphere.RadialSegments = 8;
            sphere.Rings = 4;

            for (int u = 0; u < cpU; u++)
            {
                for (int v = 0; v < cpV; v++)
                {
                    var handle = new ControlPointHandle();
                    handle.Init(_surface, u, v, _polysurface);

                    // Each handle gets its own material so hover colour changes are isolated.
                    var material = new StandardMaterial3D
                    {
                        AlbedoColor     = new Color(1f, 0.8f, 0.2f),
                        EmissionEnabled = true,
                        Emission        = new Color(0.5f, 0.4f, 0.1f),
                    };
                    var mi = new MeshInstance3D { Mesh = sphere, MaterialOverride = material };
                    handle.AddChild(mi);
                    handle.Position = _surface.Geometry.ControlPoints[u, v];

                    // Sphere collider for hover detection
                    var col = new CollisionShape3D();
                    col.Shape = new SphereShape3D { Radius = HandleRadius * 1.5f };
                    var body = new StaticBody3D();
                    body.AddChild(col);
                    handle.AddChild(body);

                    AddChild(handle);
                    _handles.Add(handle);
                }
            }
        }

        private void UpdateHandlePositions()
        {
            if (_surface == null) return;
            int v = _surface.Geometry.CpCountV;
            for (int i = 0; i < _handles.Count; i++)
            {
                int u = i / v;
                int vIdx = i % v;
                _handles[i].Position = _surface.Geometry.ControlPoints[u, vIdx];
            }
        }

        // ─── Line net ─────────────────────────────────────────────────────────────

        private void RebuildLines()
        {
            if (_surface == null || _lineMeshInstance == null) return;

            var geo = _surface.Geometry;
            int cpU = geo.CpCountU;
            int cpV = geo.CpCountV;

            // Build an ArrayMesh with LineStrip-per-row segments
            var verts = new System.Collections.Generic.List<Vector3>();
            var indices = new System.Collections.Generic.List<int>();

            // Horizontal lines (along V for each U row)
            for (int u = 0; u < cpU; u++)
            {
                int start = verts.Count;
                for (int v2 = 0; v2 < cpV; v2++)
                    verts.Add(geo.ControlPoints[u, v2]);

                for (int v2 = 0; v2 < cpV - 1; v2++)
                {
                    indices.Add(start + v2);
                    indices.Add(start + v2 + 1);
                }
            }

            // Vertical lines (along U for each V column)
            for (int v2 = 0; v2 < cpV; v2++)
            {
                int start = verts.Count;
                for (int u = 0; u < cpU; u++)
                    verts.Add(geo.ControlPoints[u, v2]);

                for (int u = 0; u < cpU - 1; u++)
                {
                    indices.Add(start + u);
                    indices.Add(start + u + 1);
                }
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
            arrays[(int)Mesh.ArrayType.Index]  = indices.ToArray();

            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);

            var mat = new StandardMaterial3D
            {
                AlbedoColor     = new Color(0.9f, 0.9f, 0.9f, 0.6f),
                Transparency    = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode     = BaseMaterial3D.ShadingModeEnum.Unshaded,
            };
            mesh.SurfaceSetMaterial(0, mat);

            _lineMeshInstance.Mesh = mesh;
        }
    }
}
