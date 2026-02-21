using Godot;
using SplineSculptor.Model;

namespace SplineSculptor.Rendering
{
    /// <summary>
    /// Node3D that owns an ArrayMesh representing one SculptSurface.
    /// Also owns a StaticBody3D with a ConcavePolygonShape3D so desktop
    /// raycasts can select the surface.
    /// </summary>
    [GlobalClass]
    public partial class SurfaceNode : Node3D
    {
        [Export] public int SamplesU { get; set; } = 16;
        [Export] public int SamplesV { get; set; } = 16;

        private SculptSurface? _surface;
        private MeshInstance3D?        _meshInstance;
        private StandardMaterial3D?   _material;
        private StaticBody3D?          _collisionBody;
        private ConcavePolygonShape3D? _collisionShape;

        private bool _rebuildPending = false;
        private bool _highResMode    = true;
        private bool _isSelected     = false;

        public SculptSurface? Surface => _surface;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                if (_material != null)
                    _material.AlbedoColor = _isSelected
                        ? new Color(1.0f, 0.55f, 0.1f)  // orange highlight
                        : (_surface?.SurfaceColor ?? new Color(0.4f, 0.6f, 0.9f));
            }
        }

        public void Init(SculptSurface surface)
        {
            if (_surface != null)
                _surface.GeometryChanged -= OnGeometryChanged;

            _surface = surface;
            _surface.GeometryChanged += OnGeometryChanged;

            RebuildMesh();
        }

        public override void _Ready()
        {
            _meshInstance = new MeshInstance3D();
            AddChild(_meshInstance);

            _material = new StandardMaterial3D
            {
                AlbedoColor  = new Color(0.4f, 0.6f, 0.9f, 1.0f),
                CullMode     = BaseMaterial3D.CullModeEnum.Disabled,
            };
            _meshInstance.MaterialOverride = _material;

            // Collision body for selection raycasting
            _collisionBody = new StaticBody3D();
            AddChild(_collisionBody);
        }

        public override void _Process(double delta)
        {
            if (_rebuildPending)
            {
                RebuildMesh();
                _rebuildPending = false;
            }
        }

        // ─── Drag mode ────────────────────────────────────────────────────────────

        public void BeginDrag()
        {
            _highResMode = false;
            RebuildMesh();
        }

        public void EndDrag()
        {
            _highResMode = true;
            _rebuildPending = true;
        }

        private void OnGeometryChanged()
        {
            if (_highResMode)
                _rebuildPending = true;
            else
                RebuildMesh();
        }

        // ─── Mesh rebuild ─────────────────────────────────────────────────────────

        private void RebuildMesh()
        {
            if (_surface == null || _meshInstance == null) return;

            int su = _highResMode ? SamplesU : System.Math.Max(4, SamplesU / 4);
            int sv = _highResMode ? SamplesV : System.Math.Max(4, SamplesV / 4);

            var (verts, normals, indices, uvs) = _surface.Geometry.Tessellate(su, sv);

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.TexUV]  = uvs;
            arrays[(int)Mesh.ArrayType.Index]  = indices;

            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            _meshInstance.Mesh = mesh;

            if (_material != null)
                _material.AlbedoColor = _isSelected
                    ? new Color(1.0f, 0.55f, 0.1f)
                    : (_surface?.SurfaceColor ?? new Color(0.4f, 0.6f, 0.9f));

            // Update collision shape on full-res rebuilds only
            if (_highResMode)
                UpdateCollisionShape(verts, indices);
        }

        private void UpdateCollisionShape(Vector3[] verts, int[] indices)
        {
            if (_collisionBody == null) return;

            // ConcavePolygonShape3D expects a flat array of triangle vertices (3 per tri)
            var faces = new Vector3[indices.Length];
            for (int i = 0; i < indices.Length; i++)
                faces[i] = verts[indices[i]];

            if (_collisionShape == null)
            {
                _collisionShape = new ConcavePolygonShape3D();
                var colShape = new CollisionShape3D { Shape = _collisionShape };
                _collisionBody.AddChild(colShape);
            }

            _collisionShape.SetFaces(faces);
        }
    }
}
