using Godot;
using System.Collections.Generic;
using SplineSculptor.Model;

namespace SplineSculptor.Rendering
{
    /// <summary>
    /// Node3D that owns an ArrayMesh representing one SculptSurface.
    /// Renders with a procedural checkerboard shader (helps read surface curvature).
    /// Also owns a StaticBody3D + ConcavePolygonShape3D for surface raycasting,
    /// and an edge-highlight overlay for hover/selection of boundary edges.
    /// </summary>
    [GlobalClass]
    public partial class SurfaceNode : Node3D
    {
        [Export] public int SamplesU { get; set; } = 16;
        [Export] public int SamplesV { get; set; } = 16;

        private SculptSurface?         _surface;
        private MeshInstance3D?        _meshInstance;
        private ShaderMaterial?        _material;
        private MeshInstance3D?        _edgeHighlightMesh;
        private StaticBody3D?          _collisionBody;
        private ConcavePolygonShape3D? _collisionShape;

        private bool _rebuildPending = false;
        private bool _highResMode    = true;
        private bool _isSelected     = false;

        private SurfaceEdge? _hoveredEdge;
        private SurfaceEdge? _selectedEdge;

        // Cached boundary polylines in local space, extracted each full-res rebuild.
        private readonly Dictionary<SurfaceEdge, Vector3[]> _edgeLocalPoints = new();

        // ─── Shared shader ────────────────────────────────────────────────────────

        private static Shader? _checkerShader;
        private static Shader GetCheckerShader()
        {
            if (_checkerShader != null) return _checkerShader;
            _checkerShader = new Shader
            {
                Code = @"
shader_type spatial;
render_mode cull_disabled, blend_mix;

uniform float checks : hint_range(1.0, 20.0) = 9.0;
uniform vec4  color_a   : source_color = vec4(0.30, 0.50, 0.80, 0.75);
uniform vec4  color_b   : source_color = vec4(0.45, 0.65, 0.95, 0.75);
uniform float is_selected = 0.0;

void fragment() {
    vec2  uv  = UV * checks;
    float chk = mod(floor(uv.x) + floor(uv.y), 2.0);
    vec3  base = chk >= 1.0 ? color_a.rgb : color_b.rgb;
    if (is_selected > 0.5) {
        ALBEDO = mix(base, vec3(1.0, 0.55, 0.1), 0.7);
    } else {
        ALBEDO = base;
    }
    ALPHA = color_a.a;
}
"
            };
            return _checkerShader;
        }

        // ─── Public API ───────────────────────────────────────────────────────────

