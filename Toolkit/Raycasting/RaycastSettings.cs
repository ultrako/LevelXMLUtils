namespace LevelXMLUtils.Raycasting;

/// <summary>
/// The shape of a raycaster's view: how many screen columns and distance steps it draws, how many directions the player
/// can face, how far and how wide it sees, and where the screen is. Everything else (wall heights on the screen, which
/// distance step a wall falls in, the shading) follows from these.
/// </summary>
/// <remarks>
/// What these cost in a level: every column is a stack of foreground groups (a colour panel per wall material, a face
/// overlay, a shade overlay, a texture overlay if any material is textured, and a pair of mask bars per distance step),
/// so <see cref="Columns"/> times <see cref="Depths"/> sets most of the shape count and of the frame time. 120 columns
/// and 50 depths ran at full frame rate in the author's browser; 60 depth steps lagged. Every facing is wiring in the
/// ray walk, so the number of facings mostly costs level size.
/// </remarks>
public record RaycastSettings
{
	/// <summary>Screen columns, each drawn as one vertical strip.</summary>
	public int Columns { get; init; } = 120;
	/// <summary>Distance steps: a wall is drawn at one of this many heights, from <see cref="MaxWallHeight"/> down to <see cref="MinWallHeight"/>.</summary>
	public int Depths { get; init; } = 50;
	/// <summary>Directions the player can face, 0 north and clockwise from there. A multiple of 4: the ray walk shares its wiring between facings a
	/// quarter turn apart.</summary>
	public int Facings { get; init; } = 32;
	public double FieldOfViewDegrees { get; init; } = 70;
	/// <summary>How far the player sees, in cells: a wall further than this is not drawn (the fog backdrop shows instead).</summary>
	public double MaxRange { get; init; } = 15;
	/// <summary>How many cells tall a wall is (1 = cubes). Taller walls put the eye higher, so rooms feel smaller and a step shorter.</summary>
	public double WallHeightCells { get; init; } = 2;

	/// <summary>The screen's top left corner in the level. The game's window is 900 by 500 and the camera does not go past the level's top left, so a
	/// screen at (0, 0) the size of the window fills it while the character is near that corner.</summary>
	public double ScreenLeft { get; init; } = 0;
	public double ScreenTop { get; init; } = 0;
	public double ScreenWidth { get; init; } = 900;
	public double ScreenHeight { get; init; } = 500;

	/// <summary>The brightness of a wall's north, east, south and west faces, so the two sides of a corner differ.</summary>
	public double[] FaceBrightness { get; init; } = { 1.00, 0.80, 0.60, 0.80 };
	/// <summary>How dark the farthest wall is: a black overlay at this opacity (0 to 100), falling to 0 for a wall one cell away.</summary>
	public double FarShadeOpacity { get; init; } = 88;
	/// <summary>The floor and ceiling at full brightness (nearest the player); toward the horizon they are fogged like the walls.</summary>
	public int FloorColor { get; init; } = 0x7D8792;
	public int CeilingColor { get; init; } = 0x7D8792;
	/// <summary>How many bands of fog the floor and ceiling are drawn in. More is smoother and costs bar shapes in every column.</summary>
	public int FloorBands { get; init; } = 4;
	/// <summary>How many stripes across a face a wall texture has (see <see cref="WallMaterial.Texture"/>).</summary>
	public int TextureStripes { get; init; } = 8;

	/// <summary>A walking step is one cell along the facing's main axis, and every so often one along the other, so that over this many steps the
	/// player has gone the facing's way to within 1/SubSteps. A power of 2.</summary>
	public int SubSteps { get; init; } = 16;

	/// <summary>Sprites are placed every this many columns across the screen, and sized every this many distance steps: each size is a physics body per
	/// state, and each place and size is wiring in the ray walk.</summary>
	public int SpriteXStep { get; init; } = 2;
	public int SpriteSizeStep { get; init; } = 2;
	/// <summary>How long a sprite that wasn't on the screen waits before it shows, so its carrier has moved it into place (a whole number of frames at 30 a
	/// second once floor(delay * 30) is taken: 0.07 is 2 frames).</summary>
	public double SpriteShowDelaySeconds { get; init; } = 0.07;

