using LevelXML;
using LevelXMLUtils.Computer;
using LevelXMLUtils.Memory;
using Xunit;
namespace LevelXMLUtils.Tests;

// The processor and its multiplier, run in the trigger simulator.
public class ProcessorTests
{
    [Fact]
    public void Multiplier_ProducesProductOfConfiguredOperands()
    {
        var multiplier = new Multiplier(4, 0, 0, 10, 10, visualized: false);

        var aValues = new[] { 1, 1, 0, 0 };
        var bValues = new[] { 1, 0, 1, 0 };

        var triggerEntities = new List<ActivateTrigger>();
        var setAIds = new List<string>();
        foreach (var bit in Enumerable.Range(0, 4).Where(i => aValues[i] == 1))
        {
            var bitSetter = new Relay(0, 0, 10, 10);
            bitSetter.Output.Connect(multiplier.SetAs[bit]);
            triggerEntities.AddRange(bitSetter.Entities.OfType<ActivateTrigger>());
            setAIds.Add(TriggerGraph.IdFor(bitSetter.Output.Single()));
        }

        var setBIds = new List<string>();
        foreach (var bit in Enumerable.Range(0, 4).Where(i => bValues[i] == 1))
        {
            var bitSetter = new Relay(0, 0, 10, 10);
            bitSetter.Output.Connect(multiplier.SetBs[bit]);
            triggerEntities.AddRange(bitSetter.Entities.OfType<ActivateTrigger>());
            setBIds.Add(TriggerGraph.IdFor(bitSetter.Output.Single()));
        }

        var power = new Relay(0, 0, 10, 10);
        power.Output.Connect(multiplier.PowerLast);
        triggerEntities.AddRange(power.Entities.OfType<ActivateTrigger>());
        var powerId = TriggerGraph.IdFor(power.Output.Single());

        var graph = TriggerGraph.FromTriggers(
            multiplier.Entities.OfType<ActivateTrigger>()
                .Concat(triggerEntities));

        graph.Pulse(setAIds.Concat(setBIds).Append(powerId).ToArray());

        var resultIds = multiplier.Ons.SelectMany(output => output.Select(trigger => TriggerGraph.IdFor(trigger))).ToHashSet();
        Assert.Equal(4, graph.LastFired.Intersect(resultIds).Count());
        Assert.DoesNotContain(TriggerGraph.IdFor(multiplier.Overflow.Single()), graph.LastFired);
    }
    
    [Fact]
    public void Processor_ExecutesRealInstructionSequenceUntilStopInstruction()
    {
        var processor = new Processor(6, 0, 0, 10, 10, displayWidth: 8, displayHeight: 8, visualized: false, visualizedMemory: false, allMemoryIsROM: false);
        var start = new Relay(0, 0, 10, 10);
        start.Output.Connect(processor.Start);

        var program = new[]
        {
            new Assembler.InstructionWord(Processor.Opcode.MoveConstToRegister, 3, 0x0),
            new Assembler.InstructionWord(Processor.Opcode.MulConstWithRegister, 5, 0x0),
            new Assembler.InstructionWord(Processor.Opcode.StopStepping),
        };
        processor.SetInitialMemory(Assembler.Assemble(program));

        var graph = TriggerGraph.FromTriggers(
            processor.Entities.OfType<ActivateTrigger>()
                .Concat(start.Entities.OfType<ActivateTrigger>()));

        var startId = TriggerGraph.IdFor(start.Output.Single());
        // Start runs steps back to back in the same frame (each step fires the loop-back trigger, which fires the next step) until the
        // StopStepping instruction: the whole program runs in one pulse
        graph.Pulse(startId);
        Assert.Equal(15u, processor.Registers[0].ValueIn(graph));
        Assert.Empty(graph.Runaway);
    }

    [Fact]
    public void Processor_RunsALoopWithMemory_ToTheStopInstruction()
    {
        // r0 = 3 * 5 by a loop that counts r1 down from 5 (adding 63 is minus one in 6 bits), then 7 + r0 in memory row 40, read back into r2
        var processor = new Processor(6, 0, 0, 10, 10, displayWidth: 8, displayHeight: 8, visualized: false, visualizedMemory: false, allMemoryIsROM: false);
        var start = new Relay(0, 0, 10, 10);
        start.Output.Connect(processor.Start);
        var program = new[]
        {
            new Assembler.InstructionWord(Processor.Opcode.MoveConstToRegister, 0, 0x0),
            new Assembler.InstructionWord(Processor.Opcode.MoveConstToRegister, 5, 0x1),
            new Assembler.InstructionWord(Processor.Opcode.AddConstToRegister, 3, 0x0, "loop"),
            new Assembler.InstructionWord(Processor.Opcode.AddConstToRegister, 63, 0x1),
            new Assembler.InstructionWord(Processor.Opcode.CmpConstFromRegister, 0, 0x1),
            new Assembler.InstructionWord(Processor.Opcode.JumpIfNotZero, "loop"),
            new Assembler.InstructionWord(Processor.Opcode.MoveConstToAddr, 7, 40),
            new Assembler.InstructionWord(Processor.Opcode.AddRegisterToAddr, 0x0, 40),
            new Assembler.InstructionWord(Processor.Opcode.MoveAddrToRegister, 40, 0x2),
            new Assembler.InstructionWord(Processor.Opcode.StopStepping),
        };
        processor.SetInitialMemory(Assembler.Assemble(program));
        var graph = TriggerGraph.FromTriggers(processor.Entities.OfType<ActivateTrigger>().Concat(start.Entities.OfType<ActivateTrigger>()));

        graph.Pulse(TriggerGraph.IdFor(start.Output.Single()));
        Assert.Empty(graph.Runaway);
        Assert.Equal(15u, processor.Registers[0].ValueIn(graph));
        Assert.Equal(0u, processor.Registers[1].ValueIn(graph));
        Assert.Equal(22u, processor.Registers[2].ValueIn(graph));
        Assert.Equal(22u, processor.MemoryRows[40].ValueIn(graph));
    }

    [Fact]
    public void Processor_Visualized_IsSelfContained()
    {
        var processor = new Processor(6, 0, 0, 10, 10, displayWidth: 7, displayHeight: 5, visualized: true, visualizedMemory: true, allMemoryIsROM: false);
        DeviceTests.AssertSelfContained(processor);
    }
}
