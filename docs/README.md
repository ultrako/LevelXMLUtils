# How the game works, for level builders

What was learned building big trigger levels for Happy Wheels: measured in the game, or read from its script, or both.

The game:

- [Triggers](triggers.md): the order targets run in, a target listed twice, how deep activations can nest, what waits
  for the next tick, delays and repeating triggers.
- [Limits](limits.md): physics proxies, the pair table, how many triggers and shapes, the level editor's limits.
- [Saving and publishing](saving-and-publishing.md): the 900,000-byte save cap, compressing under it, names,
  publishing, the request quota.
- [Coordinates and the camera](coordinates-and-camera.md): the map, its walls and the physics world, the window, what
  the camera shows.
- [Frame rate](frame-rate.md): what an action, an opacity change and a display object cost.
- [Physics and characters](physics-and-characters.md): units, joints, concave polygons, grabbing.
- [Beyond the editor](beyond-the-editor.md): values only a level's XML can set, joints, and precision.
- [Sound](sound.md): sound ids, sound triggers, how placed sounds fade with the camera.

The bigger devices in the toolkit:

- [The processor](../Toolkit/Computer/README.md): how it works, what an instruction costs, how big memory gets.
- [CHIP-8](../Toolkit/Computer/Chip8/README.md): how a program is compiled into triggers, what it costs, what the
  archive's games need.
- [Colour-code lock](../Toolkit/Devices/color-lock.md): gating a level behind a code.
- [First-person raycaster](../Toolkit/README.md#first-person-raycaster-raycasting): a 2.5D view of a grid map, walking,
  sprites. `Examples/RaycastDemo` builds a level with it.
- [Video and sound](../Toolkit/README.md#video-and-sound-playback): a video on a screen of triggers, with a song made of
  the game's sounds. `Examples/VideoDemo` builds Bad Apple!! with it.
