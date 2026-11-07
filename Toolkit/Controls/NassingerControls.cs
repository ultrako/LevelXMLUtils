using LevelXML;
namespace LevelXMLUtils.Controls;

/// <summary>
/// Changes to the imported controls that make each key's part travel a short, limited way and spring back on release (see
/// NassingerControls). Every number is a starting point found by measuring, in game
/// ticks, how long each key takes to fire its "on" and "off" triggers.
/// </summary>
public record ControlsTuning
{
	// How far the arrow keys' flippers may turn from rest (degrees): just past the angle where they reach their "on" sensor
	public double FlipDegrees { get; init; } = 26;
	// The return spring on each flipper: a second pin joint at the same pivot, not vehicle-controlled, driving back toward rest
	public double SpringSpeed { get; init; } = 20;
	public double SpringTorque { get; init; } = 1;
	// The speed Up and Down drive their flippers' motors at (the imported controls use 20): faster gets a short press to the sensor before the spring wins
	public double FlipSpeed { get; init; } = 12;
	// Their motors' torque (the imported controls use 99999, which slams the flipper into its limit)
	public double FlipTorque { get; init; } = 99999;
	// How tall the arrow keys' "on" sensors are (the imported ones are 5): grown upward, away from the flipper's rest position, so a flipper that
	// bounces at its limit stays inside instead of touching and leaving within one physics step, which a repeating trigger misses
	public double OnSensorHeight { get; init; } = 5;
	// Stop the arrow paddles with fixed shapes instead of joint limits (a fast paddle clips past a joint limit for a tick): a stopper
	// under each paddle at rest, and one just above its tip where the paddle is StopDegrees up, inside its "on" sensor
	public bool StopShapes { get; init; } = false;
	public double StopDegrees { get; init; } = 24;
	// Replace each arrow paddle (a 35 by 5 rectangle and a 10 by 10 block by its pivot) with one thin polygon bar of the same reach (the
	// game has a minimum rectangle width but not a minimum polygon width, so the paddle can be much lighter and its joints need far less force)
	public bool PolygonPaddles { get; init; } = false;
	public double PaddleThickness { get; init; } = 1.5;
	public double PaddleDensity { get; init; } = 0.1;
	// Longer levers: the arrow paddles reach LeverReach from their pivots, their "on" and "off" sensors move to the tip (which moves
	// fastest, so a few degrees clear them: "off" is left after about a degree, "on" entered at OnDegrees), and they are stopped by shapes StopRadius
	// from the pivot (which moves slowest, so it does not phase through them) at rest and at StopDegrees. Replaces StopShapes' stoppers and the joint limits.
	public bool LongLevers { get; init; } = false;
	public double LeverReach { get; init; } = 47.5;
	public double OnDegrees { get; init; } = 5;
	public double StopRadius { get; init; } = 10;
	// How far up the lever has to turn to leave its "off" sensor (degrees): margin against a wobble at the rest stop flickering "off"
	public double OffDegrees { get; init; } = 1;
	// A concave polygon as the Up and Down paddles' "on" stop instead of their joint limit (the game hands a polygon to Box2D as one
	// shape whatever its shape, and Box2D's collision assumes it is convex, so a concave one lets some surfaces pass and pushes a body out of others with
	// no speed). 0: none; 1: a U opening toward the paddle; 2: a V notch. Centred where the paddle's tip should stop, ConcaveDegrees up, inside its sensor.
	public double ConcaveStop { get; init; } = 1;
	public double ConcaveDegrees { get; init; } = 22;
	public double ConcaveSize { get; init; } = 6;
	// Left and Right turn their flippers with a lean impulse, which any spring strong enough to matter overpowers: their limits alone bring them
	// back fast enough (a spring torque of 0 leaves the spring out)
	public double LeanDegrees { get; init; } = 22;
	public double LeanSpringTorque { get; init; } = 0.1;
	// The lean flippers' own leaning strength (the editor allows 0 to 10; the imported controls use 3): a stronger lean can beat a stronger spring
	public double LeanStrength { get; init; } = 6;
	// How far the jets may rise from rest (units), and their return spring (a second sliding joint driving down)
	public double JetTravel { get; init; } = 6;
	public double JetSpringSpeed { get; init; } = 20;
	public double JetSpringForce { get; init; } = 15;
	// Repeating keys: a fire-once "on" that starts a trigger-driven clock, instead of an "on" that repeats while its sensor is touched (which
	// misses short taps: the paddle only touches the sensor for about a tick)
	public bool TriggerClock { get; init; } = true;
}