        public SculptSurface? Surface => _surface;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                _material?.SetShaderParameter("is_selected", _isSelected ? 1.0f : 0.0f);
            }
        }

        /// <summary>Called by DesktopInteraction every frame to update hover visuals.</summary>
        public void SetHoveredEdge(SurfaceEdge? edge)
        {
            if (_hoveredEdge == edge) return;
            _hoveredEdge = edge;
            UpdateEdgeHighlight();
        }

        /// <summary>Called by DesktopInteraction when an edge is clicked to select it.</summary>
        public void SetSelectedEdge(SurfaceEdge? edge)
        {
            if (_selectedEdge == edge) return;
            _selectedEdge = edge;
            UpdateEdgeHighlight();
        }

        /// <summary>Number of cached world-space boundary polyline points for an edge.</summary>
        public int GetEdgePointCount(SurfaceEdge edge) =>
            _edgeLocalPoints.TryGetValue(edge, out var pts) ? pts.Length : 0;

        /// <summary>World-space position of boundary polyline point i for the given edge.</summary>
        public Vector3 GetEdgeWorldPoint(SurfaceEdge edge, int index)
        {
            if (!_edgeLocalPoints.TryGetValue(edge, out var pts) || index >= pts.Length)
                return Vector3.Zero;
            return GlobalTransform * pts[index];
        }

        // ─── Initialisation ───────────────────────────────────────────────────────

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

            _material = new ShaderMaterial { Shader = GetCheckerShader() };
            _material.SetShaderParameter("checks",      9.0f);
            _material.SetShaderParameter("color_a",     new Color(0.30f, 0.50f, 0.80f, 0.75f));
            _material.SetShaderParameter("color_b",     new Color(0.45f, 0.65f, 0.95f, 0.75f));
            _material.SetShaderParameter("is_selected", 0.0f);
            _meshInstance.MaterialOverride = _material;

            _edgeHighlightMesh = new MeshInstance3D { Visible = false };
            AddChild(_edgeHighlightMesh);

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

        public void BeginDrag() { _highResMode = false; RebuildMesh(); }
        public void EndDrag()   { _highResMode = true;  _rebuildPending = true; }

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

            if (_highResMode)
            {
                CacheEdgePoints(verts, su, sv);
                UpdateEdgeHighlight();
                UpdateCollisionShape(verts, indices);
            }
        }

        // ─── Edge point cache ─────────────────────────────────────────────────────

        private void CacheEdgePoints(Vector3[] verts, int su, int sv)
        {
            // Tessellate vertex layout: verts[i * sv + j]
            // i = u-sample index (0..su-1), j = v-sample index (0..sv-1)
            var uMin = new Vector3[sv];
            var uMax = new Vector3[sv];
            var vMin = new Vector3[su];
            var vMax = new Vector3[su];

            for (int j = 0; j < sv; j++)
            {
                uMin[j] = verts[j];                  // i = 0
                uMax[j] = verts[(su - 1) * sv + j];  // i = su-1
            }
            for (int i = 0; i < su; i++)
            {
                vMin[i] = verts[i * sv];              // j = 0
                vMax[i] = verts[i * sv + (sv - 1)];  // j = sv-1
            }

            _edgeLocalPoints[SurfaceEdge.UMin] = uMin;
            _edgeLocalPoints[SurfaceEdge.UMax] = uMax;
            _edgeLocalPoints[SurfaceEdge.VMin] = vMin;
            _edgeLocalPoints[SurfaceEdge.VMax] = vMax;
        }

        // ─── Edge highlight ───────────────────────────────────────────────────────

        private void UpdateEdgeHighlight()
        {
            if (_edgeHighlightMesh == null) return;

            // Selected edge takes visual priority over hovered edge
            SurfaceEdge? show = _selectedEdge ?? _hoveredEdge;
            if (show == null || !_edgeLocalPoints.TryGetValue(show.Value, out var pts) || pts.Length < 2)
            {
                _edgeHighlightMesh.Visible = false;
                return;
            }

            var lineIdxs = new int[(pts.Length - 1) * 2];
            for (int i = 0; i < pts.Length - 1; i++)
            {
                lineIdxs[i * 2]     = i;
                lineIdxs[i * 2 + 1] = i + 1;
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = pts;
            arrays[(int)Mesh.ArrayType.Index]  = lineIdxs;

            var highlightMesh = new ArrayMesh();
            highlightMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);

            bool sel = _selectedEdge.HasValue;
            var mat = new StandardMaterial3D
            {
                ShadingMode     = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor     = sel ? new Color(0.2f, 1.0f, 0.3f) : new Color(1.0f, 1.0f, 0.2f),
                EmissionEnabled = true,
                Emission        = sel ? new Color(0.1f, 0.6f, 0.15f) : new Color(0.6f, 0.6f, 0.05f),
            };
            highlightMesh.SurfaceSetMaterial(0, mat);

            _edgeHighlightMesh.Mesh    = highlightMesh;
            _edgeHighlightMesh.Visible = true;
        }

        // ─── Collision shape ──────────────────────────────────────────────────────

        private void UpdateCollisionShape(Vector3[] verts, int[] indices)
        {
            if (_collisionBody == null) return;

            var faces = new Vector3[indices.Length];
            for (int i = 0; i < indices.Length; i++)
                faces[i] = verts[indices[i]];

            if (_collisionShape == null)
            {
                _collisionShape = new ConcavePolygonShape3D { BackfaceCollision = true };
                var colShape = new CollisionShape3D { Shape = _collisionShape };
                _collisionBody.AddChild(colShape);
            }

            _collisionShape.SetFaces(faces);
        }
    }
}
