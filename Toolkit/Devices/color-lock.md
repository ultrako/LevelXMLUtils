# Colour-code lock

`ColorLock` (in `Toolkit/Devices`) gates a level behind a code of coloured button clicks, so a published level can be
played only by the people given the code. It keeps out casual players, not determined ones: anyone who opens the level
file can work the code out from the triggers.

## What the player sees

A row of four flat coloured squares (red, green, blue, yellow; `ColorLock.Colors`) and, above them, a row of small white
dots, one per click of the code. Each click lights the next dot, whether it was right or wrong. Once an attempt has as
many clicks as the code, it is judged:

- all right: `Unlocked` fires, and the buttons and dots are hidden and stop responding;
- anything wrong: the dots go out and a new attempt starts.

Nothing tells the player which click was wrong, so the code has to be guessed whole: with 4 colours and 6 clicks that's
4,096 codes. A version that reset on a wrong click straight away let the code be found a click at a time in about 20
tries.

## Using it

```csharp
ColorLock colorLock = new(ColorLock.Parse("green,blue,yellow,green,red,blue"), x, y);   // (x, y): the middle of the buttons
colorLock.Unlocked.Connect(whatTheLockOpens);   // e.g. the level's start, a door
entities.AddRange(colorLock.Entities);          // after whatever it should be drawn over
```

- Don't put the code in a public repo. Read it from an environment variable or an argument when you export the level,
  as `Examples/Chip8Demo` does (`lock=red,blue,...`).
- The buttons are click triggers, so they work with or without a character. Each one also creates a physics sensor: 4
  more physics proxies (see [limits.md](../../docs/limits.md)).
- Entities are drawn in order, so add the lock's after anything it should cover, and put it where the camera is before
  the player does anything (for a level whose camera stays at the top left, around (450, 250)).
- Whatever the lock opens should do nothing until then: connect the level's start to `Unlocked` instead of to the
  spawn.

## How it works

The state is how many clicks the current attempt has had, and whether all of them were right. There is a relay per
(click number, colour) for each of two tracks ("all right so far" and "already wrong"), and the four relays of the
current click number on the current track are enabled. Each button fires its colour's relay of every click number,
last first, so a click number that a click moves on to has already had its turn and can't be moved on again by the
same click.

A failed attempt starts over through a relay that each button fires after all of those. Otherwise re-enabling the
first click's relays would let that button reach them later in the same sweep, and the click that ended the failed
attempt would count as the next attempt's first.

It relies on two rules from [triggers.md](../../docs/triggers.md):

- One trigger can't both enable and fire another, so each relay disables its own click number's relays and enables the
  next one's, which are always different triggers.
- Opacity changes run in order on the next tick, so a click lights its dot before it unlocks or starts over, and hiding
  the dots comes after. The other way round, the last dot stayed lit after unlocking.

## Testing it

`Toolkit.Tests/ColorLockTests.cs` clicks the buttons by firing their click triggers in a `TriggerGraph`: the code
unlocks; a wrong click anywhere fails the attempt; a wrong click doesn't show before the attempt ends; the click that
ends a failed attempt doesn't start the next; and the buttons stop responding once unlocked. In the game, check that a
few wrong clicks light as many dots, that finishing a failed attempt clears them, and that the right code opens
whatever it guards and leaves nothing on screen.

To publish a locked level, see [saving-and-publishing.md](../../docs/saving-and-publishing.md).
