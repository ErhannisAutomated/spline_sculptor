using Godot;

namespace SplineSculptor.VR
{
    /// <summary>
    /// A small world-space radial menu parented to an XRController3D.
    /// Shows 4 options arranged at the cardinal directions (Up / Right / Down / Left).
    /// Shown while the trackpad is held; the highlighted sector is selected on release.
    /// </summary>
    [GlobalClass]
    public partial class VRRadialMenu : Node3D
    {
        // Sector indices match the trackpad convention used in ControllerHand:
        //   0 = Up  1 = Right  2 = Down  3 = Left
        private readonly Label3D[] _labels = new Label3D[4];
        private MeshInstance3D?    _disc;

        private static readonly Color NormalColor    = new(0.95f, 0.95f, 0.95f, 0.90f);
        private static readonly Color HighlightColor = new(1.00f, 0.80f, 0.15f, 1.00f);
        private static readonly Color DimColor       = new(0.40f, 0.40f, 0.40f, 0.55f);

        // Label offsets in controller-local space (XY plane, Z facing player)
        private static readonly Vector3[] Offsets =
        {
            new( 0.000f,  0.065f, 0f),   // Up
            new( 0.085f,  0.000f, 0f),   // Right
            new( 0.000f, -0.065f, 0f),   // Down
            new(-0.085f,  0.000f, 0f),   // Left
        };

        public override void _Ready()
        {
            BuildBackgroundDisc();

            for (int i = 0; i < 4; i++)
            {
                var lbl = new Label3D
                {
                    FontSize    = 26,
                    PixelSize   = 0.00042f,
                    Billboard   = BaseMaterial3D.BillboardModeEnum.Enabled,
                    NoDepthTest = true,
                    Modulate    = NormalColor,
                    Position    = Offsets[i],
                };
                AddChild(lbl);
                _labels[i] = lbl;
            }

            Visible = false;
        }

        private void BuildBackgroundDisc()
        {
            var mesh = new CylinderMesh
            {
                TopRadius      = 0.105f,
                BottomRadius   = 0.105f,
                Height         = 0.003f,
                RadialSegments = 20,
            };
            var mat = new StandardMaterial3D
            {
                AlbedoColor  = new Color(0.04f, 0.04f, 0.10f, 0.80f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode  = BaseMaterial3D.ShadingModeEnum.Unshaded,
                NoDepthTest  = true,
            };
            _disc = new MeshInstance3D { Mesh = mesh };
            _disc.MaterialOverride = mat;
            // Rotate so the flat face points in +Z (toward the player)
            _disc.Rotation = new Vector3(Mathf.Pi / 2f, 0f, 0f);
            AddChild(_disc);
        }

        // ─── Public API ───────────────────────────────────────────────────────────

        public void SetOptions(string up, string right, string down, string left)
        {
            _labels[0].Text = up;
            _labels[1].Text = right;
            _labels[2].Text = down;
            _labels[3].Text = left;
        }

        /// <summary>Highlight one sector; pass -1 to dim all (centre/cancel).</summary>
        public void UpdateHighlight(int sector)
        {
            for (int i = 0; i < 4; i++)
                _labels[i].Modulate = (i == sector) ? HighlightColor : DimColor;
        }

        public void ResetHighlight()
        {
            foreach (var l in _labels)
                l.Modulate = NormalColor;
        }
    }
}
