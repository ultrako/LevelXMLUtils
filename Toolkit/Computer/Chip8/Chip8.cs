using LevelXMLUtils.Memory;
using LevelXMLUtils.Devices;
namespace LevelXMLUtils.Computer;

// The behaviours CHIP-8 interpreters disagree on. The defaults are the original COSMAC VIP interpreter's, which most early games were written
// for; later interpreters (CHIP-48, SUPER-CHIP) changed them, and some games depend on the later behaviour.
public record Chip8Quirks
{
	// 8XY6 and 8XYE shift VY into VX (VIP); otherwise they shift VX in place
	public bool ShiftUsesVY { get; init; } = true;
	// FX55 and FX65 leave I pointing past the last register stored or loaded (VIP); otherwise I is unchanged
	public bool LoadStoreIncrementsI { get; init; } = true;
	// BNNN jumps to NNN + V0 (VIP); otherwise BXNN jumps to XNN + VX
	public bool JumpUsesV0 { get; init; } = true;
	// 8XY1, 8XY2 and 8XY3 set VF to 0 (VIP, a side effect of how it computed them)
	public bool LogicResetsVF { get; init; } = true;
	// Sprites are cut off at the screen's edges (VIP); otherwise they wrap round. Either way the starting position wraps.
	public bool ClipSprites { get; init; } = true;
	// DXYN waits for the next frame (VIP: it drew in the vertical blank), so at most one sprite is drawn a frame
	public bool DisplayWait { get; init; } = true;
	// Where the hexadecimal digits are, which FX29 points I at (Octo puts them at 0)
	public int FontAddress { get; init; } = 0x050;
	// FX0A as Octo does it: the interpreter stops (the program counter already past FX0A) until a key is released, and VX is that key.
	// Otherwise it repeats until a key is held.
	public bool WaitForKeyRelease { get; init; } = false;
	// Not a behaviour of any interpreter, and changes nothing anyone can see: Octo's delay-timer wait loop (FX07, 3X00, a jump back to the
	// FX07) reached with the timer running would spin for the rest of the frame, so it ends the frame at once, with VX set to the timer and the
	// program counter where the spinning would have stopped (which instruction of the three depends on how many the frame had left, modulo 3).
	// With the timer at 0 the FX07 runs as usual. Only inside RunFrame, which knows the frame's length. Chip8Machine does it to save the spinning.
	public bool FuseTimerWaits { get; init; } = false;
	// Not a behaviour of any interpreter either, and it does change timing: each of these routines, when its code is found at its entry,
	// runs from the entry to one of its exits as one instruction (a game whose logic doesn't fit a frame runs faster than in Octo; nothing
	// else changes). Chip8Machine compiles them as one step, natively where it knows how (FusedRoutine.Danm8kuMoveBullet).
	public IReadOnlyList<FusedRoutine> FusedRoutines { get; init; } = Array.Empty<FusedRoutine>();

	public static Chip8Quirks Vip => new();
	// What the fused routines need on top of Octo's behaviour for Danm8ku (its bullet movement)
	public static Chip8Quirks Danm8kuFused => Octo with { FuseTimerWaits = true, FusedRoutines = new[]
		{ FusedRoutine.Danm8kuDrawBullets, FusedRoutine.Danm8kuActBullets, FusedRoutine.Danm8kuMoveBullet, FusedRoutine.Danm8kuDrawBullet, FusedRoutine.Danm8kuGogoBullet, FusedRoutine.Danm8kuPewPew, FusedRoutine.Danm8kuUpdatePlayer } };
	// What the CHIP-8 Archive's programs run on: Octo, with its quirk flags all off (each program may turn some on; see README.md in this folder)
	public static Chip8Quirks Octo => new() { ShiftUsesVY = true, LoadStoreIncrementsI = true, JumpUsesV0 = true, LogicResetsVF = false, ClipSprites = false, DisplayWait = false, FontAddress = 0, WaitForKeyRelease = true };
	public static Chip8Quirks Modern => new() { ShiftUsesVY = false, LoadStoreIncrementsI = false, JumpUsesV0 = true, LogicResetsVF = false, ClipSprites = true, DisplayWait = false };
}

