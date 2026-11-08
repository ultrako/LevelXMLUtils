using LevelXML;
namespace LevelXMLUtils.Controls;

// NassingerControls placed far from the player, so the noise of their jets (which are how Space, Shift and Ctrl are detected) is never heard, with
// the player in a seat where he spawns: a PlayerHolder, or a seat vehicle the level brings. The game sends the keys to the vehicle the player grabbed (the seat), and each vehicle passes
// them on to every vehicle in its own list, which the game fills when the level loads from the joints joining two vehicles (createJoints:
// checkAddVehicle, both ways) and never empties. So a long pin from the seat to the controls links them at load, and the start trigger deletes the
// pin at once: the link stays, and nothing is left to drag the two together (both are pinned to the world by their own joints). The pin is not
// vehicle-controlled, so neither vehicle keeps it in its motor list. The jets' sound comes from their bodies and the game stops it beyond 16 by 8
// physics units (1,000 by 500 level units at its scale of 62.5) from the middle of the screen; the controls sit AwayX to the right.
public class SeatedControls : TriggerContraption
{
	public const double AwayX = 3000;
	public NassingerControls Controls;
	// The PlayerHolder, when the seat is one; SeatHandle is the vehicle the player grabs either way
	public PlayerHolder? Seat;
	public Vehicle SeatHandle;
	public PinJoint Link;
	// Fires once, when something touches it at the spawn as the level loads; it deletes the link. A level can connect more to it.
	public ActivateTrigger Start;

	// seat: a vehicle the level builds and adds itself (only linked here); without one, a PlayerHolder at the spawn
	public SeatedControls(double x, double y, double? turnRepeatSeconds = null, double? moveRepeatSeconds = null, ControlsTuning? tuning = null, Vehicle? seat = null,
		IReadOnlyList<int>? turnChoices = null, IReadOnlyList<int>? moveChoices = null, int turnDefault = 0, int moveDefault = 0, double awayX = AwayX)
	{
		// awayX: where the controls go from the seat (negative near the map's right edge, where +AwayX would be off the map)
		Controls = new NassingerControls(x + awayX, y, turnRepeatSeconds, moveRepeatSeconds, tuning, turnChoices, moveChoices, turnDefault, moveDefault);
		IEnumerable<Entity> seatEntities = Enumerable.Empty<Entity>();
		if (seat is null)
		{
			Seat = new PlayerHolder(x, y);
			Seat.Entities.OfType<Vehicle>().First().Opacity = 0;
			seat = Seat.Handle;
			seatEntities = Seat.Entities;
		}
		SeatHandle = seat;
		Link = new PinJoint(SeatHandle, Controls.Handle) { X = x, Y = y, VehicleControlled = false };
		Start = new ActivateTrigger { X = x, Y = y, Width = 60, Height = 60, TriggeredBy = TriggeredBy.Any, RepeatType = RepeatType.Once };
		Start.AddTarget(new Target<Joint>(Link, new DeleteSelfJoint()));
		Entities = Controls.Entities.Concat(seatEntities).Append(Link).Append(Start).ToList();
		this.x = x; this.y = y;
	}
	readonly double x, y;

	// For a level that needs no character once the player holds the controls: a shape of NaN density over the seat,
	// not fixed (it stays where it is without a floor), colliding with nothing until the controls' Space fires (which means the player has
	// grabbed the seat: until then the keys don't reach the controls), when it starts colliding with characters only. That black-holes the
	// character, sends the camera to the top left, and black-holes anything jointed to him (the seat; the controls were unlinked from it at
	// the start, and the seat's pin to the world is deleted just before), and the keys still reach the controls: the vehicles stay linked.
	// The black hole spreads through every joint, joints to the world included: with the seat still pinned to the world it would take the controls
	// (pinned to the world too) and the keys would die, hence deleting the seat's pins first. Afterwards the controls still work.
	// Returns the shape, which the level adds.
	// seatPins: the pins to the world of a seat the level brings, deleted with the PlayerHolder's
	// size: the hole's width and height; at: its centre (default the spawn). It has to reach the character's own parts: with the irresponsible dad in a
	// seat of his own, an 80 by 120 hole only black-holed itself, and 300 by 300 caught him
	public Entity BlackHoleOnGrab(IEnumerable<Joint>? seatPins = null, (double w, double h)? size = null, (double x, double y)? at = null)
	{
		Rectangle hole = new() { X = at?.x ?? x, Y = at?.y ?? y, Width = size?.w ?? 80, Height = size?.h ?? 120, Fixed = false, Density = double.NaN, Collision = Collision.Nothing, Opacity = 0, FillColor = 0 };
		// The seat he holds is black-holed with him, and the black hole spreads through its pin to the world to everything else pinned to the
		// world (the controls among them: found in the game, where all but a few bodies went NaN), so the pin is deleted first
		Input onGrab = new();
		if (Seat is not null) { foreach (Joint pin in Seat.Entities.OfType<Joint>()) { onGrab.Add(new Target<Joint>(pin, new DeleteSelfJoint())); } }
		foreach (Joint pin in seatPins ?? Enumerable.Empty<Joint>()) { onGrab.Add(new Target<Joint>(pin, new DeleteSelfJoint())); }
		onGrab.Add(new Target<Shape>(hole, new ChangeCollision<Shape>(Collision.OnlyCharacters)));
		Controls.SpaceOn.Connect(onGrab);
		return hole;
	}
}
