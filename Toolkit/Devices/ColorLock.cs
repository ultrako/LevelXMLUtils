using LevelXML;
namespace LevelXMLUtils.Devices;

// A color-code combination lock: a row of colored buttons the player clicks (click triggers, so it works with or without a character) and a row
// of dots, one lit for each click of the current attempt, right or wrong. An attempt is judged only once it has as many clicks as the code:
// Unlocked fires if every click was right; otherwise the dots go out and a new attempt starts. Nothing tells which click was wrong, so the code
// can't be found a click at a time (4 colors and 6 clicks: 4,096 codes to try). After unlocking, the buttons and dots are hidden and stop
// responding.
//
// The state is the step (how many clicks so far) and whether every click so far was right: a relay per (step, color) for each of the two
// tracks, enabled for the current step's four colors on the current track. A button fires its color's relay of every step, last step first, so
// a step that a click moves on to has already had its turn. A failed attempt starts over through a relay the button fires after all of them,
// so the click that ended it doesn't also count as the next attempt's first.
public class ColorLock : TriggerContraption
{
	public readonly Output Unlocked;
	public static readonly (string name, int color)[] Colors = { ("red", 0xE03030), ("green", 0x30C040), ("blue", 0x3060E0), ("yellow", 0xE0D030) };

	// code: color indexes into Colors, in order; (x, y): the middle of the row of buttons
	public ColorLock(int[] code, double x, double y, double size = 80, double gap = 30)
	{
		if (code.Length == 0 || code.Any(c => c < 0 || c >= Colors.Length)) { throw new ArgumentException("the code is a list of color indexes"); }
		var entities = new List<Entity>();
		Relay NewRelay(bool startDisabled = false)
		{
			Relay relay = new(x, y, 5, 5, startDisabled: startDisabled);
			entities.AddRange(relay.Entities);
			return relay;
		}
		int steps = code.Length, colors = Colors.Length;
		// right[i, c] / wrong[i, c]: color c clicked as click i, every earlier click right / some earlier click wrong
		Relay[,] right = new Relay[steps, colors], wrong = new Relay[steps, colors];
		for (int i = 0; i < steps; i++) { for (int c = 0; c < colors; c++) { right[i, c] = NewRelay(startDisabled: i != 0); wrong[i, c] = NewRelay(startDisabled: true); } }
		Input Step(Relay[,] track, int i, bool on) => new(Enumerable.Range(0, colors).SelectMany(c => on ? track[i, c].Enable : track[i, c].Disable));

		// the dots: dot i lights when step i has been entered
		double dotSize = size / 4, dotsY = y - size / 2 - gap - dotSize / 2;
		Panel[] dots = Enumerable.Range(0, steps).Select(i => new Panel(x + (i - (steps - 1) / 2.0) * dotSize * 2, dotsY, dotSize, dotSize,
			onColor: 0xFFFFFF, hasOff: false, startDisabled: true)).ToArray();
		foreach (Panel dot in dots) { entities.AddRange(dot.Entities); }
		Input AllDotsOff = new(dots.SelectMany(d => d.Reset));

		Relay unlocked = NewRelay();
		Unlocked = unlocked.Output;
		// a failed attempt: the dots out and the first step's right track on, fired by the button after its steps
		Relay startOver = NewRelay(startDisabled: true);
		startOver.Output.Connect(startOver.Disable.Concat(AllDotsOff).Concat(Step(right, 0, true)));
		for (int i = 0; i < steps; i++)
		{
			bool last = i + 1 == steps;
			for (int c = 0; c < colors; c++)
			{
				// each click lights its dot (first: opacity changes are queued in order, and unlocking or starting over turns the dots off after)
				Input onWrong = last ? startOver.Enable : Step(wrong, i + 1, true);
				Input onRight = last ? unlocked.Input : Step(right, i + 1, true);
				right[i, c].Output.Connect(dots[i].Input.Concat(Step(right, i, false)).Concat(c == code[i] ? onRight : onWrong));
				wrong[i, c].Output.Connect(dots[i].Input.Concat(Step(wrong, i, false)).Concat(onWrong));
			}
		}

		// the buttons: a flat square and a click trigger over it, firing its color's relay of every step, last step first
		var buttons = new List<(Rectangle shape, ActivateTrigger click)>();
		for (int c = 0; c < colors; c++)
		{
			double bx = x + (c - (colors - 1) / 2.0) * (size + gap);
			Rectangle shape = new() { X = bx, Y = y, Width = size, Height = size, FillColor = Colors[c].color, OutlineColor = 0xFFFFFF, Interactive = false };
			ActivateTrigger click = new() { X = bx, Y = y, Width = size, Height = size, TriggeredBy = TriggeredBy.Click, RepeatType = RepeatType.Multiple };
			int color = c;
			Input sweep = new Input(Enumerable.Range(0, steps).Reverse().SelectMany(i => wrong[i, color].Input.Concat(right[i, color].Input))).Concat(startOver.Input);
			foreach (Target target in sweep) { click.AddTarget(target); }
			entities.Add(shape);
			entities.Add(click);
			buttons.Add((shape, click));
		}
		// once unlocked: the buttons and dots hidden, and the buttons no longer respond
		Relay hide = NewRelay();
		hide.Output.Connect(new Input(buttons.Select(b => (Target)new Target<Trigger>(b.click, new Disable())))
			.Concat(new Input(buttons.Select(b => (Target)new Target<Shape>(b.shape, new ChangeOpacity<Shape>(Opacity: 0, Duration: 0)))))
			.Concat(AllDotsOff));
		unlocked.Output.Connect(hide.Input);
		Entities = entities;
	}

	// "blue,yellow,red" -> indexes into Colors
	public static int[] Parse(string code) => code.Split(',').Select(name => Array.FindIndex(Colors, c => c.name == name.Trim().ToLowerInvariant()))
		.Select(i => i >= 0 ? i : throw new ArgumentException($"no color named in {code}")).ToArray();
}