	/// <summary>Where the sprites' invisible physics bodies are parked, in rows below the screen (each body in a row of its own, so no two ever overlap: the
	/// game keeps a table of overlapping bodies and freezes when it fills). Null puts them 200 units below the screen.</summary>
	public double? SpriteRowsTop { get; init; }

	public double CenterX => ScreenLeft + ScreenWidth / 2;
	public double CenterY => ScreenTop + ScreenHeight / 2;
	public int Quarters => 4;
	public int FacingsPerQuarter => Facings / 4;

	/// <summary>The screen height of a wall one cell away (before clamping to the screen): the horizontal scale times the wall's height in cells, so a
	/// wall is as tall on the screen, relative to its width, as it is in cells.</summary>
	public double PerspectiveScale => WallHeightCells * ScreenWidth / 2 / Math.Tan(FieldOfViewDegrees * Math.PI / 360);
	/// <summary>The tallest wall fills the screen's height.</summary>
	public double MaxWallHeight => ScreenHeight;
	/// <summary>A wall <see cref="MaxRange"/> away.</summary>
	public double MinWallHeight => PerspectiveScale / MaxRange;

	/// <summary>The height of distance step <paramref name="depth"/> (1 to <see cref="Depths"/>): evenly spaced heights.</summary>
	public double StepHeight(int depth) => MaxWallHeight - (double)(depth - 1) / (Depths - 1) * (MaxWallHeight - MinWallHeight);
	/// <summary>How tall a wall at a distance (in cells) is on the screen: in perspective, PerspectiveScale / distance, which keeps the top of a flat wall straight.</summary>
	public double HeightAtDistance(double distance) => Math.Clamp(PerspectiveScale / Math.Max(distance, 1e-9), MinWallHeight, MaxWallHeight);
	/// <summary>Which distance step (1 to <see cref="Depths"/>) shows a wall at a distance: the step whose height is nearest.</summary>
	public int DepthStep(double distance) =>
		Math.Clamp(1 + (int)Math.Floor((MaxWallHeight - HeightAtDistance(distance)) / (MaxWallHeight - MinWallHeight) * (Depths - 1) + 0.5 - 1e-9), 1, Depths);
	/// <summary>The distance a step stands for.</summary>
	public double StepDistance(int depth) => PerspectiveScale / StepHeight(depth);
	/// <summary>The shade overlay's opacity at a step: 0 one cell away, <see cref="FarShadeOpacity"/> at <see cref="MaxRange"/>, linear in the distance.</summary>
	public double ShadeOpacity(int depth) => FarShadeOpacity * Math.Clamp((StepDistance(depth) - 1) / (MaxRange - 1), 0, 1);
	/// <summary>The face overlay's opacity for a face (0 north, 1 east, 2 south, 3 west).</summary>
	public double FaceOpacity(int face) => Math.Round((1 - FaceBrightness[face]) * 100, 6);

	/// <summary>
	/// The ray for facing f and screen column c, as a direction that is not unit length: forward plus the camera plane (right, scaled by tan(FOV / 2)) at
	/// the column's place across the screen. Its length grows away from the middle, which makes the perpendicular distance a grid march finds come out
	/// already corrected for fisheye.
	/// </summary>
	public (double dx, double dy) Ray(int f, int c)
	{
		double angle = f * 2.0 * Math.PI / Facings;
		double dirX = Math.Sin(angle), dirY = -Math.Cos(angle), rightX = Math.Cos(angle), rightY = Math.Sin(angle);
		double plane = Math.Tan(FieldOfViewDegrees * Math.PI / 360);
		double cameraX = (c - Columns / 2.0 + 0.5) / (Columns / 2.0);
		return (dirX + rightX * plane * cameraX, dirY + rightY * plane * cameraX);
	}

	/// <summary>Which of a texture's stripes a point <paramref name="u"/> of the way across a face shows, from the face's left edge as the viewer sees it.
	/// Rounded first, so a ray landing on a stripe boundary gives the same stripe from every facing it is computed from.</summary>
	public int Stripe(double u) => Math.Clamp((int)Math.Floor(Math.Round(u * TextureStripes, 9)), 0, TextureStripes - 1);