// A routine run as one instruction (Chip8Quirks.FusedRoutines): its code from Entry on, and the addresses where the fused step stops
// (and other code it relies on, which has to be in place too)
public sealed record FusedRoutine(string Name, int Entry, ushort[] Code, int[] Exits, (int At, ushort[] Code)[]? Requires = null)
{
	static bool Matches(byte[] memory, int at, ushort[] code) => Enumerable.Range(0, code.Length).All(i => ((memory[at + 2 * i] << 8) | memory[at + 2 * i + 1]) == code[i]);
	public bool At(byte[] memory, int address) =>
		address == Entry && Matches(memory, Entry, Code) && (Requires ?? Array.Empty<(int, ushort[])>()).All(r => Matches(memory, r.At, r.Code));
	// Danm8ku's move_bullet (danm8ku.8o): a bullet's position, fractional position and velocity step on, its sprite offset is worked out,
	// and it stops before destroying a bullet that left the screen (the calls at 412 and 41C) or at its return
	public static readonly FusedRoutine Danm8kuMoveBullet = new("move_bullet", 0x3BC, new ushort[]
	{
		0x8C00, 0x800E, 0x8AF0, 0x8604, 0x80C0, 0x8AF2, 0x88F0, 0x88A5, 0x88A5, 0x8484, 0x8C10, 0x811E, 0x8AF0, 0x8714, 0x81C0, 0x8AF2,
		0x8DF0, 0x8DA5, 0x8DA5, 0x85D4, 0x6FFA, 0x8F07, 0x3F00, 0x13F2, 0x8024, 0x3F00, 0x8025, 0x8380, 0x6F01, 0x8F85, 0x4F00, 0x6302,
		0x6F01, 0x8FD5, 0x4F00, 0x6D02, 0x8DDE, 0x8DDE, 0x83D1, 0x6F3E, 0x8F45, 0x3F00, 0x1416, 0x23AC, 0x141E, 0x6F1D, 0x8F55, 0x4F00,
		0x23AC, 0x00EE,
	}, new[] { 0x412, 0x41C, 0x41E });
	// Danm8ku's bullet drawing in draw_bullets: the movement sprite for the bullet's offset (bul_nop + so * 4), drawn at the bullet
	public static readonly FusedRoutine Danm8kuDrawBullet = new("draw_bullet", 0x434, new ushort[] { 0xA272, 0x833E, 0x833E, 0xF31E, 0xD454 }, new[] { 0x43E });
	// Danm8ku's two loops over the bullet table, each as one step: draw_bullets draws every live bullet's movement sprite, act_bullets moves
	// every live bullet (move_bullet, which destroys one that left the screen with destroy_bullet) and saves it (save_bullet)
	static readonly ushort[] SaveBullet = { 0xABB8, 0xFB1E, 0xF755, 0x00EE };
	static readonly ushort[] DestroyBullet = { 0x60FF, 0x2420, 0xA29E, 0x833E, 0x833E, 0xF31E, 0xD454, 0x00EE };
	public static readonly FusedRoutine Danm8kuDrawBullets = new("draw_bullets", 0x428, new ushort[]
		{ 0x6B00, 0xABB8, 0xFB1E, 0xF765, 0x40FF, 0x143E, 0xA272, 0x833E, 0x833E, 0xF31E, 0xD454, 0x7B08, 0x3B00, 0x142A }, new[] { 0x444 });
	// gogo_bullet (0x45E): saves V0 to V7 at VSTACK_SPACE (0xB54), takes bullet_count (0x28E) and, if that slot's first byte is 255 (free),
	// makes it the new bullet's (VA = the count, the count + 8 stored, V8 1; else V8 0), restores V0 to V7, and leaves V3 0 and I at the slot
	public static readonly FusedRoutine Danm8kuGogoBullet = new("gogo_bullet", 0x45E, new ushort[]
		{ 0xAB54, 0xF755, 0xA28E, 0xF065, 0xABB8, 0xF01E, 0xF765, 0x6800, 0x30FF, 0x1480, 0xA28E, 0xF065, 0x8A00, 0x7008, 0xA28E, 0xF055, 0x6801,
		  0xAB54, 0xF765, 0x6300, 0xABB8, 0xFA1E }, new[] { 0x48A });
	// update_player (0x498): erases the player, moves it by the held keys within bounds, stores the position and draws it again
	public static readonly FusedRoutine Danm8kuUpdatePlayer = new("update_player", 0x498, new ushort[]
		{ 0xA28F, 0xF165, 0x8316, 0x8206, 0xA2AA, 0xD233, 0x6205, 0xE29E, 0x14B0, 0x71FF, 0x4101, 0x6102, 0x6207, 0xE29E, 0x14BC, 0x70FF, 0x4001,
		  0x6002, 0x6209, 0xE29E, 0x14C8, 0x7001, 0x4023, 0x6022, 0x6208, 0xE29E, 0x14D4, 0x7101, 0x413A, 0x6139, 0xA28F, 0xF155, 0x8316, 0x8206,
		  0xA2AA, 0xD233 }, new[] { 0x4E0 });
	// pew_pew (0x576): stores the new bullet's record at I, then draws bul (0x27E) at its position
	public static readonly FusedRoutine Danm8kuPewPew = new("pew_pew", 0x576, new ushort[] { 0xF755, 0xA27E, 0xD454 }, new[] { 0x57C });
	public static readonly FusedRoutine Danm8kuActBullets = new("act_bullets", 0x446, new ushort[]
		{ 0x6B00, 0xABB8, 0xFB1E, 0xF765, 0x30FF, 0x23BC, 0x30FF, 0x2420, 0x7B08, 0x3B00, 0x1448 }, new[] { 0x45C },
		new[] { (0x3BC, Danm8kuMoveBullet.Code), (0x420, SaveBullet), (0x3AC, DestroyBullet) });
}

