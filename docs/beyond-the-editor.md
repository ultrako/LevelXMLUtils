# Beyond the editor

A level written as XML can hold values the level editor would never let you set. The game loads most of them.

## Values the editor won't set

- **No shape limit:** a chain with a link count of `NaN` lifts the editor's limit of about 900 shapes
  (`new Chain { LinkCount = double.NaN }`). See [limits.md](limits.md).
- **A trigger that repeats every frame:** a repeat interval of `NaN`. The editor clamps anything under 0.1 s up to
  0.1 s. See [triggers.md](triggers.md#repeating-triggers).
- **Positions outside the map:** the editor keeps everything inside the 20,000 by 10,000 map; a level's XML can place
  things outside it, where the world's walls and edges decide what happens to them. See
  [coordinates-and-camera.md](coordinates-and-camera.md#the-map-the-walls-and-the-world).
- **Concave polygons:** the editor makes every polygon convex as you edit it; a level's XML can hold a concave one.
  The physics engine assumes polygons are convex, so a concave one pushes a body out of itself by moving it rather
  than bouncing it.
- **Long text:** the editor limits how many characters a text box holds; a level's XML (and the editor's import box)
  doesn't.
- **Font sizes over 100:** the editor's field stops at 100, but a text box keeps whatever size the XML gives it. A
  font size of about 123 rendered (enormous) in the game.

Some limits hold even in a level's XML, because the game loads those values through the same setters the editor
uses: a trigger's width and height (5 to 5,000), its delay (0 to 30 seconds) and repeat interval (0.1 to 30 seconds),
a sound trigger's volume (0 to 1), and a vehicle's acceleration (1 to 10) and leaning strength (0 to 10). Only `NaN`
gets past those checks, since every comparison with it is false: that is how a `NaN` interval repeats every frame.

## Joints and their trigger actions

Read from the editor's and the game's code, and played in a fixture level (2026-11-08). A joint in a level's XML is
loaded through the same property setters the editor uses, so most of its values are clamped as the editor would clamp
them. A trigger action's values are not: they go to the physics engine as written, even where the editor's fields for
them have limits.

- **Negative motor torque or force:** the editor's fields only take digits, and the joint only caps the value at
  100,000, so a negative torque or force in the XML loads as it is. The motor then pushes at full strength one way
  all the time, whatever its speed is set to (Box2D clamps a motor's push between minus and plus its maximum, which
  a negative maximum turns inside out). Played: pin joints with a torque of -1,000 spun clockwise, all at the same
  speed, whether their speed was +3, -3 or 0 (a torque of +1,000 at speed +3 turned anticlockwise at about 3 radians a
  second); sliding joints at angle 0 with a force of -1,000 jumped left to their limit at once, at speed +3 or -3.
- **What a joint starts with** is clamped: the upper limit to 0 up to the maximum (180 degrees on a pin joint, 8,000
  on a sliding joint), the lower limit to the maximum's negative up to 0, and the motor speed to 20 (pin joints) or
  50 (sliding joints) either way.
- **Change limits**, the trigger action, isn't clamped: it can set an upper limit below 0, a lower limit above 0, or
  both on one side of the joint's start, which the editor's fields (for a sliding joint, -3,000 to 3,000) don't allow.
  Played: changing a pin joint's limits from -10 to 10 degrees to 30 to 60 moved its bar a little, and it stopped
  there.
- **Change motor speed** isn't clamped either: the target speed can be past the editor's 20 (pin joints) or 50 (sliding
  joints). Played: a speed of 60 on a pin joint spun its bar far faster than the editor allows.

## Precision

The editor's fields round what you type: x and y to 2 decimals, a shape's width and height to whole numbers, motor
torque and force to whole numbers, and a trigger's delay and repeat interval to what fits in 4 characters (so 0.04 s,
but nothing finer). A level's XML can hold finer values, and the game reads them as written, with one exception: a
shape's rotation is rounded to whole degrees however it is set. Keep the frame rounding of delays in mind: see
[triggers.md](triggers.md#delays).

Impulses are the most useful case. The editor's impulse fields take whole numbers from -50 to 50, but the apply impulse
action uses whatever its XML says, so an impulse can be a fraction. Gravity changes a body's velocity by 10/30 each
step, so an impulse of -1/3 (`-0.3333...`) every frame, from a trigger repeating every frame, cancels it: anti-gravity.