public class NassingerControls : TriggerContraption, IControls
{
    public Output UpOn => Out(0);
    public Output UpOff => Out(1);
    public Output DownOn => Out(2);
    public Output DownOff => Out(3);
    public Output LeftOn => Out(4);
    public Output LeftOff => Out(5);
    public Output RightOn => Out(6);
    public Output RightOff => Out(7);
    public Output SpaceOn => Out(8);
    public Output SpaceOff => Out(9);
    public Output ShiftOn => Out(10);
    public Output ShiftOff => Out(11);
    public Output CtrlOn => Out(12);
    public Output CtrlOff => Out(13);
    public Vehicle Handle => (Vehicle)level.Groups[0];
    private readonly Level level;
    // What a level connects to for each of the 14 outputs: the controls' own trigger, or a key's clocks (see TriggerClock; one per speed choice)
    private readonly List<ActivateTrigger>?[] clocks = new List<ActivateTrigger>?[14];
    private Output Out(int index) => new(clocks[index] ?? new List<ActivateTrigger> { (ActivateTrigger)level.Triggers[index] });
    // With speed choices (see the constructor): per key index (0 Up, 2 Down, 4 Left, 6 Right), a gate per choice, in the order the choices were given. Only
    // the enabled gate starts its clock when the key is pressed, so a level picks the speed by enabling one gate and disabling the others.
    public readonly Dictionary<int, Relay[]> SpeedGates = new();
    // The clock of each speed gate (what ticks while the key is held; for a divided speed the step a level gets is the divider's, not the clock)
    public readonly Dictionary<int, ActivateTrigger[]> SpeedClocks = new();
    // The controls' own trigger for an output (the key's physical "on" or "off" sensor), whatever a level connects to instead (a clock)
    public ActivateTrigger KeyTrigger(int index) => (ActivateTrigger)level.Triggers[index];
    // turnRepeatSeconds / moveRepeatSeconds: when set, the Left and Right (turn) or Up and Down (move) "on" triggers repeat
    // every that many seconds while the key is held (RepeatType.Continuous, the game's "repeat while touching"; the first
    // firing is immediate) instead of firing once per press. Other levels that use these controls leave them null and
    // keep the old behaviour.
    // A repeat every that many frames (at 30 a second) as a repeating trigger's interval, for 1 frame or 4 and up. The game counts the interval in whole
    // frames, rounding down, and a repeating trigger fires every that many frames plus one (after firing its counter restarts at 0 and climbs back), so
    // n frames is an interval of n - 1 frames, written half a frame above to survive rounding to three decimals. The editor's setter clamps any interval
    // under 0.1 s up to 0.1 s, a repeat every 4 frames: only NaN (which fails every comparison) gets under it, and fires every frame. So 2 and 3 frames
    // can't be an interval at all (see FrameDivider).
    public static double RepeatInterval(int frames) => frames == 1 ? double.NaN
        : frames >= 4 ? Math.Round((frames - 0.5) / 30, 3) : throw new ArgumentException($"no repeat interval gives a step every {frames} frames: use a divider");
    // turnChoices / moveChoices (with the trigger clock): the speeds, in frames per step, the level can switch between at runtime, one clock each (see
    // SpeedGates); the choice equal to turnDefault / moveDefault is selected at the start. 2 or 3 frames is an every-frame clock and a divider.
    public NassingerControls(double x, double y, double? turnRepeatSeconds = null, double? moveRepeatSeconds = null, ControlsTuning? tuning = null,
        IReadOnlyList<int>? turnChoices = null, IReadOnlyList<int>? moveChoices = null, int turnDefault = 0, int moveDefault = 0)
    {
        string importedXML = EmbeddedLevels.Read("NassingerControls.levelXML");
        level = new(importedXML);
        foreach (Entity entity in level.Entities)
        {
            entity.X += x - 1363.60;
            entity.Y += y - 5270.25;
        }
        // Space, Shift and Ctrl fire the vehicle's three jets, and each "on" trigger (level.Triggers 8, 10 and 12) is a contact sensor where its jet's
        // own movement reaches.
        // Their noise is dealt with by moving the whole controls out of earshot (SeatedControls).
        // A repeating trigger fires at once on its first touch only while its repeat counter is full, and the game refills the counter only when the
        // trigger is disabled, not when the touch ends: a release left it part-way, so the next press waited up to a whole interval before its first
        // firing (found by logging the triggers tick by tick in the game). The key's "off" trigger now disables and re-enables its "on" trigger, through two
        // relays (one trigger can't give another two different actions).
        List<Entity> repeatResets = new();
        void Repeat(double? seconds, IReadOnlyList<int>? choices, int defaultFrames, params int[] triggerIndexes)
        {
            if (seconds is not double interval) { return; }
            if (choices is not null && !choices.Contains(defaultFrames)) { throw new ArgumentException($"the starting speed, a step every {defaultFrames} frames, is not one of the choices"); }
            foreach (int index in triggerIndexes)
            {
                ActivateTrigger on = (ActivateTrigger)level.Triggers[index];
                if (tuning?.TriggerClock == true && choices is not null)
                {
                    // One clock per choice, each started (as below) through a gate: the key's "on" activates every gate, and only the enabled one passes
                    ActivateTrigger release = (ActivateTrigger)level.Triggers[index + 1];
                    clocks[index] = new();
                    List<ActivateTrigger> timers = new(); // the clocks themselves, which the release stops (what a level gets may be a divider's step)
                    var gates = new Relay[choices.Count];
                    for (int k = 0; k < choices.Count; k++)
                    {
                        int frames = choices[k];
                        bool divided = frames == 2 || frames == 3;
                        ActivateTrigger clock = new() { X = on.X + 3 + k, Y = on.Y, Width = 5, Height = 5, TriggeredBy = 5, RepeatType = RepeatType.Permanent,
                            Interval = RepeatInterval(divided ? 1 : frames), StartDisabled = false };
                        Relay stop = new(on.X, on.Y + 40 + 10 * k, 5, 5), restart = new(on.X + 10, on.Y + 40 + 10 * k, 5, 5);
                        gates[k] = new Relay(on.X + 20, on.Y + 40 + 10 * k, 5, 5, startDisabled: frames != defaultFrames);
                        stop.Output.Connect(new Input { new Target<Trigger>(clock, new Disable()) });
                        restart.Output.Connect(new Input { new Target<Trigger>(clock, new Enable()) });
                        Input start = new(stop.Input.Concat(restart.Input).Append(new Target<Trigger>(clock, new Activate())));
                        if (divided)
                        {
                            // the clock ticks every frame and the divider passes every frames-th tick on; the press resets it, so the first tick steps at once
                            FrameDivider divider = new(frames, on.X + 30, on.Y + 40 + 10 * k);
                            new Output(new[] { clock }).Connect(divider.Tick);
                            start = new Input(divider.Reset.Concat(start));
                            clocks[index]!.Add(divider.Step.Output[0]);
                            repeatResets.AddRange(divider.Entities);
                        }
                        else { clocks[index]!.Add(clock); }
                        gates[k].Output.Connect(start);
                        timers.Add(clock);
                        repeatResets.Add(clock);
                        repeatResets.AddRange(stop.Entities.Concat(restart.Entities).Concat(gates[k].Entities));
                    }
                    new Output(new[] { on }).Connect(new Input(gates.SelectMany(g => g.Input)));
                    new Output(new[] { release }).Connect(new Input(timers.Select(clock => (Target)new Target<Trigger>(clock, new Disable()))));
                    SpeedGates[index] = gates;
                    SpeedClocks[index] = timers.ToArray();
                    continue;
                }
                if (tuning?.TriggerClock == true)
                {
                    // The key's "on" stays fire-once (it catches even a 1-tick tap) and starts a clock: a Permanent trigger, which the game fires
                    // every interval from when a trigger activates it until one disables it, with no body or sensor of its own. Starting it
                    // disables and re-enables it first (refills its counter, so the first firing is in the same tick: the game runs the queued
                    // single actions, then the repeating ones), through two relays, since one trigger can't give another three actions; the
                    // key's "off" disables it. Levels connect to the clock.
                    ActivateTrigger clock = new() { X = on.X + 3, Y = on.Y, Width = 5, Height = 5, TriggeredBy = 5, RepeatType = RepeatType.Permanent, Interval = interval, StartDisabled = false };
                    ActivateTrigger release = (ActivateTrigger)level.Triggers[index + 1];
                    Relay stop = new(on.X, on.Y + 40, 5, 5), restart = new(on.X + 10, on.Y + 40, 5, 5);
                    stop.Output.Connect(new Input { new Target<Trigger>(clock, new Disable()) });
                    restart.Output.Connect(new Input { new Target<Trigger>(clock, new Enable()) });
                    new Output(new[] { on }).Connect(new Input(stop.Input.Concat(restart.Input).Append(new Target<Trigger>(clock, new Activate()))));
                    new Output(new[] { release }).Connect(new Input { new Target<Trigger>(clock, new Disable()) });
                    clocks[index] = new() { clock };
                    repeatResets.Add(clock);
                    repeatResets.AddRange(stop.Entities.Concat(restart.Entities));
                    continue;
                }
                on.RepeatType = RepeatType.Continuous;
                on.Interval = interval;
                ActivateTrigger off = (ActivateTrigger)level.Triggers[index + 1];
                Relay disable = new(off.X, off.Y + 40, 5, 5), enable = new(off.X + 10, off.Y + 40, 5, 5);
                disable.Output.Connect(new Input { new Target<Trigger>(on, new Disable()) });
                enable.Output.Connect(new Input { new Target<Trigger>(on, new Enable()) });
                new Output(new[] { off }).Connect(new Input(disable.Input.Concat(enable.Input)));
                repeatResets.AddRange(disable.Entities.Concat(enable.Entities));
            }
        }
        Repeat(moveRepeatSeconds, moveChoices, moveDefault, 0, 2); // UpOn, DownOn
        Repeat(turnRepeatSeconds, turnChoices, turnDefault, 4, 6); // LeftOn, RightOn
        Rectangle floor = new()
        {
            Y = 5100,
            X = 0,
            Width = 5000,
            Height = 50
        };
        List<Entity> extra = new();
        if (tuning is not null)
        {
            // The arrow keys: joints 2 to 5 hold the Up, Down, Left and Right flippers to the handle (First = the flipper, Second = the handle, so a
            // positive joint angle turns the flipper counterclockwise on the screen). Up, Down and Left turn theirs that way to reach their "on"
            // sensor; Right's flipper is the one built turned 180 degrees, so it turns the other way. Each gets limits from rest to just past its
            // sensor (so pressing the opposite key only pushes it against its rest stop instead of swinging it away and back), and a return spring.
            for (int j = 2; j <= 5; j++)
            {
                PinJoint flipper = (PinJoint)level.Joints[j];
                // Positive turns Up's, Down's and Left's flippers toward their sensors; Right's flipper is built turned 180 degrees, so it turns the other way
                int toward = j == 5 ? -1 : +1;
                bool lean = j >= 4;
                double degrees = lean ? tuning.LeanDegrees : tuning.FlipDegrees, torque = lean ? tuning.LeanSpringTorque : tuning.SpringTorque;
                if (!lean) { flipper.Speed = Math.Sign(flipper.Speed) * tuning.FlipSpeed; flipper.Torque = tuning.FlipTorque; }
                flipper.Limit = !tuning.StopShapes && !tuning.LongLevers && !(tuning.ConcaveStop > 0 && !lean);
                if (tuning.ConcaveStop > 0 && !lean)
                {
                    static Vertex P(double vx, double vy) => new(new Point(vx, vy), null, null);
                    double z = tuning.ConcaveSize, a = tuning.ConcaveDegrees * Math.PI / 180, side = j == 5 ? -1 : +1;
                    // where the bar's top edge is, 28 out from the pivot, at ConcaveDegrees up; the shape's notch (its local origin) goes there, opening down
                    double cx = flipper.X + side * (28 * Math.Cos(a) + 2.5 * Math.Sin(a)), cy = flipper.Y - (28 * Math.Sin(a) + 2.5 * Math.Cos(a));
                    Vertex[] vertices = tuning.ConcaveStop == 1
                        ? new[] { P(-z, z), P(-z, -z), P(z, -z), P(z, z), P(z / 2, z), P(z / 2, 0), P(-z / 2, 0), P(-z / 2, z) }
                        : new[] { P(-z, z), P(0, -z), P(z, z), P(0, 0) };
                    extra.Add(new Polygon(vertices) { X = cx, Y = cy, Fixed = true, FillColor = 0x808080, OutlineColor = -1 });
                }
                flipper.LowerLimit = toward > 0 ? 0 : -degrees;
                flipper.UpperLimit = toward > 0 ? degrees : 0;
                if (tuning.LongLevers)
                {
                    double side = j == 5 ? -1 : +1, half = 2.5, reach = tuning.LeverReach, radius = reach - 3;
                    double on = tuning.OnDegrees * Math.PI / 180, stop = tuning.StopDegrees * Math.PI / 180, r = tuning.StopRadius;
                    // the paddle's bar, longer, still from 2.45 behind the pivot (in the paddle's own frame the bar points along +x, Right's included)
                    Vehicle paddle = (Vehicle)level.Groups[j - 1];
                    Rectangle bar = paddle.Items.OfType<Rectangle>().First(shape => shape.Width > 30);
                    double pivotX = bar.X - 15.05;
                    bar.Width = reach + 2.45;
                    bar.X = pivotX + (reach - 2.45) / 2;
                    // its sensors at the tip: "on" with its bottom edge where the bar's top edge is at OnDegrees, "off" overlapping the bar's bottom edge at rest
                    Trigger onSensor = level.Triggers[(j - 2) * 2], offSensor = level.Triggers[(j - 2) * 2 + 1];
                    onSensor.Width = offSensor.Width = 5;
                    onSensor.Height = offSensor.Height = 5;
                    onSensor.X = offSensor.X = flipper.X + side * radius;
                    onSensor.Y = flipper.Y - (radius * Math.Sin(on) + half * Math.Cos(on)) - 2.5;
                    // the sensor's top edge where the bar's bottom edge is OffDegrees up, so it overlaps the bar at rest by radius * sin(OffDegrees) or so
                    offSensor.Y = flipper.Y + half - radius * Math.Sin(tuning.OffDegrees * Math.PI / 180) + 2.5;
                    // stoppers near the pivot: above the bar where it is StopDegrees up, and under it at rest
                    double stopTop = -(r * Math.Sin(stop) + half * Math.Cos(stop));
                    extra.Add(new Rectangle { X = flipper.X + side * r, Y = flipper.Y + stopTop - 2, Width = 6, Height = 4, Fixed = true, FillColor = 0x808080, OutlineColor = -1 });
                    extra.Add(new Rectangle { X = flipper.X + side * r, Y = flipper.Y + half + 2, Width = 6, Height = 4, Fixed = true, FillColor = 0x808080, OutlineColor = -1 });
                }
                else if (tuning.StopShapes)
                {
                    // The paddle is a 35 by 5 bar from its pivot (to the right, or to the left for Right's, built turned 180 degrees), "up" is toward its "on" sensor
                    double side = j == 5 ? -1 : +1, theta = tuning.StopDegrees * Math.PI / 180, length = 32.5, half = tuning.PolygonPaddles ? tuning.PaddleThickness / 2 : 2.5;
                    double tipX = length * Math.Cos(theta) - half * Math.Sin(theta), tipY = -(length * Math.Sin(theta) + half * Math.Cos(theta));
                    extra.Add(new Rectangle { X = flipper.X + side * tipX, Y = flipper.Y + tipY - 2, Width = 6, Height = 4, Fixed = true, FillColor = 0x808080, OutlineColor = -1 });
                    extra.Add(new Rectangle { X = flipper.X + side * 23, Y = flipper.Y + half + 2, Width = 6, Height = 4, Fixed = true, FillColor = 0x808080, OutlineColor = -1 });
                }
                if (torque <= 0) { continue; }
                PinJoint spring = new PinJoint(flipper.First, flipper.Second)
                {
                    X = flipper.X, Y = flipper.Y, Motorized = true, Speed = -toward * tuning.SpringSpeed, Torque = torque, CollideConnected = false, VehicleControlled = false,
                };
                extra.Add(spring);
            }
            foreach (int g in new[] { 3, 4 }) { ((Vehicle)level.Groups[g]).LeaningStrength = tuning.LeanStrength; }
            if (tuning.PolygonPaddles)
            {
                static Vertex V(double vx, double vy) => new(new Point(vx, vy), null, null);
                foreach (int g in new[] { 1, 2, 3, 4 })
                {
                    Vehicle paddle = (Vehicle)level.Groups[g];
                    Rectangle bar = paddle.Items.OfType<Rectangle>().First(r => r.Width > 30);
                    double t = tuning.PaddleThickness / 2, l = bar.Width / 2;
                    Polygon thin = new(new[] { V(-l, -t), V(l, -t), V(l, t), V(-l, t) })
                    {
                        X = bar.X, Y = bar.Y, Rotation = bar.Rotation, Density = tuning.PaddleDensity, FillColor = bar.FillColor, OutlineColor = -1, Opacity = bar.Opacity,
                    };
                    paddle.Items.Clear();
                    paddle.Items.Add(thin);
                }
            }
            foreach (int t in new[] { 0, 2, 4, 6 })
            {
                Trigger sensor = level.Triggers[t];
                double bottom = sensor.Y + sensor.Height / 2;
                sensor.Height = tuning.OnSensorHeight;
                sensor.Y = bottom - tuning.OnSensorHeight / 2;
            }
            // The jets: joints 6, 9 and 12 slide them up and down (axis 90 degrees, pointing down the screen, Second = the jet, so rising is a
            // negative translation). Limits from rest up to JetTravel, and a return spring pushing down.
            foreach (int j in new[] { 6, 9, 12 })
            {
                SlidingJoint jet = (SlidingJoint)level.Joints[j];
                jet.Limit = true;
                jet.LowerLimit = -tuning.JetTravel;
                jet.UpperLimit = 0;
                SlidingJoint spring = new SlidingJoint(jet.First, jet.Second)
                {
                    X = jet.X, Y = jet.Y, Angle = jet.Angle, Motorized = true, Speed = tuning.JetSpringSpeed, Force = tuning.JetSpringForce, CollideConnected = false, VehicleControlled = false,
                };
                extra.Add(spring);
            }
        }
        Entities = level.Entities.Concat(extra).Concat(repeatResets).ToList();
    }
}

