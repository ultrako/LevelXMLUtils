using Xunit;
using LevelXMLUtils.Computer;
namespace LevelXMLUtils.Tests;

// The reference CHIP-8 interpreter, on small hand-assembled programs. The expected values come from the instruction set as documented
// (Cowgod's reference and the VIP behaviour the quirks name), not from running the interpreter.
public class Chip8Tests
{
	// A program from 16-bit opcodes, run one instruction per opcode given unless told otherwise
	static Chip8 Run(ushort[] ops, int? steps = null, Chip8Quirks? quirks = null, byte[]? data = null)
	{
		byte[] program = ops.SelectMany(op => new[] { (byte)(op >> 8), (byte)op }).Concat(data ?? Array.Empty<byte>()).ToArray();
		Chip8 chip = new(program, quirks);
		for (int i = 0; i < (steps ?? ops.Length); i++) { chip.Step(); }
		Assert.Null(chip.Halted);
		return chip;
	}

	[Fact]
	public void LoadAndAdd_WrapWithoutTouchingVF()
	{
		Chip8 chip = Run(new ushort[] { 0x6AFE, 0x7A05, 0x6F07, 0x7F01 });
		Assert.Equal(0x03, chip.V[0xA]);
		Assert.Equal(0x08, chip.V[0xF]);
	}

	[Theory]
	[InlineData(0xFF, 0x01, 0x00, 1)]
	[InlineData(0x10, 0x20, 0x30, 0)]
	public void AddWithCarry_SetsVFOnOverflow(int a, int b, int sum, int carry)
	{
		Chip8 chip = Run(new ushort[] { (ushort)(0x6100 | a), (ushort)(0x6200 | b), 0x8124 });
		Assert.Equal(sum, chip.V[1]);
		Assert.Equal(carry, chip.V[0xF]);
	}

	[Theory]
	[InlineData(0x30, 0x10, 0x20, 1)]
	[InlineData(0x10, 0x30, 0xE0, 0)]
	[InlineData(0x10, 0x10, 0x00, 1)]
	public void Subtract_VFIsOneWhenNoBorrow(int a, int b, int difference, int noBorrow)
	{
		Chip8 chip = Run(new ushort[] { (ushort)(0x6100 | a), (ushort)(0x6200 | b), 0x8125 });
		Assert.Equal(difference, chip.V[1]);
		Assert.Equal(noBorrow, chip.V[0xF]);
		Chip8 reverse = Run(new ushort[] { (ushort)(0x6100 | b), (ushort)(0x6200 | a), 0x8127 });
		Assert.Equal(difference, reverse.V[1]);
		Assert.Equal(noBorrow, reverse.V[0xF]);
	}

	[Fact]
	public void TheFlagIsWrittenLast_WhenVFIsAlsoTheResult()
	{
		// VF = 0xFF + 0x01: the sum is 0 but the carry, written after it, wins
		Chip8 chip = Run(new ushort[] { 0x6FFF, 0x6101, 0x8F14 });
		Assert.Equal(1, chip.V[0xF]);
	}

	[Fact]
	public void LogicOps_AndTheVIPResettingVF()
	{
		Chip8 chip = Run(new ushort[] { 0x61F0, 0x623C, 0x6F09, 0x8121 });
		Assert.Equal(0xFC, chip.V[1]);
		Assert.Equal(0, chip.V[0xF]);
		Chip8 modern = Run(new ushort[] { 0x61F0, 0x623C, 0x6F09, 0x8122 }, quirks: Chip8Quirks.Modern);
		Assert.Equal(0x30, modern.V[1]);
		Assert.Equal(9, modern.V[0xF]);
		Assert.Equal(0xCC, Run(new ushort[] { 0x61F0, 0x623C, 0x8123 }).V[1]);
	}

	[Fact]
	public void Shifts_TakeVYOnTheVIP_AndVXOtherwise()
	{
		Chip8 vip = Run(new ushort[] { 0x6100, 0x6281, 0x8126 });
		Assert.Equal(0x40, vip.V[1]);
		Assert.Equal(1, vip.V[0xF]);
		Chip8 modern = Run(new ushort[] { 0x6181, 0x6200, 0x812E }, quirks: Chip8Quirks.Modern);
		Assert.Equal(0x02, modern.V[1]);
		Assert.Equal(1, modern.V[0xF]);
	}

