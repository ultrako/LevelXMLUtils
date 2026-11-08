# CHIP-8

`Chip8Machine` (in `Toolkit/Computer/Chip8`) compiles a CHIP-8 program into triggers, and `Chip8` is a plain C#
interpreter it is checked against. `Examples/Chip8Demo` builds a playable level from a `.ch8` file. These notes say
how the machine works, what it costs, and what was learned building it (2026-10). Trigger costs are in actions, which
all take about the same time in the game, plus opacity changes at about 25 times that (see
[frame-rate.md](../../../docs/frame-rate.md)).

## The reference interpreter

- `Chip8Quirks` holds the behaviours CHIP-8 interpreters disagree on. `Chip8Quirks.Vip` is the original COSMAC VIP's:
  shifts take VY, FX55 and FX65 move I, BNNN adds V0, 8XY1/2/3 reset VF, sprites clip at the edges (their start
  wraps), and DXYN waits for the next frame. `Modern` is the later CHIP-48 behaviour, and `Octo` what the CHIP-8
  Archive's games run on.
- It records what a run did: instructions, the addresses written (these need RAM; the rest can be ROM), read as data
  and executed, the deepest call, sprites drawn and pixels flipped. `Chip8Demo measure` prints them.
- `Chip8Tests` checks every instruction group on hand-assembled programs. It was also checked (2026-10-25) against
  Timendus's chip8-test-suite (github.com/Timendus/chip8-test-suite, GPL-3.0, so not included here): every mark of
  `3-corax+` and `4-flags` passes, and `5-quirks` reports all six VIP behaviours.

## How the machine works

The program is fixed when the level is built, so there is no fetch or decode in the level.

- **Nodes.** An abstract interpreter follows the program from 0x200, where everything is 0, through its jumps, calls
  and skips. Each instruction it reaches is a node: an address together with what is known about V0 to VF and I when
  it runs there. Most addresses get one node, whose context is what every way into it agrees on. Small counted loops
  and small routines without calls get a node per context (at most 40), which unrolls them. A node knows only what is
  live there (a backward liveness analysis), and a call's return node knows what the caller knew, less what the
  routine may change.
- **What knowing buys.** A skip, jump table or arithmetic whose outcome is known compiles to the one way on, or to
  setting the result. A load, store or draw whose I is known touches its memory rows directly, without decoding the
  address. A register write that nothing reads before it is written again is dropped.
