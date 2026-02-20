using Godot;
using SplineSculptor.Model;

namespace SplineSculptor.Rendering
{
    /// <summary>
    /// Node3D that owns an ArrayMesh representing one SculptSurface.
    /// Rebuilds the mesh whenever the underlying geometry changes.
    /// </summary>
    [GlobalClass]
    public partial class SurfaceNode : Node3D
    {
        /// <summary>Tessellation resolution. Higher = smoother but more vertices.</summary>
        [Export] public int SamplesU { get; set; } = 16;
        [Export] public int SamplesV { get; set; } = 16;

        private SculptSurface? _surface;
        private MeshInstance3D? _meshInstance;
        private StandardMaterial3D? _material;

        // If true, defer tessellation to the next frame (used during drag).
        private bool _rebuildPending = false;
        private bool _highResMode    = true;

        public void Init(SculptSurface surface)
        {
            // Unsubscribe from previous surface
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
                AlbedoColor       = new Color(0.4f, 0.6f, 0.9f, 1.0f),
                CullMode          = BaseMaterial3D.CullModeEnum.Disabled,
                RoughnessTexture  = null,
            };
            _meshInstance.MaterialOverride = _material;
        }

        public override void _Process(double delta)
        {
            if (_rebuildPending)
            {
                RebuildMesh();
                _rebuildPending = false;
            }
        }

        // ─── Low-res preview during drag, full rebuild on release ─────────────────

        /// <summary>Call when a drag starts to switch to low-res tessellation.</summary>
        public void BeginDrag()
        {
            _highResMode = false;
            RebuildMesh();
        }

        /// <summary>Call when a drag ends to switch back to high-res tessellation.</summary>
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
                RebuildMesh(); // low-res stays realtime during drag
        }

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

            // Update material color from model
            if (_material != null)
                _material.AlbedoColor = _surface.SurfaceColor;
        }
    }
}
