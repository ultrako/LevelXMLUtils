# Limits

What stops a level from loading or running, and how close you can get. Saving and publishing have their own limits:
see [saving-and-publishing.md](saving-and-publishing.md).

## Physics proxies: 1,024

The physics engine is Box2D 2.0, which has room for 1,024 proxies (`b2_maxProxies`). A proxy is taken by:

- each shape in the physics: every interactive shape, and every part of the character and of vehicles. A fixed shape
  that isn't interactive is only drawn, and takes none;
- each trigger that isn't triggered by other triggers. Such a trigger creates a sensor at load and keeps it: starting
  disabled, or being disabled, doesn't free it. Only a fire-once trigger (`RepeatType.Once`) that has fired does.
- a trigger with no `b` attribute in the XML, which reads as triggered by the character. Relays must keep `b="5"`.

Running out fails in the level loader's `createTriggers` with "Cannot read properties of undefined (reading
'GetNext')" (or 'next'), or freezes on the first frame. In the game-mod build (`tools/game-mod`), the live count is
`window.__session.m_world.m_broadPhase.m_proxyCount`.

## The pair table: 16,384 overlapping boxes

Box2D keeps a pair for every two physics objects whose bounding boxes overlap, whether or not they collide. Many
bodies stacked on one point fill it and freeze the load: 300 invisible circles on one spot froze it, 100 were fine.
Give parked or pooled bodies their own places.

## Triggers: keep under about 90,000

The browser tab crashes (no JavaScript error) somewhere between about 96,000 and 126,000 triggers, measured in Chrome.
What counts is the number of triggers, not their targets: levels with over 200,000 targets in all loaded fine.

## Shapes: about 900, unless lifted

The editor stops at about 900 shapes. A chain with a link count of `NaN` lifts the limit: add
`new Chain { LinkCount = double.NaN }` to the level (see [beyond-the-editor.md](beyond-the-editor.md)).

## The level editor

The editor is the slowest way to load a level and the first to give up:

- It can't open very large levels: "Invalid string length" from `JSON.stringify` in its `buildLevel`. The level
  browser (and `tools/game-mod`'s local loader) loads them.
- Its import box gave up at 69,891 trigger targets, where the level browser's path loaded over 200,000.
- It refuses to test or save past 900 shapes or 10,000 art shapes; `tools/game-mod`'s `editor-limits` patch lifts
  that.

## The call stack

Triggers firing triggers nest on the browser's call stack, about 6,300 deep in Chrome. See
[triggers.md](triggers.md#how-deep-activations-can-nest).

## The world's size

The physics world goes from x -250 to 20,250 and y -5,250 to 10,250, around the 20,000 by 10,000 map; a moving body
that leaves it stops for good. See [coordinates-and-camera.md](coordinates-and-camera.md#the-map-the-walls-and-the-world).
