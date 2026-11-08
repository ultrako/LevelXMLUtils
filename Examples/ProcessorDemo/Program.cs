// A level to try the processor in by hand: memory and registers you click to set, buttons to fetch, execute and step, an 8 by 8 screen
// wired to the controls, and the instruction reference written beside it. Use the second auxiliary instruction five times to win.
//   dotnet run --project Examples/ProcessorDemo -- processor-demo.xml
using System.Reflection;
using LevelXML;
using LevelXMLUtils;
using LevelXMLUtils.Computer;

string output = args.Length > 0 ? args[0] : "processor-demo.xml";
int n = 6;
double x = 800;
double y = 5100;
double width = 30;
double height = 30;
uint displayWidth = 7;
uint displayHeight = 5;
Processor processor = new(n, x, y, width, height, displayWidth, displayHeight, visualized:true, visualizedMemory:true, allMemoryIsROM:false, stepDelay:0.034);
EveryFrameTrigger everyFrame = new(0,0,50,50);
TinyFallingSquare tinyFallingSquare = new(10,10);
InstantToggleChain counter = new(5, x-width*2,y-height*20,width,height);
VictoryTrigger victory = new() { X = x-width*2, Y = y-height*20, Width = width, Height = height, TriggeredBy = TriggeredBy.Triggers };
processor.AuxillaryTwo.Connect(counter.Inputs[0]);
counter.Offs[4].Connect(new Input { new Target<Trigger>(victory, new Activate()) });
new Output(new[]{(ActivateTrigger)everyFrame}).Connect(counter.Clears);
Polygon floor = new()
{
    X = 0,
    Y = 5100 + 200,
    Width = 10000,
    Height = 10,
    Opacity = 15
};
floor.Vertices.Add(new(new(0,0)));
floor.Vertices.Add(new(new(10000,0)));
floor.Vertices.Add(new(new(10000,10)));
floor.Vertices.Add(new(new(0,10)));
Chain shapeLimitDisabler = new()
{
    LinkCount = double.NaN
};
TextBox interactionTutorialTextBox = new()
{
    X = x-800,
    Y = y-165,
    Content = Text.interactionTutorial,
};
TextBox addressingModesTextBox = new()
{
    X = x + Math.Pow(2, n)*width + width*5,
    Y = y-150,
    Content = Text.addressingModesOverview,
};
TextBox stackDescriptionTextBox = new()
{
    X = addressingModesTextBox.X + 100,
    Y = addressingModesTextBox.Y + 250,
    Content = Text.stackOverview,
};
string[] instructionDescriptions = new[]
{
    Text.nopOverview,
    Text.moveOverview,
    Text.addOverview,
    Text.incrementOverview,
    Text.subtractOverview,
    Text.compareOverview,
    Text.jumpOverview,
    Text.conditionalJumpsOverview,
    Text.multiplyOverview,
    Text.stopSteppingOverview,
    Text.resumeSteppingOverview,
    Text.pushOverview,
    Text.popOverview,
    Text.playSoundOverview,
    Text.drawOverview,
    Text.clearScreenOverview,
    Text.auxillaryOverview,
};
IEnumerable<Entity> instructionDescriptionTextBoxes = instructionDescriptions
    .Select((content, index) => 
        new TextBox()
        {
            X = addressingModesTextBox.X + 800 + 600*(index/4),
            Y = y-150 + 90*(index%4),
            Content = content,
        }
    );
TextBox introToInstructionNumbers = new()
{
    X = addressingModesTextBox.X + 3900,
    Y = y-150,
    Content = 
    @"Here are all of the instruction opcodes. "
};
List<Entity> opcodeNumberTextboxes = new();
foreach ((FieldInfo field, int index) in typeof(Processor.Opcode)
    .GetFields(BindingFlags.Public | BindingFlags.Static)
    .Where(field => field.FieldType == typeof(Processor.Opcode))
    .Select((field, index) => (field, index)))
{
    string name = field.Name;
    uint value = (uint)(Processor.Opcode)field.GetValue(null)!;
    double opcodeX = introToInstructionNumbers.X + 300 + 260*(index/10);
    double opcodeY = introToInstructionNumbers.Y + 50*(index%10);
    TextBox textBox = new()
    {
        X = opcodeX,
        Y = opcodeY,
        Content = name + " : " + value.ToString()
    };
    opcodeNumberTextboxes.Add(textBox);
}
TextBox pongLevelTriviaTextBox = new()
{
    X = introToInstructionNumbers.X + 300 + 260*7,
    Y = introToInstructionNumbers.Y + 50,
    Content = Text.pongLevelTrivia,
};

IList<Entity> entities = processor.Entities
    .Concat(counter.Entities)
    .Append(victory)
    .Concat(instructionDescriptionTextBoxes)
    .Concat(opcodeNumberTextboxes)
    .Concat(new Entity[] 
    {
        floor, 
        shapeLimitDisabler, 
        interactionTutorialTextBox,
        addressingModesTextBox,
        introToInstructionNumbers,
        stackDescriptionTextBox,
        pongLevelTriviaTextBox,
        everyFrame,
        tinyFallingSquare,
    })
    .ToList();
LevelCruncher.OptimizeTriggers(entities);
Level level = new(entities
.ToArray())
{
    BackgroundColor = 0x888888,
    Y = 5000
};
File.WriteAllText(output, LevelCruncher.MinifyRelayTriggers(level.ToXML()));
Console.WriteLine($"wrote {output}");