// A CHIP-8 interpreter in plain C#: the reference a trigger-built CHIP-8 is checked against, and a way to measure what a game needs
// (instructions per frame, which addresses it writes, how deep it calls, how much it draws) before building anything in triggers.
public class Chip8
{
	public const int Width = 64, Height = 32, MemorySize = 4096, ProgramStart = 0x200, StackSize = 16;
	// The built-in hexadecimal digits 0 to F, 5 bytes each, which FX29 points I at
	public static readonly byte[] Font =
	{
		0xF0, 0x90, 0x90, 0x90, 0xF0, 0x20, 0x60, 0x20, 0x20, 0x70, 0xF0, 0x10, 0xF0, 0x80, 0xF0, 0xF0, 0x10, 0xF0, 0x10, 0xF0,
		0x90, 0x90, 0xF0, 0x10, 0x10, 0xF0, 0x80, 0xF0, 0x10, 0xF0, 0xF0, 0x80, 0xF0, 0x90, 0xF0, 0xF0, 0x10, 0x20, 0x40, 0x40,
		0xF0, 0x90, 0xF0, 0x90, 0xF0, 0xF0, 0x90, 0xF0, 0x10, 0xF0, 0xF0, 0x90, 0xF0, 0x90, 0x90, 0xE0, 0x90, 0xE0, 0x90, 0xE0,
		0xF0, 0x80, 0x80, 0x80, 0xF0, 0xE0, 0x90, 0x90, 0x90, 0xE0, 0xF0, 0x80, 0xF0, 0x80, 0xF0, 0xF0, 0x80, 0xF0, 0x80, 0x80,
	};