	[Fact]
	public void Skips_SkipOneInstruction()
	{
		// 3XNN taken skips the 6XNN after it; 4XNN not taken runs its next one
		Chip8 chip = Run(new ushort[] { 0x6105, 0x3105, 0x6201, 0x4105, 0x6301, 0x6202, 0x5120, 0x6401 }, steps: 7);
		Assert.Equal(1, chip.V[3]);
		Assert.Equal(2, chip.V[2]);
		Assert.Equal(1, chip.V[4]);
	}

	[Fact]
	public void CallAndReturn_UseTheStack()
	{
		// 200: call 206; 202: V1 = 1; 204: jump 204; 206: V2 = 2; 208: return
		Chip8 chip = Run(new ushort[] { 0x2206, 0x6101, 0x1204, 0x6202, 0x00EE }, steps: 5);
		Assert.Equal(1, chip.V[1]);
		Assert.Equal(2, chip.V[2]);
		Assert.Equal(0, chip.SP);
		Assert.Equal(1, chip.MaxStackDepth);
		Assert.Equal(0x204, chip.PC);
	}

	[Fact]
	public void JumpPlusV0()
	{
		Chip8 chip = Run(new ushort[] { 0x6004, 0xB202, 0x6101, 0x6202 }, steps: 3);
		Assert.Equal(0, chip.V[1]);
		Assert.Equal(2, chip.V[2]);
	}

	[Fact]
	public void DecimalDigits_AndStoringAndLoadingRegisters()
	{
		// V0 = 254: digits 2, 5, 4 at 0x300; then load them back into V1..V3 through FX65 (I moved by the VIP quirk)
		Chip8 chip = Run(new ushort[] { 0x60FE, 0xA300, 0xF033, 0xA300, 0xF265 });
		Assert.Equal(new byte[] { 2, 5, 4 }, chip.Memory.Skip(0x300).Take(3).ToArray());
		Assert.Equal(new byte[] { 2, 5, 4 }, chip.V.Take(3).ToArray());
		Assert.Equal(0x303, chip.I);
		Assert.Equal(new[] { 0x300, 0x301, 0x302 }, chip.Written.OrderBy(a => a).ToArray());
		Chip8 modern = Run(new ushort[] { 0x60FE, 0xA300, 0xF033, 0xA300, 0xF265 }, quirks: Chip8Quirks.Modern);
		Assert.Equal(0x300, modern.I);
	}

	[Fact]
	public void DrawingXors_AndReportsAPixelTurnedOff()
	{
		// The digit 0 at (0, 0): its top row is F0, so (0..3, 0) light up and (4, 0) stays dark; drawing it again erases it and sets VF
		Chip8 chip = Run(new ushort[] { 0x6000, 0xF029, 0xD005 });
		Assert.True(chip.Display[0, 0] && chip.Display[3, 0] && !chip.Display[4, 0]);
		Assert.True(chip.Display[0, 1] && !chip.Display[1, 1] && chip.Display[3, 1]);
		Assert.Equal(0, chip.V[0xF]);
		Chip8 twice = Run(new ushort[] { 0x6000, 0xF029, 0xD005, 0xD005 });
		Assert.False(Enumerable.Range(0, 8).Any(px => Enumerable.Range(0, 5).Any(py => twice.Display[px, py])));
		Assert.Equal(1, twice.V[0xF]);
	}

	[Fact]
	public void SpritesClipAtTheEdges_ButTheirStartWraps()
	{
		// A full 8 by 1 bar (byte FF at 0x20A, after the 5 opcodes) at x = 60: VIP clips it to 60..63; modern wraps the rest to 0..3
		ushort[] ops = { 0x613C, 0x6200, 0xA20A, 0xD121, 0x1208 };
		Chip8 clipped = Run(ops, steps: 4, data: new byte[] { 0xFF });
		Assert.True(clipped.Display[63, 0]);
		Assert.False(clipped.Display[0, 0]);
		Chip8 wrapped = Run(ops, steps: 4, quirks: Chip8Quirks.Vip with { ClipSprites = false }, data: new byte[] { 0xFF });
		Assert.True(wrapped.Display[0, 0] && wrapped.Display[3, 0] && !wrapped.Display[4, 0]);
		// x = 64 + 2 wraps to 2 before drawing
		Chip8 start = Run(new ushort[] { 0x6142, 0x6200, 0xA20A, 0xD121, 0x1208 }, steps: 4, data: new byte[] { 0x80 });
		Assert.True(start.Display[2, 0]);
	}

