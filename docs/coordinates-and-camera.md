# Coordinates and the camera

## The map, the walls and the world

Read from the game's script (2026-11-07):

- **The map** is 20,000 by 10,000, from (0, 0). The camera stays inside it.
- **Walls** stand just outside it: on the left (x -250 to 0) and the right (x 20,000 to 20,250), from y -5,250 to
  10,250, and a roof from y -5,250 to -5,000, 5,000 above the map's top. There is no wall below the map.
- **The physics world** is the box those walls enclose plus the space below the map down to y 10,250: x from -250 to
  20,250, y from -5,250 to 10,250. A moving body that leaves it is frozen by the physics engine: it stops where it is
  and no longer moves or collides. The game doesn't handle that, so the body stays there, out of the camera's reach,
  which looks as if it was deleted. Keep everything that moves inside the map.

What happens to something placed outside the map depends on whether it collides with the walls:

- A moving shape that collides with the walls and starts overlapping one is pushed out of it as the level starts, so it
  ends up somewhere other than where you put it. Collisions that collide with the walls: Everything, Everything But
  Characters and Everything But Same.
- A moving shape set to Nothing, Only Fixed, Only Fixed And Same or Only Characters doesn't collide with the walls and
  stays where it was placed.
- Fixed shapes are never pushed.
- The character collides with the walls, so a spawn point outside the map pushes him (and can tear him apart) before
  the level starts.

So keep a level's shapes and its spawn inside the map. A level designed around (0, 0) has to be shifted into the map
before it is written: add an offset to every top-level entity once (a group's children are relative to it and move
with it), and to the spawn. Check the smallest coordinate in the exported XML after anything that moves things around.

## The window and the camera

- The game window is 900 by 500.
- The camera follows the character's heart, keeping it in a dead zone of the screen (x 350 to 450 and y 300 to 350
  at rest, shifted while he moves), and it doesn't go past the level's top left.
- So a display that should fill the window is a 900 by 500 area at the top left of the level, (0, 0) to (900, 500),
  with the character somewhere near that corner (x under 450, y under 250) where the camera stays put. A character who
  falls or moves before coming to rest can leave the view scrolled.
- A black-holed character leaves the camera at the top left for good (`SeatedControls.BlackHoleOnGrab` does it once he
  holds the keyboard controls, and the keys keep working).

## Drawing order and culling

- Entities are drawn in their order in the level: put whatever should be on top later.
- Anything that has to be drawn over the character has to be a foreground group.
- Every frame, the game sets each foreground, character-layer and background object's visibility from whether it
  overlaps the camera, undoing an opacity-0 change's hiding. What that costs is in [frame-rate.md](frame-rate.md).
