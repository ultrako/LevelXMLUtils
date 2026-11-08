using LevelXML;
namespace LevelXMLUtils.Devices;

// Random events from the game's physics: a few balls in a closed box (octagonal, so none can rest in a corner), batted around by a rotor
// that a motor keeps turning, with contact sensors that only the balls set off (TriggeredBy.Targets: the balls are the sensors' targets,
// with an impulse of nothing). Hits[k] fires each time a ball enters sensor k. Kick(k) gives the first ball an impulse in one of a few
// directions: connected to a player's key presses and releases, it makes the balls' paths (and so the hits) depend on when the player
// pressed what, which the chaos of the balls then spreads.
// Placed well away from the camera; every solid shape and each sensor takes a physics proxy (about a dozen here).
public class PhysicsRandom : TriggerContraption
{
	public readonly Output[] Hits;
	readonly Relay[] kicks;
	public Input Kick(int k) => kicks[k % kicks.Length].Input;

	public PhysicsRandom(double x, double y, int balls = 3, double half = 110, double rotorSpeed = 4)
	{
		var entities = new List<Entity>();
		const double wall = 20;
		// the box: four walls and four corner pieces at 45 degrees
		foreach ((double dx, double dy, double w, double h) in new[] { (0.0, -half - wall / 2, 2 * half + 2 * wall, wall), (0.0, half + wall / 2, 2 * half + 2 * wall, wall),
			(-half - wall / 2, 0.0, wall, 2 * half), (half + wall / 2, 0.0, wall, 2 * half) })
		{
			entities.Add(new Rectangle { X = x + dx, Y = y + dy, Width = w, Height = h, Fixed = true, FillColor = 0x808080 });
		}
		double corner = half * 0.8;
		foreach ((int sx, int sy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
		{
			entities.Add(new Rectangle { X = x + sx * corner, Y = y + sy * corner, Width = half * 0.9, Height = wall, Rotation = sx * sy > 0 ? -45 : 45, Fixed = true, FillColor = 0x808080 });
		}
		// the rotor: a bar pinned to the world at the box's middle, its motor never vehicle-controlled (the game turns those off at load)
		Rectangle rotor = new() { X = x, Y = y, Width = 2 * half - 10, Height = 10, Fixed = false, Density = 5, FillColor = 0x4040C0 };
		entities.Add(rotor);
		entities.Add(new PinJoint(rotor) { X = x, Y = y, Motorized = true, Speed = rotorSpeed, Torque = 100000, CollideConnected = false, VehicleControlled = false });
		Circle[] ball = Enumerable.Range(0, balls).Select(i => new Circle { X = x - half / 2 + i * half / Math.Max(1, balls - 1), Y = y - half / 2, Width = 18, Height = 18, Fixed = false, Density = 1, FillColor = 0xC04040 }).ToArray();
		entities.AddRange(ball);
		// sensors in the box's lower half (where the balls spend most of their time), left and right of the middle
		Hits = new[] { -1, 1 }.Select(side =>
		{
			ActivateTrigger sensor = new() { X = x + side * half * 0.55, Y = y + half * 0.55, Width = 40, Height = 40, TriggeredBy = TriggeredBy.Targets, RepeatType = RepeatType.Multiple };
			foreach (Circle b in ball) { sensor.AddTarget(new Target<Shape>(b, new Impulse<Shape>(0, 0, 0))); }
			entities.Add(sensor);
			return new Output(new[] { sensor });
		}).ToArray();
		kicks = new[] { (3.0, -4.0), (-3.0, -4.0), (4.0, -1.0), (-4.0, -1.0) }.Select(push =>
		{
			Relay kick = new(x, y, 5, 5);
			kick.Output.Connect(new Input { new Target<Shape>(ball[0], new Impulse<Shape>(push.Item1, push.Item2, 0)) });
			entities.AddRange(kick.Entities);
			return kick;
		}).ToArray();
		Entities = entities;
	}
}
