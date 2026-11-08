using LevelXML;
using LevelXMLUtils.Memory;
using LevelXMLUtils.Devices;
using LevelXMLUtils.Controls;
namespace LevelXMLUtils.Computer;

public class Processor : TriggerContraption
{
    public struct Opcode
		{
			readonly uint val;
			public static readonly Opcode NoOperation = 0;
			public static readonly Opcode MoveConstToAddr = 1;
			public static readonly Opcode MoveAddrToAddr = 2;
			public static readonly Opcode MoveIndirectToAddr = 3;
			public static readonly Opcode MoveConstToAddrIndirect = 4;
			public static readonly Opcode MoveAddrToAddrIndirect = 5;
			public static readonly Opcode MoveConstToRegister = 6;
			public static readonly Opcode MoveRegisterToRegister = 7;
			public static readonly Opcode MoveConstToRegisterIndirect = 8;
			public static readonly Opcode MoveRegisterToRegisterIndirect = 9;
			public static readonly Opcode MoveAddrToRegister = 10;
			public static readonly Opcode AddConstToAddr = 11;
			public static readonly Opcode AddAddrToAddr = 12;
			public static readonly Opcode AddIndirectToAddr = 13;
			public static readonly Opcode AddConstToRegister = 14;
			public static readonly Opcode AddRegisterToRegister = 15;
			public static readonly Opcode AddRegisterToAddr = 16;
			public static readonly Opcode IncrementRegister = 17;
			public static readonly Opcode SubConstFromAddr = 18;
			public static readonly Opcode SubAddrFromAddr = 19;
			public static readonly Opcode SubIndirectFromAddr = 20;
			public static readonly Opcode SubRegisterFromAddr = 21;
			public static readonly Opcode SubRegisterFromRegister = 22;
			public static readonly Opcode CmpConstFromAddr = 23;
			public static readonly Opcode CmpAddrFromAddr = 24;
			public static readonly Opcode CmpIndirectFromAddr = 25;
			public static readonly Opcode CmpConstFromRegister = 26;
			public static readonly Opcode CmpRegisterFromRegister = 27;
			public static readonly Opcode JumpToConst = 28;
			public static readonly Opcode JumpToAddr = 29;
			public static readonly Opcode JumpToIndirect = 30;
			public static readonly Opcode JumpToRegister = 31;
			public static readonly Opcode JumpIfGreater = 32;
			public static readonly Opcode JumpIfGreaterOrEqual = 33;
			public static readonly Opcode JumpIfLess = 34;
			public static readonly Opcode JumpIfLessOrEqual = 35;
			public static readonly Opcode JumpIfZero = 36;
			public static readonly Opcode JumpIfNotZero = 37;
			public static readonly Opcode MulConstWithAddr = 38;
			public static readonly Opcode MulConstWithRegister = 39;
			public static readonly Opcode StopStepping = 40;
			public static readonly Opcode ResumeSteppingAfterFrame = 41;
			public static readonly Opcode PushConst = 42;
			public static readonly Opcode PushRegister = 43;
			public static readonly Opcode PopRegister = 44;
			public static readonly Opcode PushAddr = 45;
			public static readonly Opcode PlaySound = 46;
			public static readonly Opcode DrawAtConst = 47;
			public static readonly Opcode DrawAtRegister = 48;
			public static readonly Opcode ClearScreen = 49;
			public static readonly Opcode AuxillaryOne = 50;
			public static readonly Opcode AuxillaryTwo = 51;
			// public static readonly Opcode PlaySoundTwo = 52;
			public static readonly int NumberOfOpcodes = 52;
			public Opcode(uint val) { this.val = val; }
			public static implicit operator Opcode(uint val) { return new Opcode(val); }
			public static implicit operator uint(Opcode ins) { return ins.val; }
		}
	public static IList<Opcode> Opcodes = new List<Opcode>()
	{

		Opcode.MoveConstToAddr,
		Opcode.MoveAddrToAddr,
		Opcode.MoveIndirectToAddr,
		Opcode.MoveConstToAddrIndirect,
		Opcode.MoveConstToRegister,
		Opcode.MoveRegisterToRegister,
		Opcode.MoveConstToRegisterIndirect,
		Opcode.MoveRegisterToRegisterIndirect,
		Opcode.MoveAddrToRegister,
		Opcode.AddConstToAddr,
		Opcode.AddAddrToAddr,
		Opcode.AddIndirectToAddr,
		Opcode.AddConstToRegister,
		Opcode.AddRegisterToRegister,
		Opcode.AddRegisterToAddr,
		Opcode.IncrementRegister,
		Opcode.SubConstFromAddr,
		Opcode.SubAddrFromAddr,
		Opcode.SubIndirectFromAddr,
		Opcode.SubRegisterFromAddr,
		Opcode.SubRegisterFromRegister,
		Opcode.CmpConstFromAddr,
		Opcode.CmpAddrFromAddr,
		Opcode.CmpIndirectFromAddr,
		Opcode.CmpConstFromRegister,
		Opcode.CmpRegisterFromRegister,
		Opcode.JumpToConst,
		Opcode.JumpToAddr,
		Opcode.JumpToIndirect,
		Opcode.JumpToRegister,
		Opcode.JumpIfGreater,
		Opcode.JumpIfGreaterOrEqual,
		Opcode.JumpIfLess,
		Opcode.JumpIfLessOrEqual,
		Opcode.JumpIfZero,
		Opcode.JumpIfNotZero,
		Opcode.MulConstWithAddr,
		Opcode.MulConstWithRegister,
		Opcode.StopStepping,
		Opcode.ResumeSteppingAfterFrame,
		Opcode.PushConst,
		Opcode.PushRegister,
		Opcode.PopRegister,
		Opcode.PushAddr,
		Opcode.PlaySound,
		Opcode.DrawAtConst,
		Opcode.DrawAtRegister,
		Opcode.ClearScreen,
		Opcode.AuxillaryOne,
		Opcode.AuxillaryTwo,
	};
	private IMemoryGrid memory;
	private IMemoryGrid registers;
	/// <summary>The register rows (0 to 8, then the Shift, Ctrl, Up and Down bits) and the memory rows, for reading a run in a
	/// TriggerGraph (<see cref="MemoryRowExtensions.ValueIn"/>).</summary>
	public IReadOnlyList<IMemoryRow> Registers => registers.Rows;
	public IReadOnlyList<IMemoryRow> MemoryRows => memory.Rows;
	public Input Start;
	public Input Stop;
	public Output AuxillaryOne;
	public Output AuxillaryTwo;
	public IControls Controls;

	public void SetInitialMemory(IEnumerable<uint> initialMemory)
	{
		foreach ((int index, uint value) in Enumerable.Range(0, initialMemory.Count()).Zip(initialMemory))
        {
            memory.SetValue(index, value);
        }
	}

	public void SetInitialRegisters(IEnumerable<uint> initialRegisters)
	{
		foreach ((int index, uint value) in Enumerable.Range(0, initialRegisters.Count()).Zip(initialRegisters))
		{
			registers.SetValue(index, value);
		}
	}
	public void SetMemoryRow(int index, uint value) => memory.SetValue(index, value);
	public void SetRegister(int index, uint value) => registers.SetValue(index, value);