	public readonly byte[] Memory = new byte[MemorySize];
	public readonly byte[] V = new byte[16];
	public ushort I;
	public ushort PC = ProgramStart;
	// Called as a fused routine starts (with the machine's state as it enters it)
	public Action<FusedRoutine>? FusedStarted;
	public readonly ushort[] Stack = new ushort[StackSize];
	public int SP;
	public byte DelayTimer, SoundTimer;
	public readonly bool[,] Display = new bool[Width, Height];
	// Set whenever a pixel turns on, never cleared here: for checking what a display shows of pixels lit only for part of a frame
	public readonly bool[,] TurnedOn = new bool[Width, Height];
	public readonly bool[] Keys = new bool[16];
	public readonly Chip8Quirks Quirks;
	// Stopped at an instruction it can't run (an unknown opcode, a stack over- or underflow, a jump out of memory); Step does nothing then
	public string? Halted { get; private set; }
	readonly Random random;

	// What the game has done so far, for sizing a trigger-built CHIP-8
	public long Instructions { get; private set; }
	public readonly HashSet<int> Written = new();
	public readonly HashSet<int> ReadAsData = new();
	public readonly HashSet<int> Executed = new();
	public readonly Dictionary<string, long> OpcodeCounts = new();
	public int MaxStackDepth { get; private set; }
	public long SpritesDrawn { get; private set; }
	public long PixelsFlipped { get; private set; }
	// FX0A waiting for a key: the instruction repeats until one is pressed
	public bool WaitingForKey { get; private set; }
	int waitRegister;
	// Octo's FX0A: a key was released (the controls' "off"); while waiting for one, VX becomes it and the interpreter carries on
	public void ReleaseKey(int key)
	{
		if (Quirks.WaitForKeyRelease && WaitingForKey) { V[waitRegister] = (byte)key; WaitingForKey = false; }
	}
	// Where CXNN's random byte comes from; null for a seeded Random. A trigger-built CHIP-8 has no random trigger and uses a frame counter,
	// so a test checking one against this interpreter gives it the same counter.
	public Func<int>? RandomSource;

	public Chip8(byte[] program, Chip8Quirks? quirks = null, int seed = 0)
	{
		if (program.Length > MemorySize - ProgramStart) { throw new ArgumentException($"a program can be at most {MemorySize - ProgramStart} bytes, this one is {program.Length}"); }
		Quirks = quirks ?? Chip8Quirks.Vip;
		random = new Random(seed);
		Font.CopyTo(Memory, Quirks.FontAddress);
		program.CopyTo(Memory, ProgramStart);
	}

	byte Read(int address) { address &= 0xFFF; ReadAsData.Add(address); return Memory[address]; }
	void Write(int address, byte value) { address &= 0xFFF; Written.Add(address); Memory[address] = value; }

