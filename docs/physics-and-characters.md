# Physics and characters

## Units and impulses

- An impulse is a change of velocity, not weighted by mass. Gravity changes a body's velocity by 10 / 30 = 0.333 each
  step.
- The physics scale is 62.5 level units to the metre.

## Joints

- A sliding joint's motor speed is in metres a second, and a positive speed moves the body backwards along the joint's
  axis (left at angle 0, up at angle 90). A pin joint's speed is in radians a second.
- A joint between a group and the world measures the world relative to the group, so limits that move the group right
  are negative.
- A joint's `X` and `Y` are a point in the world, independent of either body: moving a body without moving its joint
  fights the solver.
- Revolute limits closer than 4 degrees apart (twice Box2D's angular slop) snap the joint back to its starting angle.
- Changing a joint's limits doesn't wake a sleeping body: add a tiny impulse.
- The game turns off the motors of joints controlled by a vehicle at load. A motor that should keep running must not be
  vehicle-controlled.
- A body moved by limits travels about 119 units a tick.

## Shapes

- Box2D assumes polygons are convex, and the game passes a polygon to it as one shape without splitting it. A concave
  polygon pushes a body out of itself by moving it (positional correction, at most 0.2 a step), not by giving it a
  speed, so it doesn't bounce it. That can be useful: a concave notch holds a paddle without the rebound a joint limit
  gives.

## Characters and vehicles

- A character only grabs, with Space, what his forearms touch, and attaches to the vehicle the touched shape belongs
  to. Every shape in a vehicle group is a handle unless marked otherwise.
- A hidden vehicle (`VehicleHidden`, `h="t"`) always builds a plain character: the irresponsible dad has no son then.
- The keys go to the vehicle the player grabbed, and on to every vehicle linked to it by a joint at load. The link
  stays after the joint is deleted, so a level can put the controls far from the player (`SeatedControls`).
