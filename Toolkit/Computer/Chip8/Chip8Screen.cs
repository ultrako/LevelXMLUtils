using LevelXML;
using LevelXMLUtils.Memory;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Computer;

// Chip8Machine's screen: pixels, persistence, and the column and row structures a sprite draws through
public partial class Chip8Machine
{
	// The screen
	DecoderRelays xDecoder = null!;
	readonly Dictionary<int, (DecoderRelays decoder, Relay unset)> yDecoders = new();
	Relay[,] colOff = null!, pixelGates = null!;
	readonly Dictionary<int, (Relay arm, Relay disarm)> rowLines = new();
	Relay[] offsetLines = null!;
	Relay clearScreen = null!, unsetX = null!;
	Relay[] xCurrent = null!;
	// What the x decoder's candidate v does, as a relay of its own (and the same per y decoder): a cached position fires it without decoding
	Relay[] xSelect = null!, xCurrentNarrow = null!;
	Relay unsetXNarrow = null!;
	const int NarrowColumns = 4;
	readonly Dictionary<int, Relay[]> ySelects = new();
	int maxRows = 0;
	// How many of the top sprite rows arm by halves of the screen (BuildScreen); Danm8ku's bullets are 4 rows
	const int HalvedRows = 4;
	// A sprite's rows whose bytes are known (row, byte): rows with the same byte are armed together and each of its columns fired once (a
	// column fires the pixel gates of every armed row), so two identical rows (Danm8ku's straight movement sprites) cost one column pass;
	// empty rows aren't armed at all. Pixels flip independently, so the order doesn't matter, nor does it to the collision.
	void DrawConstantRows(Steps s, IEnumerable<(int row, int bits)> rows)
	{
		foreach (var same in rows.Where(r => r.bits != 0).GroupBy(r => r.bits))
		{
			s.Add(new Input(same.SelectMany(r => rowLines[r.row].arm.Input)));
			foreach (int c in Enumerable.Range(0, 8).Where(c => ((same.Key >> (7 - c)) & 1) == 1)) { s.Add(offsetLines[c]); }
			s.Add(new Input(same.SelectMany(r => rowLines[r.row].disarm.Input)));
		}
	}
	// Returns what the end of each game frame fires (the persistence stages), or a relay doing nothing without persistence
	Relay BuildScreen(double pixelSize, int onColor, int offColor)
	{
		tag = "screen: pixel gates and persistence";
		Relay persistTick = NewRelay();
		// A relay per screen row, enabled while a pixel of the row has a stage armed: the end of a frame fires the rows, and an enabled row
		// fires its pixels' stages (second ones first, so a pixel moves one stage a frame) after disabling itself; a stage moving up enables it again
		Relay[] persistRows = Enumerable.Range(0, Height).Select(_ => NewRelay(startDisabled: true)).ToArray();
		Input[] laterStages = Enumerable.Range(0, Height).Select(_ => new Input()).ToArray(), earlierStages = Enumerable.Range(0, Height).Select(_ => new Input()).ToArray();
		maxRows = Reachable.Where(a => Op(a) >> 12 == 0xD).Select(a => Op(a) & 0xF).DefaultIfEmpty(0).Max();
		Panel background = new(x + Width * pixelSize / 2, y + Height * pixelSize / 2, Width * pixelSize, Height * pixelSize, onColor: offColor, hasOff: false, startDisabled: false, foreground: ForegroundScreen);
		entities.AddRange(background.Entities);
		pixelGates = new Relay[Width, Height];
		Relay clear = NewRelay();
		Input clearAll = new();
		for (int px = 0; px < Width; px++)
		{
			for (int py = 0; py < Height; py++)
			{
				InstantToggle pixel = new(x, y, 5, 5);
				entities.AddRange(pixel.Entities);
				TagAll(pixel.Entities, "pixels");
				Panel panel = new(x + (px + 0.5) * pixelSize, y + (py + 0.5) * pixelSize, pixelSize, pixelSize, onColor: onColor, hasOff: false, foreground: ForegroundScreen);
				entities.AddRange(panel.Entities);
				// Clearing the screen only touches lit pixels: a pixel's picture change is an opacity change, which costs the game far more than
				// firing a disabled relay
				Relay clearIfLit = NewRelay(startDisabled: true);
				// What a pixel going off does to its picture: hide it, or with persistence arm the first of two stages; the end of a game frame
				// moves an armed first stage to the second and fires the second (hide), and lighting the pixel again disarms both
				Input goneOff = panel.Reset;
				Input relit = new();
				if (PixelHides is not null)
				{
					Relay first = NewRelay(startDisabled: true), second = NewRelay(startDisabled: true);
					first.Output.Connect(first.Disable.Concat(second.Enable).Concat(persistRows[py].Enable));
					second.Output.Connect(panel.Reset.Concat(second.Disable));
					goneOff = first.Enable.Concat(persistRows[py].Enable);
					relit = first.Disable.Concat(second.Disable);
					laterStages[py] = laterStages[py].Concat(second.Input);
					earlierStages[py] = earlierStages[py].Concat(first.Input);
					PixelHides[px, py] = second;
				}
				clearIfLit.Output.Connect(pixel.Clear.Concat(goneOff).Concat(clearIfLit.Disable));
				pixel.On.Connect(panel.Input.Concat(clearIfLit.Enable).Concat(relit));
				pixel.Off.Connect(goneOff.Concat(clearIfLit.Disable));
				pixel.Off.Connect(collisionLatch.Enable);
				clearAll = clearAll.Concat(clearIfLit.Input);
				Pixels[px, py] = pixel;
				pixelGates[px, py] = NewRelay(startDisabled: true);
				pixelGates[px, py].Output.Connect(pixel.Input);
			}
		}
		clear.Output.Connect(clearAll);
		clearScreen = clear;

		tag = "screen: columns";
		// Columns: ColOff[col][c] is enabled only for col = VX + c (mod 64, or nothing past the edge when sprites clip); OffsetLine[c] fires them all
		colOff = new Relay[Width, 8];
		offsetLines = new Relay[8];
		// ColBlock[c][b] (enabled while column VX + c is in block b of 8 columns) fires its block's ColOff relays: a lit pixel finds its column
		// in 16 actions, not 64
		Relay[,] colBlocks = new Relay[8, Width / 8];
		for (int c = 0; c < 8; c++)
		{
			offsetLines[c] = NewRelay();
			for (int col = 0; col < Width; col++)
			{
				colOff[col, c] = NewRelay(startDisabled: true);
				int column = col;
				colOff[col, c].Output.Connect(new Input(Enumerable.Range(0, Height).SelectMany(row => pixelGates[column, row].Input)));
			}
			int offset = c;
			for (int b = 0; b < Width / 8; b++)
			{
				colBlocks[c, b] = NewRelay(startDisabled: true);
				int block = b;
				colBlocks[c, b].Output.Connect(new Input(Enumerable.Range(block * 8, 8).SelectMany(col => colOff[col, offset].Input)));
			}
			offsetLines[c].Output.Connect(new Input(Enumerable.Range(0, Width / 8).SelectMany(b => colBlocks[offset, b].Input)));
		}
		// The top HalvedRows sprite rows arm their pixel gates by halves of the screen (columns 0 to 31 and 32 to 63): a row's arm goes through
		// a gate per half, which the x position enables for the halves its 8 columns reach, so arming a row enables 32 pixel gates, not 64
		// (both halves only when a sprite straddles the middle). The y position enables twice the rows' relays (one per half) for those rows.
		int halvedRows = Math.Min(maxRows, HalvedRows);
		Relay[,] halfArm = new Relay[Math.Max(halvedRows, 1), 2], halfDisarm = new Relay[Math.Max(halvedRows, 1), 2];
		for (int r = 0; r < halvedRows; r++) { for (int h = 0; h < 2; h++) { halfArm[r, h] = NewRelay(startDisabled: true); halfDisarm[r, h] = NewRelay(startDisabled: true); } }
		xDecoder = Decoder(Width, 6, "x");
		xCurrent = new Relay[Width];
		unsetX = NewRelay();
		xSelect = Enumerable.Range(0, Width).Select(_ => NewRelay()).ToArray();
		// xSelect is narrow: it prepares only the first NarrowColumns columns (Danm8ku's bullet sprites use columns 0 to 3), with its own
		// current value, groups and unset (unsetXNarrow); a draw that selects through it unsets through that
		xCurrentNarrow = new Relay[Width];
		unsetXNarrow = NewRelay();
		Relay[] xGroupsNarrow = Enumerable.Range(0, Width / 8).Select(_ => NewRelay(startDisabled: true)).ToArray();
		// Unsetting goes through a relay per 8 values, enabled while the current one is among them: 16 actions, not 64
		Relay[] xGroups = Enumerable.Range(0, Width / 8).Select(_ => NewRelay(startDisabled: true)).ToArray();
		for (int vx = 0; vx < Width; vx++)
		{
			xCurrent[vx] = NewRelay(startDisabled: true);
			IEnumerable<(int col, int c)> covered = Enumerable.Range(0, 8).Where(c => !quirks.ClipSprites || vx + c < Width).Select(c => ((vx + c) % Width, c));
			int[] halves = covered.Select(t => t.col / 32).Distinct().ToArray();
			Input HalfGates(bool on) => new(Enumerable.Range(0, halvedRows).SelectMany(r => halves.SelectMany(h => on ? halfArm[r, h].Enable.Concat(halfDisarm[r, h].Enable) : halfArm[r, h].Disable.Concat(halfDisarm[r, h].Disable))));
			Input select = new Input(covered.SelectMany(t => colOff[t.col, t.c].Enable.Concat(colBlocks[t.c, t.col / 8].Enable))).Concat(xCurrent[vx].Enable).Concat(xGroups[vx / 8].Enable)
				.Concat(HalfGates(true));
			xDecoder.Candidates[vx].Output.Connect(select);
			xCurrent[vx].Output.Connect(new Input(covered.SelectMany(t => colOff[t.col, t.c].Disable.Concat(colBlocks[t.c, t.col / 8].Disable))).Concat(HalfGates(false)).Concat(xCurrent[vx].Disable));
			var narrow = covered.Where(t => t.c < NarrowColumns).ToArray();
			int[] narrowHalves = narrow.Select(t => t.col / 32).Distinct().ToArray();
			Input NarrowHalfGates(bool on) => new(Enumerable.Range(0, halvedRows).SelectMany(r => narrowHalves.SelectMany(h => on ? halfArm[r, h].Enable.Concat(halfDisarm[r, h].Enable) : halfArm[r, h].Disable.Concat(halfDisarm[r, h].Disable))));
			xCurrentNarrow[vx] = NewRelay(startDisabled: true);
			xSelect[vx].Output.Connect(new Input(narrow.SelectMany(t => colOff[t.col, t.c].Enable.Concat(colBlocks[t.c, t.col / 8].Enable))).Concat(xCurrentNarrow[vx].Enable)
				.Concat(xGroupsNarrow[vx / 8].Enable).Concat(NarrowHalfGates(true)));
			xCurrentNarrow[vx].Output.Connect(new Input(narrow.SelectMany(t => colOff[t.col, t.c].Disable.Concat(colBlocks[t.c, t.col / 8].Disable))).Concat(NarrowHalfGates(false))
				.Concat(xCurrentNarrow[vx].Disable));
		}
		for (int g = 0; g < xGroupsNarrow.Length; g++) { int group = g; xGroupsNarrow[g].Output.Connect(xGroupsNarrow[g].Disable.Concat(new Input(Enumerable.Range(group * 8, 8).SelectMany(v => xCurrentNarrow[v].Input)))); }
		unsetXNarrow.Output.Connect(new Input(xGroupsNarrow.SelectMany(g => g.Input)));
		for (int g = 0; g < xGroups.Length; g++) { int group = g; xGroups[g].Output.Connect(xGroups[g].Disable.Concat(new Input(Enumerable.Range(group * 8, 8).SelectMany(v => xCurrent[v].Input)))); }
		unsetX.Output.Connect(new Input(xGroups.SelectMany(g => g.Input)));

		tag = "screen: rows";
		// Rows: RowOff[row][r] (enabled only for row = VY + r) arms that row's pixel gates, RowOffD disarms them; RowArm[r] fires all RowOff[*][r]
		// (per half h for the halved rows; h 0 covering all 64 columns for the rest)
		int Halves(int r) => r < halvedRows ? 2 : 1;
		IEnumerable<int> Columns(int r, int h) => Halves(r) == 2 ? Enumerable.Range(h * Width / 2, Width / 2) : Enumerable.Range(0, Width);
		Relay[,,] rowArm = new Relay[Height, Math.Max(maxRows, 1), 2], rowDisarm = new Relay[Height, Math.Max(maxRows, 1), 2];
		// The same in blocks of 8 rows: arming sprite row r fires 4 block relays and the 8 rows of the enabled one, not all 32
		Relay[,,] armBlocks = new Relay[Math.Max(maxRows, 1), 2, Height / 8], disarmBlocks = new Relay[Math.Max(maxRows, 1), 2, Height / 8];
		for (int r = 0; r < maxRows; r++)
		{
			Relay arm = NewRelay(), disarm = NewRelay();
			int spriteRow = r;
			for (int h = 0; h < Halves(r); h++)
			{
				int half = h;
				for (int row = 0; row < Height; row++)
				{
					int line = row;
					rowArm[row, r, h] = NewRelay(startDisabled: true);
					rowDisarm[row, r, h] = NewRelay(startDisabled: true);
					rowArm[row, r, h].Output.Connect(new Input(Columns(r, h).SelectMany(col => pixelGates[col, line].Enable)));
					rowDisarm[row, r, h].Output.Connect(new Input(Columns(r, h).SelectMany(col => pixelGates[col, line].Disable)));
				}
				for (int b = 0; b < Height / 8; b++)
				{
					armBlocks[r, h, b] = NewRelay(startDisabled: true);
					disarmBlocks[r, h, b] = NewRelay(startDisabled: true);
					armBlocks[r, h, b].Output.Connect(new Input(Enumerable.Range(b * 8, 8).SelectMany(row => rowArm[row, spriteRow, half].Input)));
					disarmBlocks[r, h, b].Output.Connect(new Input(Enumerable.Range(b * 8, 8).SelectMany(row => rowDisarm[row, spriteRow, half].Input)));
				}
				Input armHalf = new(Enumerable.Range(0, Height / 8).SelectMany(b => armBlocks[spriteRow, half, b].Input));
				Input disarmHalf = new(Enumerable.Range(0, Height / 8).SelectMany(b => disarmBlocks[spriteRow, half, b].Input));
				if (Halves(r) == 2) { halfArm[r, h].Output.Connect(armHalf); halfDisarm[r, h].Output.Connect(disarmHalf); }
				else { arm.Output.Connect(armHalf); disarm.Output.Connect(disarmHalf); }
			}
			if (Halves(r) == 2)
			{
				arm.Output.Connect(halfArm[r, 0].Input.Concat(halfArm[r, 1].Input));
				disarm.Output.Connect(halfDisarm[r, 0].Input.Concat(halfDisarm[r, 1].Input));
			}
			rowLines[r] = (arm, disarm);
		}
		// A y decoder per sprite height: its candidates prepare only that many rows (a 2-row sprite doesn't pay for the program's tallest)
		foreach (int height in Reachable.Where(a => Op(a) >> 12 == 0xD).Select(a => Op(a) & 0xF).Where(h => h > 0).Distinct())
		{
			DecoderRelays decoder = Decoder(Height, 5, $"y{height}");
			Relay[] current = new Relay[Height];
			Relay unset = NewRelay();
			Relay[] groups = Enumerable.Range(0, Height / 8).Select(_ => NewRelay(startDisabled: true)).ToArray();
			Relay[] select = ySelects[height] = Enumerable.Range(0, Height).Select(_ => NewRelay()).ToArray();
			for (int vy = 0; vy < Height; vy++)
			{
				current[vy] = NewRelay(startDisabled: true);
				IEnumerable<(int row, int r)> covered = Enumerable.Range(0, height).Where(r => !quirks.ClipSprites || vy + r < Height).Select(r => ((vy + r) % Height, r));
				Input effects = new Input(covered.SelectMany(t => Enumerable.Range(0, Halves(t.r)).SelectMany(h => rowArm[t.row, t.r, h].Enable.Concat(rowDisarm[t.row, t.r, h].Enable)
					.Concat(armBlocks[t.r, h, t.row / 8].Enable).Concat(disarmBlocks[t.r, h, t.row / 8].Enable)))).Concat(current[vy].Enable).Concat(groups[vy / 8].Enable);
				decoder.Candidates[vy].Output.Connect(effects);
				select[vy].Output.Connect(effects);
				current[vy].Output.Connect(new Input(covered.SelectMany(t => Enumerable.Range(0, Halves(t.r)).SelectMany(h => rowArm[t.row, t.r, h].Disable.Concat(rowDisarm[t.row, t.r, h].Disable)
					.Concat(armBlocks[t.r, h, t.row / 8].Disable).Concat(disarmBlocks[t.r, h, t.row / 8].Disable)))).Concat(current[vy].Disable));
			}
			for (int g = 0; g < groups.Length; g++) { int group = g; groups[g].Output.Connect(groups[g].Disable.Concat(new Input(Enumerable.Range(group * 8, 8).SelectMany(v => current[v].Input)))); }
			unset.Output.Connect(new Input(groups.SelectMany(g => g.Input)));
			yDecoders[height] = (decoder, unset);
		}
		for (int row = 0; row < Height; row++) { persistRows[row].Output.Connect(persistRows[row].Disable.Concat(laterStages[row]).Concat(earlierStages[row])); }
		persistTick.Output.Connect(new Input(persistRows.SelectMany(r => r.Input)));
		return persistTick;
	}
}