- **Each node is a short trigger program** behind a one-hot "next" relay. An instruction's actions are split over as
  many relays as it takes for none to target one trigger twice (see [triggers.md](../../../docs/triggers.md#a-target-listed-more-than-once)).
- **Dispatch.** A frame clock (a permanent trigger with a `NaN` interval, so it fires every frame) runs `tickrate`
  instructions, a 60 Hz timer tick, `tickrate` more and another tick. Each step sweeps the "next" relays through trees
  (5 children a node, so finding the live one costs about the branching plus one or two a level). The tree keeps its
  live path enabled, so moving to the successor switches only the nodes the two don't share. The sweep goes from the
  highest address down, so an instruction can make a later successor next directly; an earlier one (a backward jump, a
  return) goes through a second "pending" tree, handed over after the sweep. Enabling "next" directly for an earlier
  address would run it again in the same sweep. A forward step costs about 30 actions, a backward one about 50.
- **The call stack** has no levels when that is exact: a call marks its site, and a return fires the marks of the
  sites it can go back to, exactly one of which is set. That needs no recursion. A levelled stack (a relay per site and
  level) is still there for programs that need it. A call costs about 53 actions and a return 57.
- **Registers** are 8-bit toggle chains. Copies, compares and arithmetic go straight from one register's bits to
  another's through gates opened only while the source is probed: a constant compare probes the register through a
  "witness" per bit, 8XY4 counts each 1 of VY into VX, and so on. A register's bits reach those gates through a hub
  split into groups, since V0 alone can feed about 35 of them.
- **Memory** is in banks: a `MixedMemory` per stretch of addresses that matter (the font, the program, RAM), split
  where more than 256 bytes in a row don't. An interval analysis of I says which bank an access can touch. RAM is put
  at every byte a store can write by that analysis, plus what `Chip8Machine.MeasureWrites` sees in a run of the
  reference interpreter with random keys, for the stores the analysis can't bound (`UnboundedStores` lists them).
  Measured RAM alone is only a lower bound: it missed a store that a random run never reaches (Danm8ku's wave 3) and
  broke that wave. Past the rows, memory reads 0, which is exact. The first byte an instruction reads decodes the
  address; the next ones step to the next row.
- **Decoders** (keys, the font, decimal digits, sprite x and y) work in two stages, the high half of the bits picking
  a group and the low half a member: about `4 * 2^(bits/2)` actions instead of `(bits/2 + 2) * 2^bits`.
- **The screen** is 64 by 32 pixels, each a toggle and a picture. Turning a pixel off sets the collision flag. Arming a
  sprite row enables its pixel gates, and a lit pixel finds its column and row through blocks of 8. 00E0 only touches
  lit pixels.
- **Persistence** (`persistence`, on by default): CHIP-8 moves a sprite by erasing it and drawing it again, so a frame
  that ends in between shows it missing, and moving things flash. With persistence, a picture shows while its pixel was
  lit at the end of the last frame or at any moment during this one. That also shows sprites lit for only part of a
  frame, which a 60 Hz interpreter shows and a 30 Hz level otherwise wouldn't. About 4,100 triggers more, and a few
  hundred actions a frame.
- **Without a character** (`foregroundScreen: false`), the screen is plain shapes instead of foreground groups: 11
  groups instead of 8,204. `SeatedControls.BlackHoleOnGrab` removes the character once he holds the controls.

## Random numbers

There is no random trigger. CXNN reads a counter that steps every game frame and every CXNN, through a fixed shuffle
of 0 to 255. A counter of frames alone gave every CXNN in a frame the same number, and consecutive frames counted up:
Danm8ku's waves came out as regular sweeps. With the counter alone, a run's numbers still depend only on the frame the
player started on, so `Chip8Demo` also steps it from the game's physics (`PhysicsRandom`): three balls in a closed box
far from the camera, batted by a motorised bar, with two sensors only the balls set off, stepping the counter by 1 and
by 8. Every press and release of a game key kicks a ball, so the numbers depend on the player's timing all through a
game. Measured in the game, the balls never settle and cross the sensors about twice a second. It costs 14 physics
proxies. Octo uses `Math.random`, so no level repeats a particular run of the archive's version; the tests give the
reference interpreter the same generator.

## Timing

- **Timer waits** (`Chip8Quirks.FuseTimerWaits`): Octo's wait for the delay timer (FX07, 3X00 and a jump back) spins
  for the rest of a frame half. Fused, reaching it with the timer running sets VX and skips the rest of the half,
  leaving the program counter where spinning would have. `FusedTimerWaits_LeaveTheSameStateAsSpinning` checks every
  frame of 3,600 that the interpreter's state is the same either way. The steps of a frame half go through relays of
  30, so the rest of a half spent waiting costs an action a group.
- **Game speed** (`gameSpeed`, 0.5 to 1): a frame skips its second tick on a fixed pattern (0.9 is one frame in five).
  The delay before a key takes effect is about a frame and a half at any speed (a frame until the key is read, and the
  controls' paddle reaches its sensor a step after it moves).
- **Adaptive skip** (`heavyBullets`, Danm8ku only): a frame whose first tick moved many bullets skips its second, so the
  game slows down at its busiest, as it does in Octo.

## Fused routines (Danm8ku)

`Chip8Quirks.FusedRoutines` runs a routine, found by its code at its entry, from the entry to one of its exits as one
instruction: in the reference interpreter by running its instructions in a loop, and in the machine natively
(`Chip8Native.cs`). It changes only timing: a game whose logic doesn't fit a frame runs faster than in Octo. Danm8ku's
bullet routines are the ones (`Chip8Quirks.Danm8kuFused`): `move_bullet`, `draw_bullet`, the two bullet loops, the
bullet spawn and the player update. Its bullet table became toggle rows (`ToggleMemoryRow`), so each bullet moves in
its own rows, with per-slot caches of what only a store can change (the position, the sprite, whether the slot is
live). `FusedRoutines_LeaveEveryGameTickTheSame` checks the fused program against the original at every game tick:
registers, all memory and the screen.

Two findings from it:

- Danm8ku relies on registers left over from the last bullet processed (a pattern that never sets vy, a spawn that
  uses a leftover va when no slot is free), so the fused loops leave exactly the same registers behind.
- A bullet whose acceleration takes vx to exactly 255 isn't saved by the original, and freezes. The game's own
  velocities never get there, but a spawn into a full table can write a record one byte off, so the native loop
  checks for it and undoes the move in place. The lockstep test `danm8ku-wave4-frozen` reaches it.

## What isn't compiled

FX18 (the sound timer), the display wait quirk, and FX0A without Octo's wait-for-release. BNNN only as Octo's jump
table idiom (a run of jumps at NNN, one per even V0). The constructor refuses a program that needs anything else.

## Costs

| Game | Instructions | Triggers | Actions a frame |
| --- | --- | --- | --- |
| Br8kout (tickrate 7) | 98 | about 16,300 | about 3,600, 8,800 at most |
| Super Pong (tickrate 30) | 238 | about 20,000 | about 19,700, 61,000 at most |
| Danm8ku (tickrate 1,000, fused) | 533 (1,597 nodes) | about 33,000 (before the toggle rows) | wave 6 about 59,000; the heaviest gameplay frames 50,000 to 60,000 at the 99th percentile |

Danm8ku's heaviest frame is the bullet table being cleared at the start of a game (about 430,000 actions), once.
Danm8ku is busy, not waiting: over 3,600 frames, about 3,000 run 1,000 or more of the 2,000 instructions allowed.
Played in the game, Br8kout and Super Pong behaved like the archive's versions in Octo.

## Testing

`Chip8MachineTests` runs the machine against the reference interpreter, as built and after
`LevelCruncher.OptimizeTriggers`: every register, the delay timer, the next instruction and every pixel after every
frame (1,200 frames of Br8kout with scripted keys, Super Pong, and Danm8ku from each wave), and one instruction at a
time, which names the first instruction that goes wrong. The full Danm8ku runs and the cost measurements take about
half an hour and only run with `SLOW_TESTS=1`. Two traps in such a test rig:

- Read a toggle's bit from its enabler (`On`): the optimizer removes the toggle trigger of a register that is only
  ever loaded.
- Give the rig a source that activates `Start`, or the optimizer removes the never-activated clock and, over several
  rounds, the whole machine.

## What the archive's games need

John Earnest's CHIP-8 Archive (github.com/JohnEarnest/chip8Archive) is all CC0. Its `programs.json` gives each
program's tickrate and Octo quirk flags, which map to `Chip8Quirks` as `ShiftUsesVY = !shiftQuirks`,
`LoadStoreIncrementsI = !loadStoreQuirks`, `JumpUsesV0 = !jumpQuirks`, `LogicResetsVF = logicQuirks`,
`ClipSprites = clipQuirks` and `DisplayWait = vBlankQuirks` (a missing flag is false). Its 50 plain CHIP-8 programs,
each run 60 seconds at its own tickrate with random keys:

- **RAM:** 34 write 11 bytes or fewer (25 none); Danm8ku 268, Mato8 411, Masquer8 1,193. Six modify their own code.
- **Speed:** tickrate 7 for 20 programs, 12 to 30 for 22, and 100 to 1,000 for the other 8.
- **Calls:** the deepest stack is 14 (the limit is 16). The most pixels flipped in a frame is about 1,500 (a screen
  clear).
- **Small ones to start with:** Br8kout (199 bytes, tickrate 7, no RAM), Ghost Escape (135 bytes, tickrate 7), Super
  Pong (595 bytes, tickrate 30, 3 bytes of RAM), Dino Run (520 bytes, tickrate 15, no RAM).
