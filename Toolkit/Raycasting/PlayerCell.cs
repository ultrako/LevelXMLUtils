using LevelXML;
namespace LevelXMLUtils.Raycasting;

/// <summary>
/// Which cell of a grid the player is in, kept as which relays are enabled rather than as binary counters that would have to be decoded every redraw.
/// <see cref="Cell"/>[y * width + x] (one per open cell) is enabled while the player is in column x; <see cref="Broadcast"/> fires the player's row, and
/// of that row only the player's column's cell is enabled, so exactly the player's cell fires. About a hundred actions on a 91 by 47 map, where decoding
/// counters was about 20,000.
/// </summary>
/// <remarks>
/// Columns and rows are grouped in blocks of about the square root of their count: a move fires the block relays of its direction (the player's block
/// is the enabled one), the block fires its own column or row relays, and that one switches the state to its neighbour. Each direction fires in the
/// order that passes a relay before it can be enabled (east from the east end, west from the west end), so a move moves exactly once. A move off the
/// grid's edge does nothing.
/// </remarks>
public class PlayerCell : TriggerContraption
{
	/// <summary>[y * width + x], null for a wall cell: fired by <see cref="Broadcast"/> while the player is in that cell.</summary>
	public readonly Relay?[] Cell;
	public readonly Relay MoveEast, MoveWest, MoveSouth, MoveNorth, Broadcast;

	public PlayerCell(int width, int height, Func<int, int, bool> open, int startX, int startY)
	{
		List<Entity> entities = new();
		Relay NewRelay(bool startDisabled = false) { Relay r = new(startDisabled: startDisabled); entities.AddRange(r.Entities); return r; }
		Cell = new Relay?[width * height];
		for (int y = 0; y < height; y++) { for (int x = 0; x < width; x++) { if (open(x, y)) { Cell[y * width + x] = NewRelay(x != startX); } } }

		IEnumerable<Target> Column(int x, bool enable) => Enumerable.Range(0, height).Select(y => Cell[y * width + x]).OfType<Relay>().SelectMany(r => enable ? r.Enable : r.Disable);
		var E = Enumerable.Range(0, width).Select(x => NewRelay(x != startX)).ToArray();
		var Wt = Enumerable.Range(0, width).Select(x => NewRelay(x != startX)).ToArray();
		var S = Enumerable.Range(0, height).Select(y => NewRelay(y != startY)).ToArray();
		var N = Enumerable.Range(0, height).Select(y => NewRelay(y != startY)).ToArray();
		var Row = Enumerable.Range(0, height).Select(y => NewRelay(y != startY)).ToArray();
		int kx = (int)Math.Ceiling(Math.Sqrt(width)), ky = (int)Math.Ceiling(Math.Sqrt(height));
		int bx = (width + kx - 1) / kx, by = (height + ky - 1) / ky;
		Relay[] Blocks(int n, int start) => Enumerable.Range(0, n).Select(k => NewRelay(k != start)).ToArray();
		Relay[] BE = Blocks(bx, startX / kx), BW = Blocks(bx, startX / kx), BS = Blocks(by, startY / ky), BN = Blocks(by, startY / ky), BR = Blocks(by, startY / ky);
		IEnumerable<Target> XBlock(int k, bool on) => on ? BE[k].Enable.Concat(BW[k].Enable) : BE[k].Disable.Concat(BW[k].Disable);
		IEnumerable<Target> YBlock(int k, bool on) => on ? BS[k].Enable.Concat(BN[k].Enable).Concat(BR[k].Enable) : BS[k].Disable.Concat(BN[k].Disable).Concat(BR[k].Disable);
		IEnumerable<Target> XCross(int from, int to) => from / kx == to / kx ? Enumerable.Empty<Target>() : XBlock(from / kx, false).Concat(XBlock(to / kx, true));
		IEnumerable<Target> YCross(int from, int to) => from / ky == to / ky ? Enumerable.Empty<Target>() : YBlock(from / ky, false).Concat(YBlock(to / ky, true));
		for (int x = 0; x < width; x++)
		{
			if (x + 1 < width) { E[x].Output.Connect(new Input(E[x].Disable.Concat(Wt[x].Disable).Concat(E[x + 1].Enable).Concat(Wt[x + 1].Enable).Concat(Column(x, false)).Concat(Column(x + 1, true)).Concat(XCross(x, x + 1)))); }
			if (x > 0) { Wt[x].Output.Connect(new Input(Wt[x].Disable.Concat(E[x].Disable).Concat(Wt[x - 1].Enable).Concat(E[x - 1].Enable).Concat(Column(x, false)).Concat(Column(x - 1, true)).Concat(XCross(x, x - 1)))); }
		}
		for (int y = 0; y < height; y++)
		{
			if (y + 1 < height) { S[y].Output.Connect(new Input(S[y].Disable.Concat(N[y].Disable).Concat(Row[y].Disable).Concat(S[y + 1].Enable).Concat(N[y + 1].Enable).Concat(Row[y + 1].Enable).Concat(YCross(y, y + 1)))); }
			if (y > 0) { N[y].Output.Connect(new Input(N[y].Disable.Concat(S[y].Disable).Concat(Row[y].Disable).Concat(N[y - 1].Enable).Concat(S[y - 1].Enable).Concat(Row[y - 1].Enable).Concat(YCross(y, y - 1)))); }
			int y1 = y;
			Row[y].Output.Connect(new Input(Enumerable.Range(0, width).Select(x => Cell[y1 * width + x]).OfType<Relay>().SelectMany(r => r.Input)));
		}
		IEnumerable<int> Block(int k, int size, int count) => Enumerable.Range(k * size, Math.Min(size, count - k * size));
		for (int k = 0; k < bx; k++)
		{
			BE[k].Output.Connect(new Input(Block(k, kx, width).Where(x => x + 1 < width).Reverse().SelectMany(x => E[x].Input)));
			BW[k].Output.Connect(new Input(Block(k, kx, width).Where(x => x > 0).SelectMany(x => Wt[x].Input)));
		}
		for (int k = 0; k < by; k++)
		{
			BS[k].Output.Connect(new Input(Block(k, ky, height).Where(y => y + 1 < height).Reverse().SelectMany(y => S[y].Input)));
			BN[k].Output.Connect(new Input(Block(k, ky, height).Where(y => y > 0).SelectMany(y => N[y].Input)));
			BR[k].Output.Connect(new Input(Block(k, ky, height).SelectMany(y => Row[y].Input)));
		}
		MoveNorth = NewRelay(); MoveNorth.Output.Connect(new Input(BN.SelectMany(b => b.Input)));            // ascending
		MoveEast = NewRelay(); MoveEast.Output.Connect(new Input(BE.Reverse().SelectMany(b => b.Input)));    // descending
		MoveSouth = NewRelay(); MoveSouth.Output.Connect(new Input(BS.Reverse().SelectMany(b => b.Input)));  // descending
		MoveWest = NewRelay(); MoveWest.Output.Connect(new Input(BW.SelectMany(b => b.Input)));              // ascending
		Broadcast = NewRelay(); Broadcast.Output.Connect(new Input(BR.SelectMany(b => b.Input)));
		Entities = entities;
	}
}