	public Processor(int bits, double x, double y, double width, double height,
					uint displayWidth, uint displayHeight, bool visualized=true, bool visualizedMemory=false,
					bool allMemoryIsROM=false, int? numMemoryRows = null,
					Func<int, int?>? opcodeMapping = null,
					double stepDelay = 0.10,
					IControls? controls = null)
    {
		opcodeMapping = opcodeMapping ?? new Func<int, int?>(i => i);
		numMemoryRows = numMemoryRows ?? (int)Math.Pow(2, bits);
        if (bits < 6)
        {
            throw new ArgumentOutOfRangeException("Processor has over 32 instructions, so needs to be at least 6 bit.");
        }
		// Currently the usage has displayWidth and height in a local var passed to the constructor,
		// but source code separately uses the variables from the public field of this class
		// I think it should stay this way just because source code and level gen code might not be in the same place
		// Memory is clickable only when it is drawn: a click trigger per bit is a trigger the cruncher can never remove (the game fires it, not
		// another trigger), so invisible clickable RAM cost 3 triggers a bit instead of 2.
		if (allMemoryIsROM)
		{
			memory = new ReadOnlyMemory((int)numMemoryRows, bits, x+(width*(bits+2)), y+height*2, width, height, visualized:visualizedMemory, visualizedAddress:visualizedMemory, clickable:visualizedMemory);
		} else
		{
			memory = new ReadWriteMemory((int)numMemoryRows, bits, x+(width*(bits+2)), y+height*2, width, height, visualized:visualizedMemory, visualizedAddress:visualizedMemory, clickable:visualizedMemory);
		}
		
		registers = new ReadWriteMemoryWithControlBits(13, bits, x-width*5, y+height*3, width, height, visualized:visualized, clickable:visualized);
		ScreenAsMemory screen = new(displayHeight, displayWidth, bits, x+(width*(bits+2)), y-displayHeight*height, width, height);
		// controls: ones the level builds and adds itself (SeatedControls, say), so their entities aren't listed here too (a duplicated entity
		// is written to the level twice); without them the processor builds plain NassingerControls of its own
		bool ownControls = controls is null;
		Controls = controls ?? new NassingerControls(x+(width*Math.Pow(2,bits-1)),y-height*50);
        Rectangle lagCube = new()
        {
            X = x + (width * (bits)),
			Y = y-displayHeight*height,
			Width =  width,
			Height = height,
			Fixed = false,
			FillColor = 0x111111,
			OutlineColor = 0xeeeeee,
        };
        PinJoint spinTheLagCube = new(lagCube)
        {
			X = lagCube.X,
			Y = lagCube.Y,
            Motorized = true,
			Limit = false,
			Speed = 10,
			Torque = 30,
        };

        Controls.ShiftOn.Connect(registers.DirectWrites[0x9][0]);
		Controls.ShiftOff.Connect(registers.Clears[0x9][0]);
		Controls.CtrlOn.Connect(registers.DirectWrites[0xa][0]);
		Controls.CtrlOff.Connect(registers.Clears[0xa][0]);
		Controls.UpOn.Connect(registers.DirectWrites[0xb][0]);
		Controls.UpOff.Connect(registers.Clears[0xb][0]);
		Controls.DownOn.Connect(registers.DirectWrites[0xc][0]);
		Controls.DownOff.Connect(registers.Clears[0xc][0]);
		// Using this instead of memory.Read several times to reduce total trigger arms, to make the level smaller
		Relay readMemoryRelay = new(x+width*(bits+2), y+height*2, width, height);
		readMemoryRelay.Output.Connect(memory.Read);
		MultiRelay memoryValues = new(bits,x+(width*(bits+2)), y+height*2, width, height);
		memory.Values.Connect(memoryValues.Inputs);
		Relay readRegistersRelay = new(x, y+height*1, width, height);
		readRegistersRelay.Output.Connect(registers.Read);
		MultiRelay registerValues = new(bits, x, y+height*1, width, height);
		registers.Values.Connect(registerValues.Inputs);
		Addressor instruction = new(bits, x, y, width, height);
		if (visualized)
		{
			instruction.AddVisualization();
		}
		MultiRelay conditionalWriteToInstruction = new(bits, x, y, width, height, startDisabled:true);
		Relay disableConditionalWriteToInstruction = new(x-width, y, width, height);
		MultiRelay conditionalWriteToSourceOperand = new(bits, x, y+height, width, height, startDisabled:true);
		Relay disableConditionalWriteToSourceOperand = new(x-width, y+height, width, height);
		Relay clearSourceOperand = new(x-width, y+height, width, height);
		Relay probeTargetOperandValue = new(x-width, y+height, width, height);
		Relay probeTargetOperand = new(x-width, y+height, width, height);
		MultiRelay conditionalWriteToTargetOperand = new(bits, x, y+height*2, width, height, startDisabled:true);
		Relay disableConditionalWriteToTargetOperand = new(x-width, y+height*2, width, height);
		MultiRelay conditionalWriteToTargetOperandValue = new(bits, x, y+height*3, width, height, startDisabled:true);
		Relay disableConditionalWriteToTargetOperandValue = new(x-width, y+height*3, width, height);
		MultiRelay conditionalWriteTargetToTargetValue = new(bits, x, y+height*3, width, height, startDisabled:true);
		Relay disableConditionalWriteTargetToTargetValue = new(x-width, y+height*3, width, height);
		MultiRelay conditionalSubtractFromTargetOperandValue = new(bits, x, y+height*3, width, height, startDisabled:true);
		Relay enableConditionalSubtractFromTargetOperandValue = new(x-width, y+height*3, width, height);

		Addressor sourceOperand = new(bits, x, y-height*4, width, height);
		if (visualized)
		{
			sourceOperand.AddVisualization();
		}
		Label sourceLabel = new("SRC", x-width, y-height*4, width, height);
		Addressor targetOperand = new(bits, x, y-height*3, width, height);
		if (visualized)
		{
			targetOperand.AddVisualization();
		}
		Label targetLabel = new("TRG", x-width, y-height*3, width, height);
		Relay conditionalProbeTargetOperand = new(x-width, y-height*3, width, height, startDisabled:true);
		Relay disableProbeTargetOperand = new(x-width, y-height*3, width, height);
		InstantToggleChain targetOperandValue = new(bits, x, y-height*2, width, height);
		if (visualized)
		{
			targetOperandValue.AddVisualization();
		}
		Label targetValueLabel = new("TRGVAL", x-width, y-height*2, width, height);
		MultiRelay shiftTargetValue = new(bits, x,y-height, width, height, startDisabled: true);
		Relay enableShiftTargetValue = new(x,y-height, width, height);
		Relay disableShiftTargetValue = new(x,y-height, width, height);
		InstantToggleChain programCounter = new(bits, x, y-height*1, width, height);
		if (visualized)
		{
			programCounter.AddVisualization();
		}
		Relay clearProgramCounter = new(x-width, y-height*1, width, height);
		MultiRelay conditionalWriteToProgramCounter = new(bits, x, y-height*1, width, height, startDisabled:true);
		Relay disableConditionalWriteToProgramCounter = new(x-width, y-height*1, width, height);
		Relay decrementProgramCounter = new(x-width, y-height*1, width, height);
		Relay decrementSourceOperand = new(x-width*2, y-height*1, width, height);
		Label labelPC = new("PC", x-width, y-height*1, width, height);
		Relay carryFlag = new(x-width*2, y-height*4, width, height, startDisabled:true);
		Relay conditionalClearCarryFlag = new(x-width*2, y-height*4, width, height, startDisabled:true);
		Relay conditionalSetCarryFlag = new(x-width*2, y-height*4, width, height, startDisabled:true);
		Relay disableConditionalClearCarryFlag = new(x-width*2, y-height*4, width, height, startDisabled:false);
		Relay disableConditionalSetCarryFlag = new(x-width*2, y-height*4, width, height, startDisabled:false);
		if (visualized)
		{
			carryFlag.AddVisualization();
		}
		Label carryLabel = new("CF", x-width*2, y-height*4, width, height, backgroundColor:-1);
		Relay zeroFlag = new(x-width*3, y-height*4, width, height, startDisabled:true);
		Relay conditionalWriteToZeroFlag = new(x-width*3, y-height*4, width, height, startDisabled:true);
		Relay disableConditionalWriteToZeroFlag = new(x-width*3, y-height*4, width, height, startDisabled:false);
		if (visualized)
		{
			zeroFlag.AddVisualization();
		}
		Label zeroFlagLabel = new("ZF", x-width*3, y-height*4, width, height, backgroundColor:-1);
		Label labelInstruction = new("INS", x-width, y, width, height);

		Relay writeBitToScreenRelay = new(x, y+height*5, width, height);
		
		MultiRelay conditionalAddToAddress = new(bits, x, y-height*1, width, height, startDisabled:true);
		Relay disableConditionalAddToAddress = new(x, y-height, width, height);
		MultiRelay conditionalAddToRegisterAddress = new(bits, x, y-height*2, width, height, startDisabled:true);
		Relay disableConditionalAddToRegisterAddress = new(x, y-height*2, width, height);
		MultiRelay conditionalAddToScreenAddress = new(bits, x, y-height*3, width, height, startDisabled:true);
		Relay disableConditionalAddToScreenAddress = new(x, y-height*3, width, height);

		Relay sendSourceToMemory = new(x, y+height, width, height);
		Relay sendSourceToRegisterMemory = new(x, y+height, width, height);
		Relay writeToRegisters = new(x, y+height*2, width, height);
		Relay readRegisters = new(x+width, y+height*2, width, height);

		Relay moveConstToAddr = new(x, y+height*2, width, height, startDisabled:true);
		Relay moveConstToAddrRelay = new(x, y+height*2, width, height, startDisabled:false);
		Relay moveAddrToAddr = new(x, y+height*3, width, height, startDisabled:true);
		Relay moveIndirectToAddr = new(x, y+height*4, width, height, startDisabled:true);
		Relay moveConstToAddrIndirect = new(x, y+height*5, width, height, startDisabled:true);
		Relay moveAddrToAddrIndirect = new(x, y+height*6, width, height, startDisabled:true);
		Relay moveConstToRegister = new(x, y+height*6.5, width, height, startDisabled:true);
		Relay moveConstToRegisterRelay = new(x, y+height*6.5, width, height, startDisabled:false);
		Relay moveRegisterToRegister = new(x, y+height*6.7, width, height, startDisabled:true);
		Relay moveConstToRegisterIndirect = new(x, y+height*6.75, width, height, startDisabled:true);
		Relay moveRegisterToRegisterIndirect = new(x, y+height*6.875, width, height, startDisabled:true);
		Relay addConstToAddr = new(x, y+height*7, width, height, startDisabled:true);
		Relay addConstToAddrRelay = new(x, y+height*7, width, height, startDisabled:false);
		Relay addAddrToAddr = new(x, y+height*8, width, height, startDisabled:true);
		Relay addIndirectToAddr = new(x, y+height*9, width, height, startDisabled:true);
		Relay subConstFromAddr = new(x, y+height*10, width, height, startDisabled:true);
		Relay subAddrFromAddr = new(x, y+height*11, width, height, startDisabled:true);
		Relay subIndirectFromAddr = new(x, y+height*12, width, height, startDisabled:true);
		Relay cmpConstFromAddr = new(x, y+height*13, width, height, startDisabled:true);
		Relay cmpConstFromAddrRelay = new(x, y+height*13, width, height, startDisabled:false);
		Relay cmpAddrFromAddr = new(x, y+height*14, width, height, startDisabled:true);
		Relay cmpIndirectFromAddr = new(x, y+height*15, width, height, startDisabled:true);
		Relay jumpToConst = new(x, y+height*16, width, height, startDisabled:true);
		Relay jumpToConstRelay = new(x, y+height*16, width, height, startDisabled:false);
		Relay conditionalJumpToConst = new(x, y+height*16, width, height, startDisabled:true);
		Relay conditionalNotJumpToConst = new(x, y+height*16, width, height, startDisabled:true);
		Relay enableConditionalNotJumpToConst = new(x, y+height*16, width, height, startDisabled:false);
		Relay disableConditionalNotJumpToConst = new(x, y+height*16, width, height, startDisabled:false);
		Relay disableConditionalJumpToConst = new(x, y+height*16, width, height, startDisabled:false);
		Relay jumpToAddr = new(x, y+height*17, width, height, startDisabled:true);
		Relay jumpToIndirect = new(x, y+height*18, width, height, startDisabled:true);
		Relay jumpIfGreater = new(x, y+height*19, width, height, startDisabled:true);
		Relay jumpIfGreaterOrEqual = new(x, y+height*20, width, height, startDisabled:true);
		Relay jumpIfLess = new(x, y+height*21, width, height, startDisabled:true);
		Relay jumpIfLessOrEqual = new(x, y+height*22, width, height, startDisabled:true);
		Relay jumpIfZero = new(x, y+height*23, width, height, startDisabled:true);
		Relay jumpIfNotZero = new(x, y+height*24, width, height, startDisabled:true);
		Relay mulConstWithAddr = new(x, y+height*24, width, height, startDisabled:true);
		Relay mulConstWithAddrRelay = new(x, y+height*24, width, height, startDisabled:false);
		Relay stopStepping = new(x, y+height*25, width, height, startDisabled:true);
		Relay incrementRegister = new(x, y+height*26, width, height, startDisabled:true);
		Relay incrementRegisterRelay = new(x, y+height*26, width, height, startDisabled:false);
		Relay cmpConstFromRegister = new(x, y+height*27, width, height, startDisabled:true);
		Relay cmpRegisterFromRegister = new(x, y+height*28, width, height, startDisabled:true);
		Relay addConstToRegister = new(x, y+height*29, width, height, startDisabled:true);
		Relay addRegisterToRegister = new(x, y+height*30, width, height, startDisabled:true);
		Relay pushConst = new(x, y+height*31, width, height, startDisabled:true);
		Relay pushConstRelay = new(x, y+height*31, width, height, startDisabled:false);
		Relay pushRegister = new(x, y+height*32, width, height, startDisabled:true);
		Relay popRegister = new(x, y+height*33, width, height, startDisabled:true);
		Relay moveAddrToRegister = new(x, y+height*34, width, height, startDisabled:true);
		Relay mulConstWithRegister = new(x, y+height*34, width, height, startDisabled:true);
		Relay pushAddr = new(x, y+height*35, width, height, startDisabled:true);
		Relay jumpToRegister = new(x, y+height*36, width, height, startDisabled:true);
		Relay clearScreen = new(x, y+height*37, width, height, startDisabled:true);
		Relay resumeSteppingAfterFrame = new(x, y+height*38, width, height, startDisabled:true);
		// Relay resumeStepping = new(x, y+height*38, width, height, startDisabled:false, delay:0.1);
		Relay resumeStepping = new(x, y+height*38, width, height, startDisabled:false, delay:stepDelay);
        SoundTrigger playSoundTrigger = new()
        {
			Sound = 0,
            X = x,
			Y = y+height*39,
			Width = width,
			Height = height,
			StartDisabled = false,
			RepeatType = RepeatType.Multiple,
			TriggeredBy = TriggeredBy.Triggers,
        };
		// SoundTrigger playSoundTriggerTwo = new()
		// {
		// 	Sound = 1,
		// 	X = x,
		// 	Y = y+height*39,
		// 	Width = width,
		// 	Height = height,
		// 	StartDisabled = false,
		// 	RepeatType = RepeatType.Multiple,
		// 	TriggeredBy = TriggeredBy.Triggers,
		// };
		Relay playSound = new(x, y+height*39, width, height, startDisabled:true);
		// Relay playSoundTwo = new(x, y+height*39, width, height, startDisabled:true);
		Relay subRegisterFromAddr = new(x, y+height*40, width, height, startDisabled:true);
		Relay drawAtConst = new(x, y+height*41, width, height, startDisabled:true);
		Relay drawAtConstRelay = new(x, y+height*41, width, height, startDisabled:false);
		Relay drawAtRegister = new(x, y+height*42, width, height, startDisabled:true);
		Relay auxillaryOne = new(x, y+height*43, width, height, startDisabled:true);
		Relay auxillaryTwo = new(x, y+height*44, width, height, startDisabled:true);
		Relay subRegisterFromRegister = new(x, y+height*45, width, height, startDisabled:true);
		Relay addRegisterToAddr = new(x, y+height*46, width, height, startDisabled:true);
        // I don't think I'll need mulAddrWithAddr

        // Buttons that set the program counter and instruction bits, the lowest bit on the right
        List<Clickable> ButtonRow(double rowY) => Enumerable.Range(0, bits).Reverse().Select(index => new Clickable(x + width*index, rowY, width, height)).ToList();
        List<Clickable> programCounterButtons = ButtonRow(y-height*1);
		Clickable clearProgramCounterButton = new(x-width, y-height*1, width, height);
		List<Clickable> instructionButtons = ButtonRow(y);
		Clickable clearInstructionButton = new(x-width, y, width, height);

		// A no-operand operation would need to use this once
		// An operation with 2 operands would use it once,
		// and then have the operation's trigger use these twice to fetch operands
		MultiRelay incrementPC = new(2, x-width*8, y-height, width, height);
		foreach(Output output in incrementPC.Outputs)
		{
			// Toggling LSB in a toggle chain is the same as incrementing
			output.Connect(programCounter.Inputs[0]);
		}
		Clickable fetchInstructionButton = new(x-width*4, y, width, height);
		Relay fetchInstructionRelay = new(x-width*4, y, width, height);
		fetchInstructionButton.Output.Connect(fetchInstructionRelay.Input);
		Label fetchInstructionLabel = new("FETCH", x-width*4, y, width, height, textSize: 0.3);

		Relay conditionalGetTargetOperandValueAsMemory = new(x-width*7, y, width, height, startDisabled:true);
		Relay disableConditionalGetTargetOperandValueAsMemory = new(x-width*7, y, width, height, startDisabled:false);
		Relay conditionalGetTargetOperandValueAsRegister = new(x-width*7, y, width, height, startDisabled:true);
		Relay disableConditionalGetTargetOperandValueAsRegister = new(x-width*7, y, width, height, startDisabled:false);
		Relay conditionalDerefTargetOperand = new(x-width*7, y, width, height, startDisabled:true);
		Relay disableConditionalDerefTargetOperand = new(x-width*7, y, width, height, startDisabled:false);
		Relay conditionalDerefTargetOperandRegister = new(x-width*8, y, width, height, startDisabled:true);
		Relay disableConditionalDerefTargetOperandRegister = new(x-width*8, y, width, height, startDisabled:false);

		MultiRelay conditionalDerefSourceOperand = new(2, x-width*7, y, width, height, startDisabled:true);
		conditionalDerefSourceOperand.Outputs[1].Connect(conditionalDerefSourceOperand.Inputs[0]);
		Relay conditionalDerefSourceOperandAsRegister = new(x-width*5, y, width, height, startDisabled:true);
		Relay disableConditionalDerefSourceOperand = new(x-width*7, y, width, height, startDisabled:false);
		disableConditionalDerefSourceOperand.Output.Connect(conditionalDerefSourceOperand.Disables);
		Relay enableConditionalDerefSourceOperand = new(x-width*7, y, width, height, startDisabled:false);
		Relay disableConditionalDerefSourceOperandAsRegister = new(x-width*5, y, width, height, startDisabled:false);
		disableConditionalDerefSourceOperandAsRegister.Output.Connect(conditionalDerefSourceOperandAsRegister.Disable);
		Relay enableConditionalDerefSourceOperandAsRegister = new(x-width*5, y, width, height, startDisabled:false);
		enableConditionalDerefSourceOperandAsRegister.Output.Connect(conditionalDerefSourceOperandAsRegister.Enable);

		Relay sendPCToAddress = new(x-width*4, y, width, height);
		Relay sendTargetToAddress = new(x-width*3, y, width, height);
		Relay sendTargetToRegisterAddress = new(x-width*4, y, width, height);
		Relay sendTargetToScreenAddress = new(x-width*5, y, width, height);
		Relay sendSourceToAddress = new(x-width*5, y, width, height);
		Relay sendSourceToRegisterAddress = new(x-width*6, y, width, height);

		Relay fetchSourceOperand = new(x-width*7, y, width, height);
		Relay fetchTargetOperand = new(x-width*6, y, width, height);
		Relay fetchTargetOperandRelay = new(x-width*6, y, width, height);

		programCounterButtons.Select(button => button.Output).ToList().Connect(programCounter.Sets);
		instructionButtons.Select(button => button.Output).ToList().Connect(instruction.Sets);
		clearProgramCounterButton.Output.Connect(clearProgramCounter.Input);
		clearInstructionButton.Output.Connect(instruction.Clears);

		Relay setInstruction = new(x-width*2, y, width, height);
		Clickable executeInstructionButton = new(x-width*3, y, width, height);
		Relay executeInstructionRelay = new(x-width*3, y, width, height);
		executeInstructionButton.Output.Connect(executeInstructionRelay.Input);
		Label executeInstructionLabel = new("EXEC", x-width*3, y, width, height, textSize: 0.3);
		executeInstructionRelay.Output.Connect(setInstruction.Input);

		// Clickable derefSourceOperandButton = new(x-width*2, y+height, width, height);
		// Relay enableDerefSourceOperand = new(x-width*2, y+height, width, height);
		// Label derefSourceOperandLabel = new("DEREF", x-width*2, y+height, width, height, textSize:0.2);
		// derefSourceOperandButton.Output.Connect(enableDerefSourceOperand.Input);
		// enableDerefSourceOperand.Output.Connect(conditionalDerefSourceOperand.Enables[0]);
		// derefSourceOperandButton.Output.Connect(conditionalDerefSourceOperand.Inputs[0]);
		// derefSourceOperandButton.Output.Connect(disableConditionalDerefSourceOperand.Input);

		// Clickable fetchSourceOperandButton = new(x-width*1, y+height, width, height);
		// Label fetchSourceOperandLabel = new("FETCHSOURCE", x-width*1, y+height, width, height, textSize:0.15);
		// fetchSourceOperandButton.Output.Connect(fetchSourceOperand.Input);

		// Clickable fetchTargetOperandButton = new(x-width*0, y+height, width, height);
		// Label fetchTargetOperandLabel = new("FETCHTARGET", x-width*0, y+height, width, height, textSize:0.15);
		// fetchTargetOperandButton.Output.Connect(fetchTargetOperand.Input);

		// Clickable targetInput = new(x+width*1, y+height, width, height);
		// Label targetInputLabel = new("TRGINPUT", x+width*1, y+height, width, height, textSize:0.15);
		// targetInput.Output.Connect(targetOperand.Inputs); 

		Relay nextInstruction = new(x-width*2, y, width, height);
		Clickable nextInstructionButton = new(x-width*2, y, width, height);
		Label nextInstructionLabel = new("NXT", x-width*2, y, width, height, textSize: 0.3);
		nextInstructionButton.Output.Connect(nextInstruction.Input);
		nextInstruction.Output.Connect(programCounter.Inputs[0]);

		Relay step = new(x-width*4, y-height, width, height);
		Relay enableStep = new(x-width*4, y-height, width, height);
		enableStep.Output.Connect(step.Enable);
		Clickable stepButton = new(x-width*4, y-height, width, height);
		Label stepLabel = new("STEP", x-width*4, y-height, width, height, textSize: 0.3);
		stepButton.Output.Connect(step.Input);
		// Clickable stepButtonTwo = new(x+width*20, y+height*2, width, height);
		// Label stepLabelTwo = new("STEP", x+width*20, y+height*2, width, height, textSize: 0.3);
		// stepButtonTwo.Output.Connect(step.Input);
		step.Output.Connect(fetchInstructionRelay.Input);
		step.Output.Connect(executeInstructionRelay.Input);
		step.Output.Connect(nextInstruction.Input);

		Clickable showPCButton = new(x-width*3, y-height, width, height);
		Label showPCLabel = new("SHOWPC", x-width*3.2, y-height, width, height, textSize:0.2);
		showPCButton.Output.Connect(sendPCToAddress.Input);
		showPCButton.Output.Connect(memory.Read);

		InstantToggle continuous = new(x-width*4, y-height*2, width, height);
		Clickable continuousButton = new(x-width*4, y-height*2, width, height);
		Panel continuousPanel = new(x-width*4, y-height*2, width, height, label: "CNT");
		continuousButton.Output.Connect(continuous.Input);
		
		Relay repeatStep = new(x-width*4, y-height, width, height, delay:0, startDisabled: true);
		continuous.On.Connect(repeatStep.Enable);
		continuous.Off.Connect(repeatStep.Disable);
		step.Output.Connect(repeatStep.Input);
		repeatStep.Output.Connect(step.Input);

		Start = continuous.Set
			.Concat(repeatStep.Enable)
			.Concat(enableStep.Input)
			.Concat(step.Input);
		Stop = step.Disable;

		List<Relay> operationRelays = new()
		{
			moveConstToAddr,
			moveAddrToAddr,
			moveIndirectToAddr,
			moveConstToAddrIndirect,
			moveConstToRegister,
			moveRegisterToRegister,
			moveConstToRegisterIndirect,
			moveRegisterToRegisterIndirect,
			moveAddrToRegister,
			addConstToAddr,
			addAddrToAddr,
			addIndirectToAddr,
			addConstToRegister,
			addRegisterToRegister,
			addRegisterToAddr,
			incrementRegister,
			subConstFromAddr,
			subAddrFromAddr,
			subIndirectFromAddr,
			subRegisterFromAddr,
			subRegisterFromRegister,
			cmpConstFromAddr,
			cmpAddrFromAddr,
			cmpIndirectFromAddr,
			cmpConstFromRegister,
			cmpRegisterFromRegister,
			jumpToConst,
			jumpToAddr,
			jumpToIndirect,
			jumpToRegister,
			jumpIfGreater,
			jumpIfGreaterOrEqual,
			jumpIfLess,
			jumpIfLessOrEqual,
			jumpIfZero,
			jumpIfNotZero,
			mulConstWithAddr,
			mulConstWithRegister,
			stopStepping,
			resumeSteppingAfterFrame,
			pushConst,
			pushRegister,
			popRegister,
			pushAddr,
			playSound,
			drawAtConst,
			drawAtRegister,
			clearScreen,
			auxillaryOne,
			auxillaryTwo,
		};

		foreach ((Opcode opcode, Relay operation) in Opcodes.Zip(operationRelays))
		{
			int? mappedOpcode = opcodeMapping((int)(uint)opcode);
			if (mappedOpcode is int validMappedOpcode)
			{
				setInstruction.Output.Connect(operation.Enable);
				instruction.UnaryOff[validMappedOpcode].Connect(operation.Disable);
				executeInstructionRelay.Output.Connect(operation.Input);
			}
		}
		setInstruction.Output.Connect(instruction.ProbeUnary);

		// 0 is going to be a NOP
		moveConstToAddr.Output.Connect(moveConstToAddrRelay.Input);
		moveConstToAddrRelay.Output.Connect(incrementPC.Inputs[0]);
		// This output is connected so we can specify addressing modes
		// just by enabling or disabling these triggers.
		moveConstToAddrRelay.Output.Connect(fetchSourceOperand.Input);
		moveConstToAddrRelay.Output.Connect(conditionalDerefSourceOperandAsRegister.Input);
		moveConstToAddrRelay.Output.Connect(conditionalDerefSourceOperand.Inputs);
		moveConstToAddrRelay.Output.Connect(incrementPC.Inputs[1]);
		moveConstToAddrRelay.Output.Connect(fetchTargetOperand.Input);
		// Only mov will have this, needed to cleanly implement push
		moveConstToAddrRelay.Output.Connect(conditionalDerefTargetOperandRegister.Input);
		moveConstToAddrRelay.Output.Connect(conditionalDerefTargetOperand.Input);
		moveConstToAddrRelay.Output.Connect(sendTargetToAddress.Input);
		moveConstToAddrRelay.Output.Connect(sendSourceToMemory.Input);

		moveAddrToAddr.Output.Connect(conditionalDerefSourceOperand.Enables[0]);
		moveAddrToAddr.Output.Connect(moveConstToAddrRelay.Input);
		moveAddrToAddr.Output.Connect(disableConditionalDerefSourceOperand.Input);

		moveIndirectToAddr.Output.Connect(conditionalDerefSourceOperand.Enables);
		moveIndirectToAddr.Output.Connect(moveConstToAddrRelay.Input);
		moveIndirectToAddr.Output.Connect(disableConditionalDerefSourceOperand.Input);

		moveConstToAddrIndirect.Output.Connect(conditionalDerefTargetOperand.Enable);
		moveConstToAddrIndirect.Output.Connect(moveConstToAddrRelay.Input);
		moveConstToAddrIndirect.Output.Connect(disableConditionalDerefTargetOperand.Input);
		disableConditionalDerefTargetOperand.Output.Connect(conditionalDerefTargetOperand.Disable);

		moveAddrToAddrIndirect.Output.Connect(conditionalDerefSourceOperand.Enables[0]);
		moveAddrToAddrIndirect.Output.Connect(conditionalDerefTargetOperand.Enable);
		moveAddrToAddrIndirect.Output.Connect(moveConstToAddrRelay.Input);
		moveAddrToAddrIndirect.Output.Connect(disableConditionalDerefSourceOperand.Input);
		moveAddrToAddrIndirect.Output.Connect(disableConditionalDerefTargetOperand.Input);

		moveConstToRegister.Output.Connect(moveConstToRegisterRelay.Input);
		moveConstToRegisterRelay.Output.Connect(incrementPC.Inputs[0]);
		moveConstToRegisterRelay.Output.Connect(fetchSourceOperand.Input);
		moveConstToRegisterRelay.Output.Connect(conditionalDerefSourceOperandAsRegister.Input);
		moveConstToRegisterRelay.Output.Connect(conditionalDerefSourceOperand.Inputs);
		moveConstToRegisterRelay.Output.Connect(incrementPC.Inputs[1]);
		moveConstToRegisterRelay.Output.Connect(fetchTargetOperand.Input);
		moveConstToRegisterRelay.Output.Connect(conditionalDerefTargetOperandRegister.Input);
		moveConstToRegister.Output.Connect(sendTargetToRegisterAddress.Input);
		moveConstToRegister.Output.Connect(sendSourceToRegisterMemory.Input);

		moveRegisterToRegister.Output.Connect(conditionalDerefSourceOperandAsRegister.Enable);
		moveRegisterToRegister.Output.Connect(moveConstToRegisterRelay.Input);
		moveRegisterToRegister.Output.Connect(disableConditionalDerefSourceOperandAsRegister.Input);
		moveRegisterToRegister.Output.Connect(sendTargetToRegisterAddress.Input);
		moveRegisterToRegister.Output.Connect(sendSourceToRegisterMemory.Input);

		moveConstToRegisterIndirect.Output.Connect(conditionalDerefTargetOperandRegister.Enable);
		moveConstToRegisterIndirect.Output.Connect(moveConstToAddrRelay.Input);
		moveConstToRegisterIndirect.Output.Connect(disableConditionalDerefTargetOperandRegister.Input);
		disableConditionalDerefTargetOperandRegister.Output.Connect(conditionalDerefTargetOperandRegister.Disable);
		moveConstToRegisterIndirect.Output.Connect(sendTargetToRegisterAddress.Input);
		moveConstToRegisterIndirect.Output.Connect(sendSourceToMemory.Input);

		moveRegisterToRegisterIndirect.Output.Connect(conditionalDerefSourceOperandAsRegister.Enable);
		moveRegisterToRegisterIndirect.Output.Connect(conditionalDerefTargetOperandRegister.Enable);
		//moveRegisterToRegisterIndirect.Output.Connect(conditionalDerefTargetOperand.Enable);
		moveRegisterToRegisterIndirect.Output.Connect(moveConstToAddrRelay.Input);
		moveRegisterToRegisterIndirect.Output.Connect(disableConditionalDerefSourceOperandAsRegister.Input);
		moveRegisterToRegisterIndirect.Output.Connect(disableConditionalDerefTargetOperandRegister.Input);
		moveRegisterToRegisterIndirect.Output.Connect(disableConditionalDerefTargetOperand.Input);

		moveAddrToRegister.Output.Connect(conditionalDerefSourceOperand.Enables[0]);
		moveAddrToRegister.Output.Connect(moveConstToRegisterRelay.Input);
		moveAddrToRegister.Output.Connect(disableConditionalDerefSourceOperand.Input);
		moveAddrToRegister.Output.Connect(sendTargetToRegisterAddress.Input);
		moveAddrToRegister.Output.Connect(sendSourceToRegisterMemory.Input);

		// This doesn't set carry or zero flags yet
		addConstToAddr.Output.Connect(conditionalGetTargetOperandValueAsMemory.Enable);
		addConstToAddr.Output.Connect(addConstToAddrRelay.Input);
		addConstToAddr.Output.Connect(disableConditionalGetTargetOperandValueAsMemory.Input);
		addConstToAddrRelay.Output.Connect(incrementPC.Inputs[0]);
		addConstToAddrRelay.Output.Connect(fetchSourceOperand.Input);
		addConstToAddrRelay.Output.Connect(incrementPC.Inputs[1]);
		addConstToAddrRelay.Output.Connect(fetchTargetOperand.Input);
		addConstToAddrRelay.Output.Connect(conditionalDerefSourceOperandAsRegister.Input);
		addConstToAddrRelay.Output.Connect(conditionalDerefSourceOperand.Inputs);
		addConstToAddrRelay.Output.Connect(conditionalGetTargetOperandValueAsRegister.Input);
		addConstToAddrRelay.Output.Connect(conditionalGetTargetOperandValueAsMemory.Input);
		addConstToAddrRelay.Output.Connect(carryFlag.Disable);
		addConstToAddrRelay.Output.Connect(conditionalSetCarryFlag.Enable);
		// This is depending on the fact that the source operand is a toggle chain
		addConstToAddrRelay.Output.Connect(conditionalWriteToSourceOperand.Enables);
		addConstToAddrRelay.Output.Connect(targetOperandValue.Probes);
		targetOperandValue.Ons.Connect(conditionalWriteToSourceOperand.Inputs);
		sourceOperand.Offs[bits-1].Connect(conditionalSetCarryFlag.Input);
		conditionalSetCarryFlag.Output.Connect(carryFlag.Enable);
		addConstToAddrRelay.Output.Connect(disableConditionalWriteToSourceOperand.Input);
		addConstToAddrRelay.Output.Connect(disableConditionalSetCarryFlag.Input);
		disableConditionalSetCarryFlag.Output.Connect(conditionalSetCarryFlag.Disable);
		addConstToAddrRelay.Output.Connect(zeroFlag.Disable);
		addConstToAddrRelay.Output.Connect(conditionalWriteToZeroFlag.Enable);
		addConstToAddrRelay.Output.Connect(sourceOperand.Probes);
		sourceOperand.Ons.Connect(conditionalWriteToZeroFlag.Input);
		addConstToAddrRelay.Output.Connect(disableConditionalWriteToZeroFlag.Input);

		addConstToAddr.Output.Connect(sendTargetToAddress.Input);
		addConstToAddr.Output.Connect(sendSourceToMemory.Input);

		addAddrToAddr.Output.Connect(conditionalDerefSourceOperand.Enables[0]);
		addAddrToAddr.Output.Connect(conditionalGetTargetOperandValueAsMemory.Enable);
		addAddrToAddr.Output.Connect(addConstToAddrRelay.Input);
		addAddrToAddr.Output.Connect(disableConditionalDerefSourceOperand.Input);
		addAddrToAddr.Output.Connect(disableConditionalGetTargetOperandValueAsMemory.Input);
		addAddrToAddr.Output.Connect(sendTargetToAddress.Input);
		addAddrToAddr.Output.Connect(sendSourceToMemory.Input);

		addIndirectToAddr.Output.Connect(conditionalDerefSourceOperand.Enables);
		addIndirectToAddr.Output.Connect(conditionalGetTargetOperandValueAsMemory.Enable);
		addIndirectToAddr.Output.Connect(addConstToAddrRelay.Input);
		addIndirectToAddr.Output.Connect(disableConditionalDerefSourceOperand.Input);
		addIndirectToAddr.Output.Connect(disableConditionalGetTargetOperandValueAsMemory.Input);
		addIndirectToAddr.Output.Connect(sendTargetToAddress.Input);
		addIndirectToAddr.Output.Connect(sendSourceToMemory.Input);

		addConstToRegister.Output.Connect(conditionalGetTargetOperandValueAsRegister.Enable);
		addConstToRegister.Output.Connect(addConstToAddrRelay.Input);
		addConstToRegister.Output.Connect(disableConditionalGetTargetOperandValueAsRegister.Input);
		// Not strictly necessary as the last register address was target
		addConstToRegister.Output.Connect(sendTargetToRegisterAddress.Input);
		addConstToRegister.Output.Connect(sendSourceToRegisterMemory.Input);

		addRegisterToRegister.Output.Connect(conditionalGetTargetOperandValueAsRegister.Enable);
		addRegisterToRegister.Output.Connect(conditionalDerefSourceOperandAsRegister.Enable);
		addRegisterToRegister.Output.Connect(addConstToAddrRelay.Input);
		addRegisterToRegister.Output.Connect(disableConditionalGetTargetOperandValueAsRegister.Input);
		addRegisterToRegister.Output.Connect(disableConditionalDerefSourceOperandAsRegister.Input);
		addRegisterToRegister.Output.Connect(sendTargetToRegisterAddress.Input);
		addRegisterToRegister.Output.Connect(sendSourceToRegisterMemory.Input);

		addRegisterToAddr.Output.Connect(conditionalDerefSourceOperandAsRegister.Enable);
		addRegisterToAddr.Output.Connect(conditionalGetTargetOperandValueAsMemory.Enable);
		addRegisterToAddr.Output.Connect(addConstToAddrRelay.Input);
		addRegisterToAddr.Output.Connect(disableConditionalDerefSourceOperandAsRegister.Input);
		addRegisterToAddr.Output.Connect(disableConditionalGetTargetOperandValueAsMemory.Input);
		addRegisterToAddr.Output.Connect(sendTargetToAddress.Input);
		addRegisterToAddr.Output.Connect(sendSourceToMemory.Input);

		incrementRegister.Output.Connect(incrementPC.Inputs[0]);
		incrementRegister.Output.Connect(fetchSourceOperand.Input);
		incrementRegister.Output.Connect(conditionalDerefSourceOperandAsRegister.Enable);
		incrementRegister.Output.Connect(incrementRegisterRelay.Input);
		incrementRegisterRelay.Output.Connect(conditionalDerefSourceOperandAsRegister.Input);
		// Now, the source address is still going to be in the address for the register's memory
		// so, we won't send address again, and can in fact not have it here in the first place.
		incrementRegisterRelay.Output.Connect(sourceOperand.Inputs[0]);
		incrementRegisterRelay.Output.Connect(sendSourceToRegisterMemory.Input);
		incrementRegister.Output.Connect(disableConditionalDerefSourceOperandAsRegister.Input);

		subConstFromAddr.Output.Connect(conditionalGetTargetOperandValueAsMemory.Enable);
		subConstFromAddr.Output.Connect(cmpConstFromAddrRelay.Input);
		subConstFromAddr.Output.Connect(disableConditionalGetTargetOperandValueAsMemory.Input);
		disableConditionalGetTargetOperandValueAsMemory.Output.Connect(conditionalGetTargetOperandValueAsMemory.Disable);

		cmpConstFromAddrRelay.Output.Connect(incrementPC.Inputs[0]);
		cmpConstFromAddrRelay.Output.Connect(fetchSourceOperand.Input);
		cmpConstFromAddrRelay.Output.Connect(incrementPC.Inputs[1]);
		// Figure out why the order of these next 4 matter
		cmpConstFromAddrRelay.Output.Connect(conditionalDerefSourceOperandAsRegister.Input);
		cmpConstFromAddrRelay.Output.Connect(conditionalDerefSourceOperand.Inputs);
		cmpConstFromAddrRelay.Output.Connect(fetchTargetOperand.Input);
		cmpConstFromAddrRelay.Output.Connect(conditionalGetTargetOperandValueAsMemory.Input);
		cmpConstFromAddrRelay.Output.Connect(conditionalGetTargetOperandValueAsRegister.Input);
		cmpConstFromAddrRelay.Output.Connect(enableConditionalSubtractFromTargetOperandValue.Input);
		enableConditionalSubtractFromTargetOperandValue.Output.Connect(conditionalSubtractFromTargetOperandValue.Enables);
		cmpConstFromAddrRelay.Output.Connect(sourceOperand.Probes);
		sourceOperand.Ons.Connect(conditionalSubtractFromTargetOperandValue.Disables);
		cmpConstFromAddrRelay.Output.Connect(carryFlag.Enable);
		cmpConstFromAddrRelay.Output.Connect(conditionalClearCarryFlag.Enable);
		cmpConstFromAddrRelay.Output.Connect(conditionalSubtractFromTargetOperandValue.Inputs);
		conditionalSubtractFromTargetOperandValue.Outputs.Connect(targetOperandValue.Inputs);
		// Increment, as adding the complement gives you one below what subtraction is.
		cmpConstFromAddrRelay.Output.Connect(targetOperandValue.Inputs[0]);
		targetOperandValue.Offs[bits-1].Connect(conditionalClearCarryFlag.Input);
		conditionalClearCarryFlag.Output.Connect(carryFlag.Disable);
		cmpConstFromAddrRelay.Output.Connect(disableConditionalClearCarryFlag.Input);
		disableConditionalClearCarryFlag.Output.Connect(conditionalClearCarryFlag.Disable);
		cmpConstFromAddrRelay.Output.Connect(clearSourceOperand.Input);
		clearSourceOperand.Output.Connect(sourceOperand.Clears);
		cmpConstFromAddrRelay.Output.Connect(conditionalWriteToSourceOperand.Enables);
		cmpConstFromAddrRelay.Output.Connect(zeroFlag.Enable);
		cmpConstFromAddrRelay.Output.Connect(conditionalWriteToZeroFlag.Enable);
		cmpConstFromAddrRelay.Output.Connect(probeTargetOperandValue.Input);
		probeTargetOperandValue.Output.Connect(targetOperandValue.Probes);
		targetOperandValue.Ons.Connect(conditionalWriteToZeroFlag.Input);
		conditionalWriteToZeroFlag.Output.Connect(zeroFlag.Disable);
		cmpConstFromAddrRelay.Output.Connect(disableConditionalWriteToZeroFlag.Input);
		disableConditionalWriteToZeroFlag.Output.Connect(conditionalWriteToZeroFlag.Disable);
		// commented as this line is already present elsewhere
		//targetOperandValue.Ons.Connect(conditionalWriteToSourceOperand.Inputs);
		cmpConstFromAddrRelay.Output.Connect(disableConditionalWriteToSourceOperand.Input);

		subConstFromAddr.Output.Connect(sendTargetToAddress.Input);
		subConstFromAddr.Output.Connect(sendSourceToMemory.Input);

		subAddrFromAddr.Output.Connect(conditionalDerefSourceOperand.Enables[0]);
		subAddrFromAddr.Output.Connect(conditionalGetTargetOperandValueAsMemory.Enable);
		subAddrFromAddr.Output.Connect(cmpConstFromAddrRelay.Input);
		subAddrFromAddr.Output.Connect(disableConditionalDerefSourceOperand.Input);
		subAddrFromAddr.Output.Connect(disableConditionalGetTargetOperandValueAsMemory.Input);
		subAddrFromAddr.Output.Connect(sendTargetToAddress.Input);
		subAddrFromAddr.Output.Connect(sendSourceToMemory.Input);

		subIndirectFromAddr.Output.Connect(conditionalDerefSourceOperand.Enables);
		subIndirectFromAddr.Output.Connect(conditionalGetTargetOperandValueAsMemory.Enable);
		subIndirectFromAddr.Output.Connect(cmpConstFromAddrRelay.Input);
		subIndirectFromAddr.Output.Connect(disableConditionalDerefSourceOperand.Input);
		subIndirectFromAddr.Output.Connect(disableConditionalGetTargetOperandValueAsMemory.Input);
		subIndirectFromAddr.Output.Connect(sendTargetToAddress.Input);
		subIndirectFromAddr.Output.Connect(sendSourceToMemory.Input);

		subRegisterFromAddr.Output.Connect(conditionalGetTargetOperandValueAsMemory.Enable);
		subRegisterFromAddr.Output.Connect(conditionalDerefSourceOperandAsRegister.Enable);
		subRegisterFromAddr.Output.Connect(cmpConstFromAddrRelay.Input);
		subRegisterFromAddr.Output.Connect(disableConditionalGetTargetOperandValueAsMemory.Input);
		subRegisterFromAddr.Output.Connect(disableConditionalDerefSourceOperandAsRegister.Input);
		subRegisterFromAddr.Output.Connect(sendTargetToAddress.Input);
		subRegisterFromAddr.Output.Connect(sendSourceToMemory.Input);

		subRegisterFromRegister.Output.Connect(conditionalGetTargetOperandValueAsRegister.Enable);
		subRegisterFromRegister.Output.Connect(conditionalDerefSourceOperandAsRegister.Enable);
		subRegisterFromRegister.Output.Connect(cmpConstFromAddrRelay.Input);
		subRegisterFromRegister.Output.Connect(disableConditionalGetTargetOperandValueAsRegister.Input);
		subRegisterFromRegister.Output.Connect(disableConditionalDerefSourceOperandAsRegister.Input);
		subRegisterFromRegister.Output.Connect(sendTargetToRegisterAddress.Input);
		subRegisterFromRegister.Output.Connect(sendSourceToRegisterMemory.Input);

		cmpConstFromAddr.Output.Connect(conditionalGetTargetOperandValueAsMemory.Enable);
		cmpConstFromAddr.Output.Connect(cmpConstFromAddrRelay.Input);
		cmpConstFromAddr.Output.Connect(disableConditionalGetTargetOperandValueAsMemory.Input);

		cmpAddrFromAddr.Output.Connect(conditionalDerefSourceOperand.Enables[0]);
		cmpAddrFromAddr.Output.Connect(conditionalGetTargetOperandValueAsMemory.Enable);
		cmpAddrFromAddr.Output.Connect(cmpConstFromAddrRelay.Input);
		cmpAddrFromAddr.Output.Connect(disableConditionalDerefSourceOperand.Input);
		cmpAddrFromAddr.Output.Connect(disableConditionalGetTargetOperandValueAsMemory.Input);

		cmpIndirectFromAddr.Output.Connect(conditionalDerefSourceOperand.Enables);
		cmpIndirectFromAddr.Output.Connect(conditionalGetTargetOperandValueAsMemory.Enable);
		cmpIndirectFromAddr.Output.Connect(cmpConstFromAddrRelay.Input);
		cmpIndirectFromAddr.Output.Connect(disableConditionalDerefSourceOperand.Input);
		cmpIndirectFromAddr.Output.Connect(disableConditionalGetTargetOperandValueAsMemory.Input);

		cmpConstFromRegister.Output.Connect(conditionalGetTargetOperandValueAsRegister.Enable);
		cmpConstFromRegister.Output.Connect(cmpConstFromAddrRelay.Input);
		cmpConstFromRegister.Output.Connect(disableConditionalGetTargetOperandValueAsRegister.Input);
		disableConditionalGetTargetOperandValueAsRegister.Output.Connect(conditionalGetTargetOperandValueAsRegister.Disable);

		cmpRegisterFromRegister.Output.Connect(conditionalGetTargetOperandValueAsRegister.Enable);
		cmpRegisterFromRegister.Output.Connect(conditionalDerefSourceOperandAsRegister.Enable);
		cmpRegisterFromRegister.Output.Connect(cmpConstFromAddrRelay.Input);
		cmpRegisterFromRegister.Output.Connect(disableConditionalGetTargetOperandValueAsRegister.Input);
		cmpRegisterFromRegister.Output.Connect(disableConditionalDerefSourceOperandAsRegister.Input);

		jumpToConst.Output.Connect(incrementPC.Inputs[0]);
		jumpToConst.Output.Connect(jumpToConstRelay.Input);
		jumpToConstRelay.Output.Connect(fetchSourceOperand.Input);
		jumpToConstRelay.Output.Connect(conditionalDerefSourceOperandAsRegister.Input);
		jumpToConstRelay.Output.Connect(conditionalDerefSourceOperand.Inputs);
		jumpToConstRelay.Output.Connect(conditionalWriteToProgramCounter.Enables);
		jumpToConstRelay.Output.Connect(clearProgramCounter.Input);
		clearProgramCounter.Output.Connect(programCounter.Clears);
		jumpToConstRelay.Output.Connect(sourceOperand.Probes);
		sourceOperand.Ons.Connect(conditionalWriteToProgramCounter.Inputs);
		conditionalWriteToProgramCounter.Outputs.Connect(programCounter.Inputs);
		jumpToConstRelay.Output.Connect(disableConditionalWriteToProgramCounter.Input);
		disableConditionalWriteToProgramCounter.Output.Connect(conditionalWriteToProgramCounter.Disables);
		// As the fetch-exec cycle increments pc every step,
		// we need to decrement pc to keep an accurate jump value
		jumpToConstRelay.Output.Connect(decrementProgramCounter.Input);
		decrementProgramCounter.Output.Connect(programCounter.Inputs);

		jumpToAddr.Output.Connect(conditionalDerefSourceOperand.Enables[0]);
		jumpToAddr.Output.Connect(incrementPC.Inputs[0]);
		jumpToAddr.Output.Connect(jumpToConstRelay.Input);
		jumpToAddr.Output.Connect(disableConditionalDerefSourceOperand.Input);

		jumpToIndirect.Output.Connect(conditionalDerefSourceOperand.Enables);
		jumpToIndirect.Output.Connect(incrementPC.Inputs[0]);
		jumpToIndirect.Output.Connect(jumpToConstRelay.Input);
		jumpToIndirect.Output.Connect(disableConditionalDerefSourceOperand.Input);

		jumpToRegister.Output.Connect(incrementPC.Inputs[0]);
		jumpToRegister.Output.Connect(conditionalDerefSourceOperandAsRegister.Enable);
		jumpToRegister.Output.Connect(jumpToConstRelay.Input);
		jumpToRegister.Output.Connect(disableConditionalDerefSourceOperandAsRegister.Input);

		jumpIfGreater.Output.Connect(incrementPC.Inputs[0]);
		jumpIfGreater.Output.Connect(conditionalJumpToConst.Enable);
		jumpIfGreater.Output.Connect(carryFlag.Input);
		jumpIfGreater.Output.Connect(disableConditionalJumpToConst.Input);
		carryFlag.Output.Connect(conditionalJumpToConst.Input);
		conditionalJumpToConst.Output.Connect(jumpToConstRelay.Input);
		// Self disabling to work with other logic
		conditionalJumpToConst.Output.Connect(conditionalJumpToConst.Disable);

		jumpIfGreaterOrEqual.Output.Connect(incrementPC.Inputs[0]);
		jumpIfGreaterOrEqual.Output.Connect(conditionalJumpToConst.Enable);
		jumpIfGreaterOrEqual.Output.Connect(carryFlag.Input);
		// carryflag to condJump, but that's already done
		jumpIfGreaterOrEqual.Output.Connect(zeroFlag.Input);
		// This will fire on either zero or carry, but not twice
		// as it's a self disable trigger
		zeroFlag.Output.Connect(conditionalJumpToConst.Input);
		jumpIfGreaterOrEqual.Output.Connect(disableConditionalJumpToConst.Input);
		disableConditionalJumpToConst.Output.Connect(conditionalJumpToConst.Disable);

		jumpIfLess.Output.Connect(incrementPC.Inputs[0]);
		jumpIfLess.Output.Connect(enableConditionalNotJumpToConst.Input);
		// enableCond to cond.Enable
		jumpIfLess.Output.Connect(carryFlag.Input);
		// carryFlag to disableCondNotJump
		jumpIfLess.Output.Connect(zeroFlag.Input);
		zeroFlag.Output.Connect(conditionalNotJumpToConst.Disable);
		jumpIfLess.Output.Connect(conditionalNotJumpToConst.Input);
		jumpIfLess.Output.Connect(disableConditionalNotJumpToConst.Input);
		disableConditionalNotJumpToConst.Output.Connect(conditionalNotJumpToConst.Disable);
		// cond to jumpToConst

		jumpIfLessOrEqual.Output.Connect(incrementPC.Inputs[0]);
		jumpIfLessOrEqual.Output.Connect(enableConditionalNotJumpToConst.Input);
		enableConditionalNotJumpToConst.Output.Connect(conditionalNotJumpToConst.Enable);
		jumpIfLessOrEqual.Output.Connect(carryFlag.Input);
		carryFlag.Output.Connect(conditionalNotJumpToConst.Disable);
		jumpIfLessOrEqual.Output.Connect(conditionalNotJumpToConst.Input);
		conditionalNotJumpToConst.Output.Connect(jumpToConstRelay.Input);
		jumpIfLessOrEqual.Output.Connect(disableConditionalNotJumpToConst.Input);

		jumpIfZero.Output.Connect(incrementPC.Inputs[0]);
		jumpIfZero.Output.Connect(conditionalJumpToConst.Enable);
		jumpIfZero.Output.Connect(zeroFlag.Input);
		jumpIfZero.Output.Connect(disableConditionalJumpToConst.Input);
		// zeroflag to condJump, condJump to jumpToConst

		jumpIfNotZero.Output.Connect(incrementPC.Inputs[0]);
		jumpIfNotZero.Output.Connect(enableConditionalNotJumpToConst.Input);
		jumpIfNotZero.Output.Connect(zeroFlag.Input);
		jumpIfNotZero.Output.Connect(conditionalNotJumpToConst.Input);
		jumpIfNotZero.Output.Connect(disableConditionalNotJumpToConst.Input);

		// This instruction is going to use the 3 op registers a little differently:
		// Multiply requires full use of 3 registers, so I'll use targetOperand
		// to store the target operand's value instead of targetOperandValue,
		// and I'll use targetOperandValue to store the result of the multiplication
		// as it happens.
		// Afterwards I'll just fetch the targetOperand address again.
		mulConstWithAddr.Output.Connect(conditionalDerefTargetOperand.Enable);
		mulConstWithAddr.Output.Connect(mulConstWithAddrRelay.Input);
		mulConstWithAddr.Output.Connect(disableConditionalDerefTargetOperand.Input);
		mulConstWithAddrRelay.Output.Connect(incrementPC.Inputs[0]);
		mulConstWithAddrRelay.Output.Connect(fetchSourceOperand.Input);
		mulConstWithAddrRelay.Output.Connect(incrementPC.Inputs[1]);
		mulConstWithAddrRelay.Output.Connect(fetchTargetOperand.Input);
		mulConstWithAddrRelay.Output.Connect(targetOperandValue.Clears);
		mulConstWithAddrRelay.Output.Connect(conditionalDerefTargetOperandRegister.Input);
		mulConstWithAddrRelay.Output.Connect(conditionalDerefTargetOperand.Input);
		mulConstWithAddrRelay.Output.Connect(disableConditionalDerefTargetOperand.Input);
		// We can't just enable writeToTargetValue as we're also probing
		// the source operand, which will write to target value also.
		mulConstWithAddrRelay.Output.Connect(conditionalWriteTargetToTargetValue.Enables);
		mulConstWithAddrRelay.Output.Connect(enableShiftTargetValue.Input);
		enableShiftTargetValue.Output.Connect(shiftTargetValue.Enables);
		mulConstWithAddrRelay.Output.Connect(conditionalProbeTargetOperand.Enable);
		// pasted from Multiplier
		Input PowerLast = new(sourceOperand.Probes
			.Zip(shiftTargetValue.Inputs, (lhs, rhs) => new[] {lhs, rhs}).SelectMany(l => l).SelectMany(l => l).ToList()
		);
		PowerLast.Reverse();
		mulConstWithAddrRelay.Output.Connect(PowerLast);
		shiftTargetValue.Outputs.Connect(targetOperandValue.ShiftLeft);
		targetOperand.Ons.Connect(conditionalWriteTargetToTargetValue.Inputs);
		conditionalWriteTargetToTargetValue.Outputs.Connect(targetOperandValue.Inputs);
		sourceOperand.Ons.Connect(conditionalProbeTargetOperand.Input);
		conditionalProbeTargetOperand.Output.Connect(targetOperand.Probes);
		mulConstWithAddrRelay.Output.Connect(disableConditionalWriteTargetToTargetValue.Input);
		disableConditionalWriteTargetToTargetValue.Output.Connect(conditionalWriteTargetToTargetValue.Disables);
		mulConstWithAddrRelay.Output.Connect(disableShiftTargetValue.Input);
		disableShiftTargetValue.Output.Connect(shiftTargetValue.Disables);
		mulConstWithAddrRelay.Output.Connect(disableProbeTargetOperand.Input);
		disableProbeTargetOperand.Output.Connect(conditionalProbeTargetOperand.Disable);
		mulConstWithAddrRelay.Output.Connect(fetchTargetOperandRelay.Input);
		fetchTargetOperandRelay.Output.Connect(fetchTargetOperand.Input);
		// Now our result value is in targetOperandValue,
		// and our result address is in targetOperand
		mulConstWithAddrRelay.Output.Connect(clearSourceOperand.Input);
		mulConstWithAddrRelay.Output.Connect(conditionalWriteToSourceOperand.Enables);
		mulConstWithAddrRelay.Output.Connect(probeTargetOperandValue.Input);
		// targetOperandValue.Ons.Connect(conditionalWriteToSourceOperand.Inputs);
		mulConstWithAddrRelay.Output.Connect(disableConditionalWriteToSourceOperand.Input);
		mulConstWithAddr.Output.Connect(sendTargetToAddress.Input);
		mulConstWithAddr.Output.Connect(sendSourceToMemory.Input);

		mulConstWithRegister.Output.Connect(conditionalDerefTargetOperandRegister.Enable);
		mulConstWithRegister.Output.Connect(mulConstWithAddrRelay.Input);
		mulConstWithRegister.Output.Connect(disableConditionalDerefTargetOperandRegister.Input);
		mulConstWithRegister.Output.Connect(sendTargetToRegisterAddress.Input);
		mulConstWithRegister.Output.Connect(sendSourceToRegisterMemory.Input);

		stopStepping.Output.Connect(continuous.Clear);
		stopStepping.Output.Connect(repeatStep.Disable);

		resumeSteppingAfterFrame.Output.Connect(resumeStepping.Input);
		resumeStepping.Output.Connect(continuous.Set);
		resumeStepping.Output.Connect(repeatStep.Enable);
		resumeStepping.Output.Connect(step.Input);

		pushConst.Output.Connect(pushConstRelay.Input);
		pushConstRelay.Output.Connect(incrementPC.Inputs[0]);
		pushConstRelay.Output.Connect(fetchSourceOperand.Input);
		pushConstRelay.Output.Connect(conditionalDerefSourceOperandAsRegister.Input);
		pushConstRelay.Output.Connect(conditionalDerefSourceOperand.Inputs);
		// Hard-wiring the fact that register 0 is "sp", fetching value of sp
		pushConstRelay.Output.Connect(registers.AddressClears);
		pushConstRelay.Output.Connect(targetOperand.Clears);
		pushConstRelay.Output.Connect(conditionalWriteToTargetOperand.Enables);
		pushConstRelay.Output.Connect(readRegisters.Input);
		readRegisters.Output.Connect(readRegistersRelay.Input);
		pushConstRelay.Output.Connect(disableConditionalWriteToTargetOperand.Input);
		pushConstRelay.Output.Connect(sendTargetToAddress.Input);
		pushConstRelay.Output.Connect(sendSourceToMemory.Input);
		// Move sp into source operand
		pushConstRelay.Output.Connect(sourceOperand.Clears);
		pushConstRelay.Output.Connect(conditionalWriteToSourceOperand.Enables);
		pushConstRelay.Output.Connect(probeTargetOperand.Input);
		probeTargetOperand.Output.Connect(targetOperand.Probes);
		// For some reason I never had this line before,
		// maybe I avoided it because it messed with some other instruction?
		targetOperand.Ons.Connect(conditionalWriteToSourceOperand.Inputs);
		pushConstRelay.Output.Connect(disableConditionalWriteToSourceOperand.Input);
		// Decrement source operand
		pushConstRelay.Output.Connect(decrementSourceOperand.Input);
		decrementSourceOperand.Output.Connect(sourceOperand.Inputs);
		// Move back into sp
		// (sp address is still in registerAddress)
		pushConstRelay.Output.Connect(sendSourceToRegisterMemory.Input);

		pushRegister.Output.Connect(conditionalDerefSourceOperandAsRegister.Enable);
		pushRegister.Output.Connect(pushConstRelay.Input);
		pushRegister.Output.Connect(disableConditionalDerefSourceOperandAsRegister.Input);

		// We increment sp before popping because in the push,
		// we decrement sp after pushing.
		popRegister.Output.Connect(sourceOperand.Clears);
		popRegister.Output.Connect(conditionalDerefSourceOperandAsRegister.Enable);
		popRegister.Output.Connect(incrementRegisterRelay.Input);
		popRegister.Output.Connect(disableConditionalDerefSourceOperandAsRegister.Input);
		// Incrementregister relay also puts the incremented relay into source
		popRegister.Output.Connect(incrementPC.Inputs[0]);
		// // This has only one operand, the target register
		popRegister.Output.Connect(fetchTargetOperand.Input);
		// // This puts the address of what we want to put into the register, into source
		// // so now we dereference it
		popRegister.Output.Connect(enableConditionalDerefSourceOperand.Input);
		enableConditionalDerefSourceOperand.Output.Connect(conditionalDerefSourceOperand.Enables);
		popRegister.Output.Connect(conditionalDerefSourceOperand.Inputs[0]);
		popRegister.Output.Connect(disableConditionalDerefSourceOperand.Input);
		// // Now we send the value to the register
		popRegister.Output.Connect(sendTargetToRegisterAddress.Input);
		popRegister.Output.Connect(sendSourceToRegisterMemory.Input);

		pushAddr.Output.Connect(conditionalDerefSourceOperand.Enables[0]);
		pushAddr.Output.Connect(pushConstRelay.Input);
		pushAddr.Output.Connect(disableConditionalDerefSourceOperand.Input);

		playSound.Output.Connect(new Input(new[]{(Target)new Target<Trigger>(playSoundTrigger, new Activate())}));

		// playSoundTwo.Output.Connect(new Input(new[]{(Target)new Target<Trigger>(playSoundTriggerTwo, new Activate())}));

		drawAtConst.Output.Connect(drawAtConstRelay.Input);
		drawAtConstRelay.Output.Connect(incrementPC.Inputs[0]);
		drawAtConstRelay.Output.Connect(fetchTargetOperand.Input);
		drawAtConstRelay.Output.Connect(conditionalDerefTargetOperandRegister.Input);
		drawAtConstRelay.Output.Connect(sendTargetToScreenAddress.Input);
		// Enable is mandatory because it's what selects the memory row
		drawAtConstRelay.Output.Connect(screen.EnableWrite);
		drawAtConstRelay.Output.Connect(writeBitToScreenRelay.Input);
		writeBitToScreenRelay.Output.Connect(screen.Writes[0]);
		// I think disable is optional, but test it out later
		//drawAtConstRelay.Output.Connect(screen.DisableWrite);

		drawAtRegister.Output.Connect(conditionalDerefTargetOperandRegister.Enable);
		drawAtRegister.Output.Connect(drawAtConstRelay.Input);
		drawAtRegister.Output.Connect(disableConditionalDerefTargetOperandRegister.Input);

		clearScreen.Output.Connect(screen.ClearScreen);

		AuxillaryOne = auxillaryOne.Output;

		AuxillaryTwo = auxillaryTwo.Output;
		
		sendSourceToMemory.Output.Connect(memory.EnableWrite);
		sendSourceToMemory.Output.Connect(sourceOperand.Probes);
		sourceOperand.Ons.Connect(memory.Writes);
		sendSourceToMemory.Output.Connect(memory.DisableWrite);

		sendSourceToRegisterMemory.Output.Connect(registers.EnableWrite);
		sendSourceToRegisterMemory.Output.Connect(sourceOperand.Probes);
		sourceOperand.Ons.Connect(registers.Writes);
		sendSourceToRegisterMemory.Output.Connect(registers.DisableWrite);

		conditionalDerefSourceOperand.Outputs[0].Connect(sendSourceToAddress.Input);
		conditionalDerefSourceOperand.Outputs[0].Connect(sourceOperand.Clears);
		conditionalDerefSourceOperand.Outputs[0].Connect(conditionalWriteToSourceOperand.Enables);
		conditionalDerefSourceOperand.Outputs[0].Connect(readMemoryRelay.Input);
		//memoryValues.Outputs.Connect(conditionalWriteToSourceOperand.Inputs);
		//conditionalWriteToSourceOperand.Outputs.Connect(sourceOperand.Inputs);
		conditionalDerefSourceOperand.Outputs[0].Connect(disableConditionalWriteToSourceOperand.Input);

		conditionalDerefSourceOperandAsRegister.Output.Connect(sendSourceToRegisterAddress.Input);
		conditionalDerefSourceOperandAsRegister.Output.Connect(sourceOperand.Clears);
		conditionalDerefSourceOperandAsRegister.Output.Connect(conditionalWriteToSourceOperand.Enables);
		conditionalDerefSourceOperandAsRegister.Output.Connect(readRegistersRelay.Input);
		registerValues.Outputs.Connect(conditionalWriteToSourceOperand.Inputs);
		conditionalDerefSourceOperandAsRegister.Output.Connect(disableConditionalWriteToSourceOperand.Input);

		conditionalGetTargetOperandValueAsMemory.Output.Connect(sendTargetToAddress.Input);
		conditionalGetTargetOperandValueAsMemory.Output.Connect(targetOperandValue.Clears);
		conditionalGetTargetOperandValueAsMemory.Output.Connect(conditionalWriteToTargetOperandValue.Enables);
		conditionalGetTargetOperandValueAsMemory.Output.Connect(readMemoryRelay.Input);
		memoryValues.Outputs.Connect(conditionalWriteToTargetOperandValue.Inputs);
		conditionalWriteToTargetOperandValue.Outputs.Connect(targetOperandValue.Inputs);
		conditionalGetTargetOperandValueAsMemory.Output.Connect(disableConditionalWriteToTargetOperandValue.Input);
		disableConditionalWriteToTargetOperandValue.Output.Connect(conditionalWriteToTargetOperandValue.Disables);

		conditionalGetTargetOperandValueAsRegister.Output.Connect(sendTargetToRegisterAddress.Input);
		conditionalGetTargetOperandValueAsRegister.Output.Connect(targetOperandValue.Clears);
		conditionalGetTargetOperandValueAsRegister.Output.Connect(conditionalWriteToTargetOperandValue.Enables);
		conditionalGetTargetOperandValueAsRegister.Output.Connect(readRegistersRelay.Input);
		registerValues.Outputs.Connect(conditionalWriteToTargetOperandValue.Inputs);
		conditionalGetTargetOperandValueAsRegister.Output.Connect(disableConditionalWriteToTargetOperandValue.Input);

		conditionalDerefTargetOperand.Output.Connect(sendTargetToAddress.Input);
		conditionalDerefTargetOperand.Output.Connect(targetOperand.Clears);
		conditionalDerefTargetOperand.Output.Connect(conditionalWriteToTargetOperand.Enables);
		conditionalDerefTargetOperand.Output.Connect(readMemoryRelay.Input);
		// mem already connected to conditionalWrite
		conditionalDerefTargetOperand.Output.Connect(disableConditionalWriteToTargetOperand.Input);

		conditionalDerefTargetOperandRegister.Output.Connect(sendTargetToRegisterAddress.Input);
		conditionalDerefTargetOperandRegister.Output.Connect(targetOperand.Clears);
		conditionalDerefTargetOperandRegister.Output.Connect(conditionalWriteToTargetOperand.Enables);
		conditionalDerefTargetOperandRegister.Output.Connect(readRegistersRelay.Input);
		registerValues.Outputs.Connect(conditionalWriteToTargetOperand.Inputs);
		conditionalDerefTargetOperandRegister.Output.Connect(disableConditionalWriteToTargetOperand.Input);

		sendPCToAddress.Output.Connect(memory.AddressClears);
		sendPCToAddress.Output.Connect(conditionalAddToAddress.Enables);
		sendPCToAddress.Output.Connect(programCounter.Probes);
		programCounter.Ons.Connect(conditionalAddToAddress.Inputs);
		conditionalAddToAddress.Outputs.Connect(memory.Address);
		sendPCToAddress.Output.Connect(disableConditionalAddToAddress.Input);
		disableConditionalAddToAddress.Output.Connect(conditionalAddToAddress.Disables);

		sendTargetToAddress.Output.Connect(memory.AddressClears);
		sendTargetToAddress.Output.Connect(conditionalAddToAddress.Enables);
		sendTargetToAddress.Output.Connect(targetOperand.Probes);
		targetOperand.Ons.Connect(conditionalAddToAddress.Inputs);
		sendTargetToAddress.Output.Connect(disableConditionalAddToAddress.Input);

		sendTargetToRegisterAddress.Output.Connect(registers.AddressClears);
		sendTargetToRegisterAddress.Output.Connect(conditionalAddToRegisterAddress.Enables);
		sendTargetToRegisterAddress.Output.Connect(targetOperand.Probes);
		targetOperand.Ons.Connect(conditionalAddToRegisterAddress.Inputs);
		conditionalAddToRegisterAddress.Outputs.Connect(registers.Address);
		sendTargetToRegisterAddress.Output.Connect(disableConditionalAddToRegisterAddress.Input);
		disableConditionalAddToRegisterAddress.Output.Connect(conditionalAddToRegisterAddress.Disables);

		sendTargetToScreenAddress.Output.Connect(screen.AddressClears);
		sendTargetToScreenAddress.Output.Connect(conditionalAddToScreenAddress.Enables);
		sendTargetToScreenAddress.Output.Connect(targetOperand.Probes);
		targetOperand.Ons.Connect(conditionalAddToScreenAddress.Inputs);
		conditionalAddToScreenAddress.Outputs.Connect(screen.Address);
		sendTargetToScreenAddress.Output.Connect(disableConditionalAddToScreenAddress.Input);
		disableConditionalAddToScreenAddress.Output.Connect(conditionalAddToScreenAddress.Disables);

		sendSourceToAddress.Output.Connect(memory.AddressClears);
		sendSourceToAddress.Output.Connect(conditionalAddToAddress.Enables);
		sendSourceToAddress.Output.Connect(sourceOperand.Probes);
		sourceOperand.Ons.Connect(conditionalAddToAddress.Inputs);
		sendSourceToAddress.Output.Connect(disableConditionalAddToAddress.Input);

		sendSourceToRegisterAddress.Output.Connect(registers.AddressClears);
		sendSourceToRegisterAddress.Output.Connect(conditionalAddToRegisterAddress.Enables);
		sendSourceToRegisterAddress.Output.Connect(sourceOperand.Probes);
		sourceOperand.Ons.Connect(conditionalAddToRegisterAddress.Inputs);
		sendSourceToRegisterAddress.Output.Connect(disableConditionalAddToRegisterAddress.Input);

		fetchInstructionRelay.Output.Connect(sendPCToAddress.Input);
		fetchInstructionRelay.Output.Connect(instruction.Clears);
        fetchInstructionRelay.Output.Connect(conditionalWriteToInstruction.Enables);
		fetchInstructionRelay.Output.Connect(readMemoryRelay.Input);
		memoryValues.Outputs.Connect(conditionalWriteToInstruction.Inputs);
		conditionalWriteToInstruction.Outputs.Connect(instruction.Inputs);
		fetchInstructionRelay.Output.Connect(disableConditionalWriteToInstruction.Input);
		disableConditionalWriteToInstruction.Output.Connect(conditionalWriteToInstruction.Disables);

		fetchSourceOperand.Output.Connect(sendPCToAddress.Input);
		fetchSourceOperand.Output.Connect(sourceOperand.Clears);
		fetchSourceOperand.Output.Connect(conditionalWriteToSourceOperand.Enables);
		fetchSourceOperand.Output.Connect(readMemoryRelay.Input);
		memoryValues.Outputs.Connect(conditionalWriteToSourceOperand.Inputs);
		conditionalWriteToSourceOperand.Outputs.Connect(sourceOperand.Inputs);
		fetchSourceOperand.Output.Connect(disableConditionalWriteToSourceOperand.Input);
		disableConditionalWriteToSourceOperand.Output.Connect(conditionalWriteToSourceOperand.Disables);

		fetchTargetOperand.Output.Connect(sendPCToAddress.Input);
		fetchTargetOperand.Output.Connect(targetOperand.Clears);
		fetchTargetOperand.Output.Connect(conditionalWriteToTargetOperand.Enables);
		fetchTargetOperand.Output.Connect(readMemoryRelay.Input);
		memoryValues.Outputs.Connect(conditionalWriteToTargetOperand.Inputs);
		conditionalWriteToTargetOperand.Outputs.Connect(targetOperand.Inputs);
		fetchTargetOperand.Output.Connect(disableConditionalWriteToTargetOperand.Input);
		disableConditionalWriteToTargetOperand.Output.Connect(conditionalWriteToTargetOperand.Disables);

        Entities = (ownControls ? Controls.Entities : Enumerable.Empty<Entity>())
			.Concat(instruction.Entities)
			.Concat(sourceOperand.Entities)
			.Concat(carryFlag.Entities)
			.Concat(conditionalClearCarryFlag.Entities)
			.Concat(conditionalSetCarryFlag.Entities)
			.Concat(disableConditionalClearCarryFlag.Entities)
			.Concat(disableConditionalSetCarryFlag.Entities)
			.Concat(zeroFlag.Entities)
			.Concat(conditionalWriteToZeroFlag.Entities)
			.Concat(disableConditionalWriteToZeroFlag.Entities)
			.Concat(targetOperand.Entities)
			.Concat(targetOperandValue.Entities)
			.Concat(shiftTargetValue.Entities)
			.Concat(enableShiftTargetValue.Entities)
			.Concat(disableShiftTargetValue.Entities)
			.Concat(conditionalProbeTargetOperand.Entities)
			.Concat(disableProbeTargetOperand.Entities)
			.Concat(fetchTargetOperandRelay.Entities)
			.Concat(programCounter.Entities)
			.Concat(conditionalWriteToProgramCounter.Entities)
			.Concat(disableConditionalWriteToProgramCounter.Entities)
			.Concat(clearProgramCounter.Entities)
			.Concat(incrementPC.Entities)
			.Concat(decrementProgramCounter.Entities)
			.Concat(decrementSourceOperand.Entities)
			.Concat(writeToRegisters.Entities)
			.Concat(readMemoryRelay.Entities)
			.Concat(memoryValues.Entities)
			.Concat(readRegisters.Entities)
			.Concat(conditionalDerefSourceOperand.Entities)
			.Concat(conditionalDerefSourceOperandAsRegister.Entities)
			.Concat(disableConditionalDerefSourceOperand.Entities)
			.Concat(disableConditionalDerefSourceOperandAsRegister.Entities)
			.Concat(enableConditionalDerefSourceOperand.Entities)
			.Concat(enableConditionalDerefSourceOperandAsRegister.Entities)
			.Concat(conditionalGetTargetOperandValueAsMemory.Entities)
			.Concat(conditionalGetTargetOperandValueAsRegister.Entities)
			.Concat(disableConditionalGetTargetOperandValueAsMemory.Entities)
			.Concat(disableConditionalGetTargetOperandValueAsRegister.Entities)
			.Concat(conditionalDerefTargetOperand.Entities)
			.Concat(conditionalDerefTargetOperandRegister.Entities)
			.Concat(disableConditionalDerefTargetOperandRegister.Entities)
			.Concat(sendSourceToMemory.Entities)
			.Concat(sendSourceToRegisterMemory.Entities)
			.Concat(moveConstToAddr.Entities)
			.Concat(moveConstToAddrRelay.Entities)
			.Concat(moveAddrToAddr.Entities)
			.Concat(moveIndirectToAddr.Entities)
			.Concat(moveConstToAddrIndirect.Entities)
			.Concat(moveAddrToAddrIndirect.Entities)
			.Concat(moveConstToRegister.Entities)
			.Concat(moveConstToRegisterRelay.Entities)
			.Concat(moveRegisterToRegister.Entities)
			.Concat(moveConstToRegisterIndirect.Entities)
			.Concat(moveRegisterToRegisterIndirect.Entities)
			.Concat(disableConditionalDerefTargetOperand.Entities)
			.Concat(addConstToAddr.Entities)
			.Concat(addConstToAddrRelay.Entities)
			.Concat(addAddrToAddr.Entities)
			.Concat(addIndirectToAddr.Entities)
			.Concat(subConstFromAddr.Entities)
			.Concat(subAddrFromAddr.Entities)
			.Concat(subIndirectFromAddr.Entities)
			.Concat(cmpConstFromAddrRelay.Entities)
			.Concat(cmpConstFromAddr.Entities)
			.Concat(cmpAddrFromAddr.Entities)
			.Concat(cmpIndirectFromAddr.Entities)
			.Concat(jumpToConstRelay.Entities)
			.Concat(jumpToConst.Entities)
			.Concat(conditionalJumpToConst.Entities)
			.Concat(conditionalNotJumpToConst.Entities)
			.Concat(enableConditionalNotJumpToConst.Entities)
			.Concat(disableConditionalNotJumpToConst.Entities)
			.Concat(disableConditionalJumpToConst.Entities)
			.Concat(jumpToAddr.Entities)
			.Concat(jumpToIndirect.Entities)
			.Concat(jumpIfGreater.Entities)
			.Concat(jumpIfGreaterOrEqual.Entities)
			.Concat(jumpIfLess.Entities)
			.Concat(jumpIfLessOrEqual.Entities)
			.Concat(jumpIfZero.Entities)
			.Concat(jumpIfNotZero.Entities)
			.Concat(mulConstWithAddr.Entities)
			.Concat(mulConstWithAddrRelay.Entities)
			.Concat(stopStepping.Entities)
			.Concat(incrementRegister.Entities)
			.Concat(incrementRegisterRelay.Entities)
			.Concat(cmpConstFromRegister.Entities)
			.Concat(cmpRegisterFromRegister.Entities)
			.Concat(addConstToRegister.Entities)
			.Concat(addRegisterToRegister.Entities)
			.Concat(pushConst.Entities)
			.Concat(pushConstRelay.Entities)
			.Concat(pushRegister.Entities)
			.Concat(popRegister.Entities)
			.Concat(mulConstWithRegister.Entities)
			.Concat(moveAddrToRegister.Entities)
			.Concat(pushAddr.Entities)
			.Concat(jumpToRegister.Entities)
			.Concat(clearScreen.Entities)
			.Concat(resumeSteppingAfterFrame.Entities)
			.Concat(resumeStepping.Entities)
			.Concat(new[]{playSoundTrigger})
			.Concat(playSound.Entities)
			// .Concat(playSoundTwo.Entities)
			.Concat(subRegisterFromAddr.Entities)
			.Concat(drawAtConst.Entities)
			.Concat(drawAtConstRelay.Entities)
			.Concat(drawAtRegister.Entities)
			.Concat(auxillaryOne.Entities)
			.Concat(auxillaryTwo.Entities)
			.Concat(subRegisterFromRegister.Entities)
			.Concat(addRegisterToAddr.Entities)
			.Concat(memory.Entities)
			.Concat(screen.Entities)
			.Concat(sendPCToAddress.Entities)
			.Concat(conditionalAddToAddress.Entities)
			.Concat(disableConditionalAddToAddress.Entities)
			.Concat(sendSourceToAddress.Entities)
			.Concat(sendSourceToRegisterAddress.Entities)
			.Concat(sendTargetToAddress.Entities)
			.Concat(sendTargetToRegisterAddress.Entities)
			.Concat(sendTargetToScreenAddress.Entities)
			.Concat(conditionalAddToRegisterAddress.Entities)
			.Concat(conditionalAddToScreenAddress.Entities)
			.Concat(disableConditionalAddToRegisterAddress.Entities)
			.Concat(disableConditionalAddToScreenAddress.Entities)
			.Concat(setInstruction.Entities)
			.Concat(fetchInstructionRelay.Entities)
			.Concat(conditionalWriteToInstruction.Entities)
			.Concat(disableConditionalWriteToInstruction.Entities)
			.Concat(fetchSourceOperand.Entities)
			.Concat(conditionalWriteToSourceOperand.Entities)
			.Concat(disableConditionalWriteToSourceOperand.Entities)
			.Concat(clearSourceOperand.Entities)
			.Concat(probeTargetOperand.Entities)
			.Concat(probeTargetOperandValue.Entities)
			.Concat(fetchTargetOperand.Entities)
			.Concat(conditionalWriteToTargetOperand.Entities)
			.Concat(disableConditionalWriteToTargetOperand.Entities)
			.Concat(conditionalWriteToTargetOperandValue.Entities)
			.Concat(disableConditionalWriteToTargetOperandValue.Entities)
			.Concat(conditionalWriteTargetToTargetValue.Entities)
			.Concat(disableConditionalWriteTargetToTargetValue.Entities)
			.Concat(conditionalSubtractFromTargetOperandValue.Entities)
			.Concat(enableConditionalSubtractFromTargetOperandValue.Entities)
			.Concat(executeInstructionRelay.Entities)
			.Concat(nextInstruction.Entities)
			.Concat(step.Entities)
			.Concat(continuous.Entities)
			.Concat(repeatStep.Entities)
			// .Concat(derefSourceOperandButton.Entities)
			// .Concat(enableDerefSourceOperand.Entities)
			// .Concat(derefSourceOperandLabel.Entities)
			// .Concat(fetchSourceOperandButton.Entities)
			// .Concat(fetchSourceOperandLabel.Entities)
			// .Concat(fetchTargetOperandButton.Entities)
			// .Concat(fetchTargetOperandLabel.Entities)
			// .Concat(targetInput.Entities)
			// .Concat(targetInputLabel.Entities)
			.Concat(registers.Entities)
			.Concat(readRegistersRelay.Entities)
			.Concat(registerValues.Entities)
			.Concat(enableStep.Entities)
			.Concat(writeBitToScreenRelay.Entities)
			.Concat(new Entity[]{lagCube, spinTheLagCube})
			// .Concat(labelPC.Entities)
			// .Concat(labelInstruction.Entities)
			// .Concat(stepLabelTwo.Entities)
			// .Concat(stepLabel.Entities)
			// .Concat(nextInstructionLabel.Entities)
			// .Concat(executeInstructionLabel.Entities)
			// .Concat(fetchInstructionLabel.Entities)
			// .Concat(targetValueLabel.Entities)
			// .Concat(targetLabel.Entities)
			// .Concat(zeroFlagLabel.Entities)
			// .Concat(carryLabel.Entities)
			// .Concat(sourceLabel.Entities)
			// .Concat(stepButtonTwo.Entities)
			// .Concat(continuousButton.Entities)
			// .Concat(stepButton.Entities)
			// .Concat(nextInstructionButton.Entities)
			// .Concat(executeInstructionButton.Entities)
			// .Concat(fetchInstructionButton.Entities)
			// .Concat(instructionButtons.Entities)
			// .Concat(clearInstructionButton.Entities)
			// .Concat(clearProgramCounterButton.Entities)
			// .Concat(programCounterButtons.Entities)
			// .Concat(continuousPanel.Entities)
			;
			if (visualized)
			{
				continuous.On.Connect(continuousPanel.Input);
				continuous.Off.Connect(continuousPanel.Reset);
				resumeStepping.Output.Connect(continuousPanel.Input);
				stopStepping.Output.Connect(continuousPanel.Reset);
				Entities = Entities
					.Concat(labelPC.Entities)
					.Concat(labelInstruction.Entities)
					// .Concat(stepLabelTwo.Entities)
					.Concat(stepLabel.Entities)
					.Concat(showPCLabel.Entities)
					.Concat(nextInstructionLabel.Entities)
					.Concat(executeInstructionLabel.Entities)
					.Concat(fetchInstructionLabel.Entities)
					.Concat(targetValueLabel.Entities)
					.Concat(targetLabel.Entities)
					.Concat(zeroFlagLabel.Entities)
					.Concat(carryLabel.Entities)
					.Concat(sourceLabel.Entities)
					// .Concat(stepButtonTwo.Entities)
					.Concat(continuousButton.Entities)
					.Concat(stepButton.Entities)
					.Concat(showPCButton.Entities)
					.Concat(nextInstructionButton.Entities)
					.Concat(executeInstructionButton.Entities)
					.Concat(fetchInstructionButton.Entities)
					.Concat(instructionButtons.SelectMany(button => button.Entities))
					.Concat(clearInstructionButton.Entities)
					.Concat(clearProgramCounterButton.Entities)
					.Concat(programCounterButtons.SelectMany(button => button.Entities))
					.Concat(continuousPanel.Entities)
				;
			}
	}
}