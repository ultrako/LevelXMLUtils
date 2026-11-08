using LevelXMLUtils.Memory;
using LevelXMLUtils.Devices;
using LevelXMLUtils.Controls;
namespace LevelXMLUtils.Computer;

public class Assembler
{
    public class InstructionComponent {}
	public class Instruction : InstructionComponent
	{
		public Processor.Opcode Opcode;
        public string? Label;
		public Instruction(uint opcode, string? label=null) 
        {
            this.Opcode = opcode;
            this.Label = label;
        }
	}
	public class Operand : InstructionComponent
	{
		public uint Value;
		public Operand(uint value) {this.Value = value;}
	}
    public class JumpOperandPlaceholder : Operand
    {
        public JumpOperandPlaceholder() : base(0) {}
    }
	public class JumpInstruction : Instruction
	{
		public string LabelToJump;
		public JumpInstruction(uint opcode, string label) :
		base(opcode)
		{this.LabelToJump = label;}
	}

    public class InstructionWord
    {
        public Instruction Instruction;
        public Operand? FirstOperand;
        public Operand? SecondOperand;
        public InstructionWord(Processor.Opcode opcode, uint? firstOperand=null, uint? secondOperand=null, string? label=null)
        {
            Instruction = new(opcode, label);
            if (firstOperand is uint first)
            {
                FirstOperand = new(first);
            }
            if (secondOperand is uint second)
            {
                SecondOperand = new(second);
            }
        }
        /// <summary>
        /// Use this constructor if you want a jump to a label
        /// </summary>
        /// <param name="opcode"></param>
        /// <param name="jumpLabel"></param>
        public InstructionWord(Processor.Opcode opcode, string jumpLabel)
        {
            Instruction = new JumpInstruction(opcode, jumpLabel);
            FirstOperand = new JumpOperandPlaceholder();
        }
        internal IList<InstructionComponent> ConvertToProgramMembers()
        {
            List<InstructionComponent> members = new()
            {
                Instruction
            };
            if (FirstOperand is Operand first)
            {
                members.Add(first);
            }
            if (SecondOperand is Operand second)
            {
                members.Add(second);
            }
            return members;
        }
    }

    static public IEnumerable<uint> Assemble(IList<InstructionComponent> program)
    {
        IList<uint> memoryValues = program
        .Select<InstructionComponent, uint>(programMember => programMember switch
        {
            Instruction instruction => instruction.Opcode,
            Operand operand => operand.Value,
            _ => 0, 
        }).ToList();
        foreach ((InstructionComponent programMember, int index) in program.Zip(Enumerable.Range(0,program.Count)))
        {
            if (programMember is JumpInstruction jump)
            {
                string label = jump.LabelToJump;
                var memberIndexEnumerable = program
                .Select((member, index) => new {member, index})
                .Where(tuple => tuple.member is Instruction instruction && instruction.Label == label);
                var memberIndexTuple = memberIndexEnumerable.FirstOrDefault();
                if (memberIndexTuple is null)
                {
                    throw new KeyNotFoundException("No instruction with label " + label + ".");
                }
                uint jumpLocation = (uint)memberIndexTuple.index;
                memoryValues[index+1] = jumpLocation;
            }
        }
        return memoryValues;
    }

    static public IEnumerable<uint> Assemble(IList<InstructionWord> words)
    {
        IList<InstructionComponent> components = words.SelectMany(word => word.ConvertToProgramMembers()).ToList();
        return Assemble(components);
    }

    static public Func<int, int?> DontMapUnusedOpcodes(IList<InstructionWord> words)
    {
        IList<Processor.Opcode> opcodesUsed = words
            .Select(word => word.Instruction.Opcode)
            .Distinct()
            .ToList()
            ;
        int? func(int originalOpcode) 
        {
            if(opcodesUsed.Any(usedOpcode => usedOpcode == originalOpcode))
            {
                return originalOpcode;
            }
            return null;
        }
        return func;
    }

    static public Func<int, int?> MapMostUsedOpcodesToLowestNumbers(IList<InstructionWord> words)
    {
        IList<Processor.Opcode> opcodeByFrequencies = words
            .Select(word => word.Instruction.Opcode)
            .GroupBy(opcode => opcode)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .ToList()
            ;
        int? func(int originalOpcode) 
        {
            int index = opcodeByFrequencies.IndexOf
            (
                opcodeByFrequencies.FirstOrDefault
                (
                    opcode => ((int)(uint)opcode).Equals(originalOpcode)
                )
            );
            if (index >= 0)
            {
                return index;
            }
            return null;
        }
        return func;
    }

    static private int CountSetBits(uint n)
    {
        int count = 0;
        while (n != 0)
        {
            n &= (n - 1); // Clear the least significant bit set
            count++;
        }
        return count;
    }

    static public Func<int, int?> MapMostUsedOpcodesToLowestBitCountNumbers(IList<InstructionWord> words, int bits)
    {
        IList<Processor.Opcode> opcodeByFrequencies = words
            .Select(word => word.Instruction.Opcode)
            .GroupBy(opcode => opcode)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .ToList()
            ;
        // foreach (Processor.Opcode opcode in opcodeByFrequencies)
        // {
        //     Console.WriteLine(opcode);
        // }
        IList<uint> uintsOrderedByBitCount = Enumerable.Range(0, (int)Math.Pow(2, bits))
            .Select(i => (uint)i)
            .OrderBy(i => CountSetBits(i))
            .ToList();
        // foreach(uint i in uintsOrderedByBitCount)
        // {
        //     Console.WriteLine(i);
        // }
        int? func(int originalOpcode) 
        {
            int index = opcodeByFrequencies.IndexOf
            (
                opcodeByFrequencies.FirstOrDefault
                (
                    opcode => ((int)(uint)opcode).Equals(originalOpcode)
                )
            );
            if (index >= 0)
            {
                return (int)uintsOrderedByBitCount[index];
            }
            return null;
        }
        return func;
    }

    static public void ApplyMapping(IList<InstructionWord> words, Func<int, int?> mapping)
    {
        foreach (InstructionWord word in words)
        {
            Processor.Opcode opcode = word.Instruction.Opcode;
            int? mapped = mapping((int)(uint)opcode);
            if (mapped is int mappedCode)
            {
                Processor.Opcode newOpcode = new((uint)mappedCode);
                word.Instruction.Opcode = newOpcode;
            }
            else
            {
                throw new ArgumentException("Mapping does not map an instruction that the list of instruction words uses!");
            }
        }
    }
}