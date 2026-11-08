# Sound

## Sound ids

A sound trigger's sound is a number: its index in the game's sound list (`_sfxLookup` in the game's dependencies
script). There are 326.

## Sound triggers

- A sound trigger made with LevelXML's defaults plays once (`RepeatType.Once`) and is triggered by the character, so it
  also takes a physics proxy. For a sound that logic plays, use `RepeatType.Multiple` and `TriggeredBy.Triggers`.
- What one controls: the sound, its volume (0 to 1; anything outside that is ignored, so only stacked copies get
  louder), a delay in whole frames, and, for a sound not placed in the world (`l="1"`), a fixed pan. Nothing changes
  these while it plays, and a sound always plays to its end.

## Sounds placed in the world

A sound placed in the world (`l` other than 1) plays at the trigger's position, copied when it fires. Every frame its
volume is `v * min(1 - |dx| / 1000, 1 - |dy| / 500)` and its pan `dx / 1000`, where dx and dy are its distance from
the middle of the screen in level units. Past either cutoff (1,000 sideways, 500 up or down) it stops for good. So the
camera can fade, pan and end a playing sound, but only as a fade. Keep such a trigger's `x` and `y` in the XML: without
them its volume is `NaN`.

## Winning

A `VictoryTrigger` activated by another trigger gives the game's own win screen and sound.