	/// <summary>
	/// The cells the ray of facing f and column c goes through from the middle of the player's cell, relative to it, over open space and out to
	/// <see cref="MaxRange"/>: for each, the face it would be hit on, the distance step a wall there shows at, and the texture stripe the ray lands on.
	/// </summary>
	public List<(int x, int y, int face, int depth, int stripe)> RayPath(int f, int c)
	{
		(double rdx, double rdy) = Ray(f, c);
		double sx = 0.5, sy = 0.5; int mx = 0, my = 0;
		double ddx = rdx == 0 ? double.MaxValue : Math.Abs(1.0 / rdx), ddy = rdy == 0 ? double.MaxValue : Math.Abs(1.0 / rdy);
		int stx = rdx < 0 ? -1 : 1, sty = rdy < 0 ? -1 : 1;
		double sdx = rdx < 0 ? (sx - mx) * ddx : (mx + 1.0 - sx) * ddx, sdy = rdy < 0 ? (sy - my) * ddy : (my + 1.0 - sy) * ddy;
		var path = new List<(int, int, int, int, int)>();
		while (true)
		{
			int side;
			if (sdx < sdy) { sdx += ddx; mx += stx; side = 0; } else { sdy += ddy; my += sty; side = 1; }
			double perp = side == 0 ? (mx - sx + (1 - stx) / 2.0) / rdx : (my - sy + (1 - sty) / 2.0) / rdy;
			if (perp > MaxRange) { return path; }
			int face = side == 0 ? (stx > 0 ? 3 : 1) : (sty > 0 ? 0 : 2);
			// where along the face the ray lands, from its left edge as the viewer sees it
			double along = side == 0 ? sy + perp * rdy : sx + perp * rdx, frac = along - Math.Floor(along);
			double u = side == 0 ? (stx > 0 ? frac : 1 - frac) : (sty > 0 ? 1 - frac : frac);
			path.Add((mx, my, face, DepthStep(perp), Stripe(u)));
		}
	}

	// Walking: a step is one cell along the axis the facing points along most (MajorStep) and, every so often, one along the other (MinorStep): MinorK
	// sixteenths (of SubSteps) of the time, a Bresenham line kept in a sub-cell counter per axis. 0 for the four straight facings, SubSteps for the diagonals.
	public (int x, int y) MajorStep(int f) => Steps(f).major;
	public (int x, int y) MinorStep(int f) => Steps(f).minor;
	public int MinorK(int f) => Steps(f).k;
	((int x, int y) major, (int x, int y) minor, int k) Steps(int f)
	{
		double angle = f * 2.0 * Math.PI / Facings, ux = Math.Sin(angle), uy = -Math.Cos(angle);
		bool xMajor = Math.Abs(ux) >= Math.Abs(uy);
		double major = xMajor ? ux : uy, minor = xMajor ? uy : ux;
		int k = (int)Math.Round(Math.Abs(minor) / Math.Abs(major) * SubSteps);
		(int, int) majorStep = xMajor ? (Math.Sign(major), 0) : (0, Math.Sign(major));
		(int, int) minorStep = k == 0 ? (0, 0) : xMajor ? (0, Math.Sign(minor)) : (Math.Sign(minor), 0);
		return (majorStep, minorStep, k);
	}

	/// <summary>Throws if the settings can't make a working raycaster.</summary>
	public void Validate()
	{
		if (Facings < 4 || Facings % 4 != 0) { throw new ArgumentException($"Facings must be a positive multiple of 4, not {Facings}"); }
		if (Columns < 2 || Depths < 2) { throw new ArgumentException("at least 2 columns and 2 depths are needed"); }
		if (SubSteps < 2 || (SubSteps & (SubSteps - 1)) != 0) { throw new ArgumentException($"SubSteps must be a power of 2, not {SubSteps}"); }
		if (FaceBrightness.Length != 4) { throw new ArgumentException("FaceBrightness needs a brightness for each of the four faces"); }
		if (MinWallHeight >= MaxWallHeight) { throw new ArgumentException("a wall at MaxRange is as tall as the screen: increase MaxRange or lower WallHeightCells"); }
		if (ScreenLeft < 0 || ScreenTop < 0) { throw new ArgumentException("the game moves anything at a negative coordinate back into the map: keep the screen at positive coordinates"); }
	}
}