// The texts written in the level
static class Text
{
    public const string interactionTutorial =
    @"Welcome to the processor level! Here's how to use it:

    Clicking Controls: 
    Click on memory and registers to set their bits.
    Registers are the 12x6 grid below INS,
    and memory is the huge grid to the right.
    The last 4 registers are just single bits, and are wired to a controls machine.
    Bits are displayed as green squares if set (1) and red squares if not set (0).
    To clear a row or register, click above it.
    To clear SRC, TRG, TRGVAL, PC, or INS, click to the right of it.

    Register Layout: 
    Memory and registers go from top to bottom, most significant bit first. 
    Other registers go from left to right, with the most significant bit on the left.

    Navigation Buttons:
        FETCH: Moves the memory value pointed to by PC to the instruction register.
        EXEC: Executes the instruction in the instruction register.
        INC: Adds one to the PC register.
        STEP: Does FETCH, EXEC, and INC. If 'CNT' is on, it loops.
        SHOWPC: Sets the last read memory address to PC.

    Instruction Reference: Check the list for what each instruction number does.
    Input the binary representation of an instruction number into the memory address that PC is on and press step,
    or input that binary representation into INS directly and press exec.
    (the list is over to the right)";
    public const string addressingModesOverview =
	@"Each operand of an instruction can have its own addressing mode,
	but not all addressing modes are available for every instruction.
	Here are the possible addressing modes:
	Const: The value in the operand is exactly what is used. Only valid for source operands.
	Addr: The operand refers to an address in memory
	AddrIndirect: The operand refers to the address stored in memory pointed to by another address.
	Register: The operand refers to a register.
	RegisterIndirect: The operand refers to the address in memory held by the register the operand is referring to.";
	public const string nopOverview =
	@"The No-Operation instruction does nothing. 
    It has no operands.";
	public const string moveOverview =
	@"The move instruction copies the value of the first operand into the
	second operand, overwriting the original value in the second operand.";
	public const string addOverview =
	@"The add instruction adds the value of the first operand 
    to the value already stored in the second operand.
    It also sets the carry flag if the result overflowed,
    and sets the zero flag if the result is exactly zero.";
	public const string incrementOverview =
	@"The increment instruction adds one to the only operand";
	public const string subtractOverview =
	@"The subtract instruction subtracts the value of the first operand
    to the value already stored in the second operand. 
	It also sets the zero flag if the two values were the same, 
    and sets the carry flag if the first value is more than the second value";
	public const string compareOverview =
	@"The compare instruction sets the same flags that the
    subtract instruction does, but does not modify the second operand.";
	public const string jumpOverview =
	@"The jump instruction jumps to the location specified by its one operand";
	public const string conditionalJumpsOverview =
	@"Conditional jump instructions jump to the location specified its one operand,
	based on the result of the previous compare; for example, cmp A B followed by jge C,
	will only jump if A is greater than or equal to B.
    More specifically, it jumps based on the flags CF and ZF (carry flag and zero flag).
    jg: CF
    jle: NOT CF
    jz: ZF
    jnz: NOT ZF
    jge: CF OR ZF
    jl: (NOT CF) AND (NOT ZF)";
	public const string multiplyOverview =
	@"The multiply instruction multiplies the first operand
    with the second operand, and stores the result in the second operand";
	public const string stopSteppingOverview =
	@"If you set the switch labeled 'CNT', 
    when you step an instruction it will continue stepping,
	until it reaches a stopStepping instruction.";
	public const string resumeSteppingOverview =
	@"The resumeStepping instruction resumes the processor after waiting for a frame.
	Put this right before a stopStepping instruction
	and jump to 0 instruction to rerun the program every frame";
	public const string stackOverview =
	@"The first register (register 0) is hard coded to be the stack pointer, 'sp'.
	If you want to use push and pop opcodes, 
    make sure to set sp to an appropriate location first;
	It's suggested to initialize it with 2^n-1, the last address of memory.";
	public const string pushOverview =
	@"The push instruction pushes its one operand to the stack.
	Specifically, it moves the source operand into the address specified by sp,
	and then decrements sp.";
	public const string popOverview =
	@"The pop instruction pops a value from the stack into its one operand.
	Specifically, it increments sp,
	and then moves the value in the address specified by sp into the operand.";
	public const string playSoundOverview =
	@"The playSound instruction plays a wheelchair guy grunt noise.
    It has no operands.";
	public const string drawOverview =
	@"The draw instruction draws a pixel on the monitor,
    the pixel being specified by its one operand.
	The pixels are ordered left to right, and then top to bottom.";
	public const string clearScreenOverview =
	@"The clear screen instruction clears
    all of the pixels on the screen. It has no operands";
	public const string auxillaryOverview =
	@"The auxillary instructions are used to interface with something outside of the processor.
	In the pong level this was used to increment the score, which is held in a separate unary counter.
	They have no operands.
    Just as a treat for speedrunners, executing auxillaryTwo 32 times in a single frame wins you the level.";
    public const string pongLevelTrivia =
    @"
    Whereas this level only has 64 rows of 6-bit wide memory, 
    the pong level has around 500 rows of 9-bit wide memory.
    The reason this level isn't that large is because of two reasons:
    I have a display panel for every single bit in the memory and registers in this demonstration, 
    which makes the level larger and laggier.
    The entire memory in the pong level is ROM,
    making it so I can use one trigger per 9 bit memory row.
    But I can't just have the player generate the level to play around with various instructions,
    I need to have the memory in this level be read/write and clickable, 
    which means (3*bits+1) times as many triggers per row";
}