	// One instruction (a fused routine counts as one)
	public void Step()
	{
		FusedRoutine? fused = Quirks.FusedRoutines.FirstOrDefault(f => f.At(Memory, PC));
		if (fused is not null) { FusedStarted?.Invoke(fused); }
		if (fused is not null && Halted is null)
		{
			long before = Instructions;
			for (int guard = 0; guard < 10000; guard++)
			{
				StepOne();
				if (Halted is not null || fused.Exits.Contains(PC)) { break; }
			}
			Instructions = before + 1;
			OpcodeCounts[$"fused {fused.Name}"] = OpcodeCounts.GetValueOrDefault($"fused {fused.Name}") + 1;
			return;
		}
		Stepping?.Invoke(PC);
		StepOne();
	}
	// Called before each instruction that isn't part of a fused routine, with its address
	public Action<ushort>? Stepping;
	void StepOne()
	{
		if (Halted is not null) { return; }
		if (Quirks.WaitForKeyRelease && WaitingForKey) { return; }
		if (PC > MemorySize - 2) { Halted = $"PC {PC:X3} is past the end of memory"; return; }
		Executed.Add(PC); Executed.Add(PC + 1);
		int op = (Memory[PC] << 8) | Memory[PC + 1];
		if (Quirks.FuseTimerWaits && frameLength > 0 && DelayTimer != 0 && IsTimerWait(PC))
		{
			// The spinning would run the loop's three instructions in turn for the frame's remaining frameLength - frameSlot instructions
			V[(op >> 8) & 0xF] = DelayTimer;
			PC = (ushort)(PC + 2 * ((frameLength - frameSlot) % 3));
			Instructions += frameLength - frameSlot;
			OpcodeCounts["fused timer wait"] = OpcodeCounts.GetValueOrDefault("fused timer wait") + 1;
			WaitedForTimer = true;
			return;
		}
		PC += 2;
		Instructions++;
		int x = (op >> 8) & 0xF, y = (op >> 4) & 0xF, n = op & 0xF, nn = op & 0xFF, nnn = op & 0xFFF;
		string name;
		switch (op >> 12)
		{
			case 0x0 when op == 0x00E0:
				name = "00E0 clear";
				for (int px = 0; px < Width; px++) { for (int py = 0; py < Height; py++) { if (Display[px, py]) { Display[px, py] = false; PixelsFlipped++; } } }
				break;
			case 0x0 when op == 0x00EE:
				name = "00EE return";
				if (SP == 0) { Halted = $"return with an empty stack at {PC - 2:X3}"; return; }
				PC = Stack[--SP];
				break;
			case 0x0:
				name = "0NNN machine code";
				Halted = $"machine code call {op:X4} at {PC - 2:X3} (only the VIP's own code could run these)";
				return;
			case 0x1: name = "1NNN jump"; PC = (ushort)nnn; break;
			case 0x2:
				name = "2NNN call";
				if (SP == StackSize) { Halted = $"call with a full stack at {PC - 2:X3}"; return; }
				Stack[SP++] = PC;
				MaxStackDepth = Math.Max(MaxStackDepth, SP);
				PC = (ushort)nnn;
				break;
			case 0x3: name = "3XNN skip if equal"; if (V[x] == nn) { PC += 2; } break;
			case 0x4: name = "4XNN skip if not equal"; if (V[x] != nn) { PC += 2; } break;
			case 0x5 when n == 0: name = "5XY0 skip if registers equal"; if (V[x] == V[y]) { PC += 2; } break;
			case 0x6: name = "6XNN load"; V[x] = (byte)nn; break;
			case 0x7: name = "7XNN add"; V[x] = (byte)(V[x] + nn); break;
			case 0x8:
				switch (n)
				{
					case 0x0: name = "8XY0 move"; V[x] = V[y]; break;
					case 0x1: name = "8XY1 or"; V[x] |= V[y]; if (Quirks.LogicResetsVF) { V[0xF] = 0; } break;
					case 0x2: name = "8XY2 and"; V[x] &= V[y]; if (Quirks.LogicResetsVF) { V[0xF] = 0; } break;
					case 0x3: name = "8XY3 xor"; V[x] ^= V[y]; if (Quirks.LogicResetsVF) { V[0xF] = 0; } break;
					case 0x4: { name = "8XY4 add with carry"; int sum = V[x] + V[y]; V[x] = (byte)sum; V[0xF] = (byte)(sum > 0xFF ? 1 : 0); break; }
					case 0x5: { name = "8XY5 subtract"; int vx = V[x], vy = V[y]; V[x] = (byte)(vx - vy); V[0xF] = (byte)(vx >= vy ? 1 : 0); break; }
					case 0x6: { name = "8XY6 shift right"; byte source = Quirks.ShiftUsesVY ? V[y] : V[x]; V[x] = (byte)(source >> 1); V[0xF] = (byte)(source & 1); break; }
					case 0x7: { name = "8XY7 reverse subtract"; int vx = V[x], vy = V[y]; V[x] = (byte)(vy - vx); V[0xF] = (byte)(vy >= vx ? 1 : 0); break; }
					case 0xE: { name = "8XYE shift left"; byte source = Quirks.ShiftUsesVY ? V[y] : V[x]; V[x] = (byte)(source << 1); V[0xF] = (byte)(source >> 7); break; }
					default: Halted = $"unknown opcode {op:X4} at {PC - 2:X3}"; return;
				}
				break;
			case 0x9 when n == 0: name = "9XY0 skip if registers differ"; if (V[x] != V[y]) { PC += 2; } break;
			case 0xA: name = "ANNN load I"; I = (ushort)nnn; break;
			case 0xB: name = "BNNN jump plus V0"; PC = (ushort)((nnn + (Quirks.JumpUsesV0 ? V[0] : V[x])) & 0xFFF); break;
			case 0xC: name = "CXNN random"; V[x] = (byte)((RandomSource?.Invoke() ?? random.Next(256)) & nn); break;
			case 0xD: name = "DXYN draw"; Draw(V[x], V[y], n); break;
			case 0xE when nn == 0x9E: name = "EX9E skip if key"; if (Keys[V[x] & 0xF]) { PC += 2; } break;
			case 0xE when nn == 0xA1: name = "EXA1 skip if not key"; if (!Keys[V[x] & 0xF]) { PC += 2; } break;
			case 0xF:
				switch (nn)
				{
					case 0x07: name = "FX07 read delay timer"; V[x] = DelayTimer; break;
					case 0x0A:
						name = "FX0A wait for key";
						if (Quirks.WaitForKeyRelease) { WaitingForKey = true; waitRegister = x; break; }
						int key = Array.IndexOf(Keys, true);
						if (key < 0) { WaitingForKey = true; PC -= 2; } else { WaitingForKey = false; V[x] = (byte)key; }
						break;
					case 0x15: name = "FX15 set delay timer"; DelayTimer = V[x]; break;
					case 0x18: name = "FX18 set sound timer"; SoundTimer = V[x]; break;
					case 0x1E: name = "FX1E add to I"; I = (ushort)((I + V[x]) & 0xFFFF); break;
					case 0x29: name = "FX29 point I at digit"; I = (ushort)(Quirks.FontAddress + 5 * (V[x] & 0xF)); break;
					case 0x33:
						name = "FX33 decimal digits";
						Write(I, (byte)(V[x] / 100)); Write(I + 1, (byte)(V[x] / 10 % 10)); Write(I + 2, (byte)(V[x] % 10));
						break;
					case 0x55:
						name = "FX55 store registers";
						for (int r = 0; r <= x; r++) { Write(I + r, V[r]); }
						if (Quirks.LoadStoreIncrementsI) { I = (ushort)(I + x + 1); }
						break;
					case 0x65:
						name = "FX65 load registers";
						for (int r = 0; r <= x; r++) { V[r] = Read(I + r); }
						if (Quirks.LoadStoreIncrementsI) { I = (ushort)(I + x + 1); }
						break;
					default: Halted = $"unknown opcode {op:X4} at {PC - 2:X3}"; return;
				}
				break;
			default: Halted = $"unknown opcode {op:X4} at {PC - 2:X3}"; return;
		}
		OpcodeCounts[name] = OpcodeCounts.GetValueOrDefault(name) + 1;
	}

