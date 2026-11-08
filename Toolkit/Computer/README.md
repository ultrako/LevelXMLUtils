# The processor

Notes on `Processor` (in `Toolkit/Computer`), its memory (`MixedMemory` and the row classes, in `Toolkit/Memory`) and
`Assembler`, from measuring it in the trigger simulator (2026-10-24).

## How it works

- The word and the address are the same width (`bits`). Memory has `2^bits` rows unless `numMemoryRows` says fewer.
  The registers are a separate grid of 13 rows: 9 general registers and 4 one-bit rows wired to the keyboard controls
  (Shift, Ctrl, Up and Down).
- An instruction is an opcode word and up to two operand words, source then destination. A step fetches the opcode,
  fires the operation the decoder left enabled, and the operation fetches its operands. The 50 opcodes are in the CISC
  style: memory-to-memory moves and arithmetic, indirect modes, push and pop through register 0, a multiply by a
  constant, jumps on the carry and zero flags, drawing on a small screen, sound, and two `Auxillary` outputs for the
  level to use.
- `Start` runs steps back to back in the same frame (a step fires the next one) until `StopStepping`, or until
  `ResumeSteppingAfterFrame` waits `stepDelay` (default 0.1 s, 3 frames). Each step run in one frame nests deeper on the
  browser's call stack (see [triggers.md](../../docs/triggers.md#how-deep-activations-can-nest)), but the action budget below
  stops a frame long before that.

## Cost, in actions an instruction

Memory selects a row through blocks: one block and one place in the block are current, and an access switches only
those. (It used to disable every row that didn't match the address on every access, which was 87% of an instruction's
cost at 9 bits.)

| | 9 bits, 512 rows | 12 bits | 16 bits |
| --- | --- | --- | --- |
| fetch only | 1,024 | 1,123 | 1,255 |
| add register to register | 2,964 | 3,300 | 3,748 |
| load (address to register) | 3,498 | 3,975 | 4,611 |
| store (register to register-indirect) | 3,580 | 4,006 | 4,574 |
| multiply by constant | 3,424 | 3,850 | 4,446 |

(12 and 16 bits with 512 rows.) A frame can spend roughly 60,000 to 80,000 cheap actions on top of a normal frame, so
a 9-bit program runs about 20 instructions a frame, 600 a second.

## Size

- **RAM:** 2 triggers per bit and about 4 per row, plus the decoder (a few hundred). A third trigger per bit makes
  memory clickable, only with `visualizedMemory`. Full memory: 9 bits 12,729 triggers, 11 bits 55,249 (close to the
  ~90,000 at which a level crashes on load), 12 bits 117,000 (too many). 11 bits with 2,048 rows of RAM came to 5.9 MB
  of XML, 408 KB at zlib 9 (326 KB with Zopfli).
- **ROM:** nearly free in triggers. The optimizer folds each ROM cell into its row's read relay, so a word is about one
  trigger plus a target per 1 bit: 4,096 random 16-bit words came to 5,872 triggers for the whole processor, 1.1 MB, 74
  KB at zlib 9. ROM is limited by file size.

## Could it be RISC-V (RV32I)?

Estimated from the measurements above; nothing of this is built.

- **Memory.** 32-bit RAM rows are about 68 triggers each, so under the ~90,000 trigger limit there is room for about
  1,100 to 1,200 words (4.5 KB) of RAM next to the core and 32 registers. ROM at about 30 to 35 bytes compressed per
  32-bit word allows roughly 25,000 words (100 KB) of code and constants under the 900,000-byte save cap. RAM is the
  binding limit. `MixedMemory` checks every address bit, so an address past the last row reads and writes nothing:
  32-bit addresses work as they are, and only the rows that exist cost anything.
- **Speed.** Instruction cost grows slowly with width; 32 bits would probably cost 5,000 to 8,000 actions an
  instruction, about 10 a frame, 300 to 400 a second.
- **What's missing:** bitwise AND, OR and XOR; shifts by a register amount, left, right and arithmetic (only shift left
  by one exists); signed comparisons; byte and halfword loads and stores with sign extension (memory is
  word-addressed); LUI, AUIPC, JAL and JALR with a link register; x0 always zero; 32 registers; and decoding fixed
  32-bit instructions by field. The instruction decoder (`Addressor`) enumerates all `2^bits` opcode values, which
  can't work at 32 bits: decode the opcode, funct3 and funct7 fields instead (about 11 bits). RV32I is load/store, so
  most of today's memory-to-memory and indirect opcodes would go: the core would get simpler, not bigger.
- **Toolchains.** A cross compiler works the usual way (`riscv64-unknown-elf-gcc -march=rv32i -mabi=ilp32`,
  freestanding, linked for a few KB of RAM, the binary loaded with `SetInitialMemory`). Running a compiler on the
  processor itself is out of reach. Something tiny (a Forth, a monitor) would fit in ROM and 4 KB of RAM, but input is
  only the keyboard controls and click triggers, and the screen is small.
