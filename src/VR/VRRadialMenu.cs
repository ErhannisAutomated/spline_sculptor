using System.Collections.Generic;
using Godot;

namespace SplineSculptor.VR
{
	/// <summary>
	/// A small world-space radial menu parented to an XRController3D.
	/// Shows 4 options arranged at the cardinal directions (Up / Right / Down / Left).
	/// Shown while the trackpad is held; the highlighted sector is selected on release.
	///
	/// Supports a page stack for nested menus:
	///   PushPage — add a new menu level
	///   PopPage  — return to the previous level
	/// Items marked isSubmenu get a "→" suffix and are shown in a distinct colour.
	/// Items marked isDisabled are dimmed and cannot be highlighted.
	/// </summary>
	[GlobalClass]
	public partial class VRRadialMenu : Node3D
	{
		// Sector indices: 0=Up  1=Right  2=Down  3=Left
		private readonly Label3D[] _labels = new Label3D[4];
		private MeshInstance3D?    _disc;

		private static readonly Color NormalColor    = new(0.95f, 0.95f, 0.95f, 0.90f);
		private static readonly Color SubmenuColor   = new(0.55f, 0.85f, 1.00f, 0.90f);
		private static readonly Color HighlightColor = new(1.00f, 0.80f, 0.15f, 1.00f);
		private static readonly Color DimColor       = new(0.40f, 0.40f, 0.40f, 0.55f);

		private static readonly Vector3[] Offsets =
		{
			new( 0.000f,  0.065f, -0.003f),   // Up
			new( 0.085f,  0.000f, -0.003f),   // Right
			new( 0.000f, -0.065f, -0.003f),   // Down
			new(-0.085f,  0.000f, -0.003f),   // Left
		};

		// ─── Page stack ───────────────────────────────────────────────────────────

		private struct MenuPage
		{
			public string[] Labels;      // 4 entries (Up/Right/Down/Left)
			public bool[]   IsSubmenu;   // shows "→" suffix + blue tint
			public bool[]   IsDisabled;  // dimmed, cannot highlight
		}
		private readonly Stack<MenuPage> _pageStack = new();

		public bool IsAtRoot => _pageStack.Count <= 1;

		// ─── Lifecycle ────────────────────────────────────────────────────────────

		public override void _Ready()
		{
			BuildBackgroundDisc();

			for (int i = 0; i < 4; i++)
			{
				var lbl = new Label3D
				{
					FontSize      = 26,
					PixelSize     = 0.00042f,
					Billboard     = BaseMaterial3D.BillboardModeEnum.Enabled,
					NoDepthTest   = true,
					SortingOffset = 1.0f,
					Modulate      = NormalColor,
					Position      = Offsets[i],
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
			_disc.Rotation = new Vector3(-Mathf.Pi / 2f, 0f, 0f);
			AddChild(_disc);
		}

		// ─── Public API ───────────────────────────────────────────────────────────

		/// <summary>
		/// Push a new page onto the stack.
		/// labels, isSubmenu, isDisabled must each have exactly 4 elements.
		/// Null arrays default to all-false.
		/// </summary>
		public void PushPage(string[] labels,
		                     bool[]? isSubmenu  = null,
		                     bool[]? isDisabled = null)
		{
			_pageStack.Push(new MenuPage
			{
				Labels     = labels,
				IsSubmenu  = isSubmenu  ?? new bool[4],
				IsDisabled = isDisabled ?? new bool[4],
			});
			RefreshDisplay();
		}

		/// <summary>
		/// Pop the current page. Returns true if there was a page to pop (was not root).
		/// </summary>
		public bool PopPage()
		{
			if (_pageStack.Count <= 1) return false;
			_pageStack.Pop();
			RefreshDisplay();
			return true;
		}

		/// <summary>Clear all pages (used on teardown or full reset).</summary>
		public void ClearPages() => _pageStack.Clear();

		/// <summary>
		/// Highlight one sector (pass -1 to dim all / centre-cancel).
		/// Disabled items stay dim regardless.
		/// </summary>
		public void UpdateHighlight(int sector)
		{
			if (_pageStack.Count == 0) return;
			var page = _pageStack.Peek();
			for (int i = 0; i < 4; i++)
			{
				if (page.IsDisabled[i])
					_labels[i].Modulate = DimColor;
				else if (i == sector)
					_labels[i].Modulate = HighlightColor;
				else
					_labels[i].Modulate = page.IsSubmenu[i] ? SubmenuColor : NormalColor;
			}
		}

		/// <summary>Reset all labels to their non-highlighted base colour.</summary>
		public void ResetHighlight() => RefreshDisplay();

		/// <summary>Returns true if the sector at the current page is selectable.</summary>
		public bool IsSelectable(int sector) =>
			_pageStack.Count > 0 && sector >= 0 && !_pageStack.Peek().IsDisabled[sector];

		// ─── Internal display refresh ─────────────────────────────────────────────

		private void RefreshDisplay()
		{
			if (_pageStack.Count == 0) return;
			var page = _pageStack.Peek();
			for (int i = 0; i < 4; i++)
			{
				string label = page.Labels[i] ?? "";
				if (page.IsSubmenu[i]) label += " \u2192"; // →
				_labels[i].Text     = label;
				_labels[i].Modulate = page.IsDisabled[i] ? DimColor
				                    : page.IsSubmenu[i]  ? SubmenuColor
				                    :                      NormalColor;
			}
		}
	}
}