	// DXYN: N rows of a sprite from I, XORed onto the screen at (VX, VY); VF is 1 when it turned any lit pixel off
	void Draw(byte vx, byte vy, int rows)
	{
		SpritesDrawn++;
		int startX = vx % Width, startY = vy % Height;
		V[0xF] = 0;
		for (int row = 0; row < rows; row++)
		{
			int py = startY + row;
			if (py >= Height) { if (Quirks.ClipSprites) { break; } py %= Height; }
			byte bits = Read(I + row);
			for (int col = 0; col < 8; col++)
			{
				if (((bits >> (7 - col)) & 1) == 0) { continue; }
				int px = startX + col;
				if (px >= Width) { if (Quirks.ClipSprites) { continue; } px %= Width; }
				if (Display[px, py]) { V[0xF] = 1; }
				Display[px, py] = !Display[px, py];
				if (Display[px, py]) { TurnedOn[px, py] = true; }
				PixelsFlipped++;
			}
		}
	}

	// Octo's delay-timer wait loop at `address`: FX07, 3X00 (the same X), a jump back to the FX07
	public bool IsTimerWait(int address)
	{
		if (address > MemorySize - 6) { return false; }
		int a = (Memory[address] << 8) | Memory[address + 1], b = (Memory[address + 2] << 8) | Memory[address + 3], c = (Memory[address + 4] << 8) | Memory[address + 5];
		int x = (a >> 8) & 0xF;
		return (a & 0xF0FF) == 0xF007 && b == (0x3000 | (x << 8)) && c == (0x1000 | address);
	}
	// A fused timer wait ended this frame's instructions
	public bool WaitedForTimer { get; private set; }
	int frameLength, frameSlot;