	[Fact]
	public void DisplayWait_EndsTheFrameAtASprite()
	{
		// draw, draw, then add: with the VIP's display wait each frame stops at its first sprite
		ushort[] ops = { 0xF029, 0xD005, 0xD005, 0x7101 };
		byte[] program = ops.SelectMany(op => new[] { (byte)(op >> 8), (byte)op }).ToArray();
		Chip8 vip = new(program);
		vip.RunFrame(10);
		Assert.Equal(2, vip.Instructions);
		vip.RunFrame(10);
		Assert.Equal(3, vip.Instructions);
		Chip8 modern = new(program, Chip8Quirks.Modern);
		modern.RunFrame(4);
		Assert.Equal(4, modern.Instructions);
	}

	[Fact]
	public void Timers_CountDownOncePerTick()
	{
		Chip8 chip = Run(new ushort[] { 0x6103, 0xF115, 0xF118 });
		chip.TickTimers(); chip.TickTimers();
		Assert.Equal(1, chip.DelayTimer);
		chip.TickTimers(); chip.TickTimers();
		Assert.Equal(0, chip.DelayTimer);
		Assert.Equal(0, chip.SoundTimer);
	}

	[Fact]
	public void Keys_SkipAndWait()
	{
		// FX0A repeats until a key is down, then puts its number in VX
		Chip8 chip = new(new byte[] { 0xF3, 0x0A, 0x61, 0x01 });
		chip.Step(); chip.Step();
		Assert.True(chip.WaitingForKey);
		Assert.Equal(0x200, chip.PC);
		chip.Keys[0xB] = true;
		chip.Step();
		Assert.Equal(0xB, chip.V[3]);
		Assert.Equal(0x202, chip.PC);
		// EX9E skips while the key is down
		Chip8 skip = new(new byte[] { 0x60, 0x0B, 0xE0, 0x9E, 0x61, 0x01, 0x62, 0x02 });
		skip.Keys[0xB] = true;
		for (int i = 0; i < 3; i++) { skip.Step(); }
		Assert.Equal(0, skip.V[1]);
		Assert.Equal(2, skip.V[2]);
	}

	[Fact]
	public void OctosWaitForKey_StopsUntilAKeyIsReleased()
	{
		// FX0A, then V1 = 1: nothing runs while waiting (the timers would still tick), and the released key ends up in V3
		Chip8 chip = new(new byte[] { 0xF3, 0x0A, 0x61, 0x01 }, Chip8Quirks.Octo);
		chip.Step();
		Assert.True(chip.WaitingForKey);
		Assert.Equal(0x202, chip.PC);
		chip.Keys[5] = true;
		chip.Step(); chip.Step();
		Assert.Equal(0, chip.V[1]);
		chip.Keys[5] = false;
		chip.ReleaseKey(5);
		Assert.Equal(5, chip.V[3]);
		chip.Step();
		Assert.Equal(1, chip.V[1]);
	}

	[Fact]
	public void RandomIsMasked()
	{
		Chip8 chip = Run(Enumerable.Repeat((ushort)0xC10F, 50).ToArray());
		Assert.True(chip.V[1] <= 0x0F);
	}

	[Fact]
	public void AddToI_AndPointAtADigit()
	{
		Chip8 chip = Run(new ushort[] { 0xA100, 0x6105, 0xF11E });
		Assert.Equal(0x105, chip.I);
		Assert.Equal(0x050 + 5 * 0xA, Run(new ushort[] { 0x610A, 0xF129 }).I);
		Assert.Equal(5 * 0xA, Run(new ushort[] { 0x610A, 0xF129 }, quirks: Chip8Quirks.Octo).I);
	}

	[Fact]
	public void BadPrograms_HaltWithAReason()
	{
		Chip8 chip = new(new byte[] { 0x00, 0xEE });
		chip.Step();
		Assert.Contains("empty stack", chip.Halted);
		Chip8 unknown = new(new byte[] { 0x80, 0x0F });
		unknown.Step();
		Assert.Contains("unknown opcode 800F", unknown.Halted);
	}
}