// Passes on every n-th tick of an every-frame clock (a repeat every 2 or 3 frames, which no repeat interval can give: see NassingerControls.RepeatInterval).
// A ring of n relays, one enabled at a time: a tick fires them in passing order (the last first, so the one a relay enables has already been passed) and the
// enabled one disables itself and enables the next; the first also fires Step. The wrap goes through a pending relay fired after the ring, or the first
// would fire again in the same tick. Reset puts the ring back on the first, so the next tick steps.
public class FrameDivider : TriggerContraption
{
    public readonly Relay Step;
    public readonly Input Tick, Reset;
    public FrameDivider(int n, double x, double y)
    {
        Relay[] ring = Enumerable.Range(0, n).Select(i => new Relay(x + 10 * i, y, 5, 5, startDisabled: i != 0)).ToArray();
        Relay pending = new(x + 10 * n, y, 5, 5, startDisabled: true);
        Step = new Relay(x, y + 5, 5, 5);
        for (int i = 0; i < n; i++)
        {
            Input next = i < n - 1 ? ring[i + 1].Enable : pending.Enable;
            ring[i].Output.Connect(new Input(ring[i].Disable.Concat(next).Concat(i == 0 ? Step.Input : new Input())));
        }
        pending.Output.Connect(new Input(pending.Disable.Concat(ring[0].Enable)));
        Tick = new Input(ring.Reverse().SelectMany(r => r.Input).Concat(pending.Input));
        Reset = new Input(ring[0].Enable.Concat(ring.Skip(1).SelectMany(r => r.Disable)).Concat(pending.Disable));
        Entities = ring.Concat(new[] { pending, Step }).SelectMany(r => r.Entities).ToList();
    }
}