	// The 60 Hz timers count down once per tick
	public void TickTimers()
	{
		if (DelayTimer > 0) { DelayTimer--; }
		if (SoundTimer > 0) { SoundTimer--; }
	}

	// A 60 Hz frame: instructionsPerFrame instructions (or up to the first sprite drawn, with DisplayWait), then a timer tick
	public void RunFrame(int instructionsPerFrame)
	{
		WaitedForTimer = false;
		frameLength = instructionsPerFrame;
		for (int i = 0; i < instructionsPerFrame && Halted is null && !WaitedForTimer; i++)
		{
			long drawn = SpritesDrawn;
			frameSlot = i;
			Step();
			if (Quirks.DisplayWait && SpritesDrawn > drawn) { break; }
		}
		frameLength = 0;
		TickTimers();
	}

	// The screen as text, '#' for a lit pixel
	public string Screen()
	{
		var text = new System.Text.StringBuilder();
		for (int py = 0; py < Height; py++)
		{
			for (int px = 0; px < Width; px++) { text.Append(Display[px, py] ? '#' : '.'); }
			text.Append('\n');
		}
		return text.ToString();
	}

	// Contiguous address ranges, for reporting which parts of memory a game writes or reads
	public static string Ranges(IEnumerable<int> addresses)
	{
		var sorted = addresses.OrderBy(a => a).ToList();
		var parts = new List<string>();
		for (int i = 0; i < sorted.Count;)
		{
			int start = sorted[i], end = start;
			while (i + 1 < sorted.Count && sorted[i + 1] == end + 1) { end = sorted[++i]; }
			i++;
			parts.Add(start == end ? $"{start:X3}" : $"{start:X3}-{end:X3}");
		}
		return parts.Count == 0 ? "none" : string.Join(", ", parts);
	}
}

// CXNN's random numbers in a trigger-built CHIP-8 (Chip8Machine), which has no random trigger: a counter that steps once each game frame
// (so the player's timing mixes in) and once each CXNN (so every call gets a new value), looked up in a fixed shuffle of 0 to 255 (so
// consecutive values are scattered, not counting up). Octo uses Math.random, so no generator could repeat a run of the archive's games;
// this one only has to look random. A test gives the reference interpreter one of these (RandomSource = random.Next, Frame each frame).
public class Chip8Random
{
	public static readonly byte[] Table = Shuffled();
	static byte[] Shuffled()
	{
		byte[] table = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
		Random random = new(0xC8);
		for (int i = table.Length - 1; i > 0; i--) { int j = random.Next(i + 1); (table[i], table[j]) = (table[j], table[i]); }
		return table;
	}
	int counter;
	public void Frame() => counter = (counter + 1) & 0xFF;
	public int Next() { int value = Table[counter]; counter = (counter + 1) & 0xFF; return value; }
}

