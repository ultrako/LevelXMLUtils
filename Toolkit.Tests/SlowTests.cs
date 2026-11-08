using Xunit;
namespace LevelXMLUtils.Tests;

// Tests that take minutes (whole games run in lockstep with the reference interpreter) or only measure (costs, the heaviest frames):
// skipped unless SLOW_TESTS=1, so a plain `dotnet test` stays quick.
//   SLOW_TESTS=1 dotnet test Toolkit.Tests
public sealed class SlowFactAttribute : FactAttribute
{
	public SlowFactAttribute() { if (!SlowTests.Enabled) { Skip = SlowTests.Reason; } }
}

public sealed class SlowTheoryAttribute : TheoryAttribute
{
	public SlowTheoryAttribute() { if (!SlowTests.Enabled) { Skip = SlowTests.Reason; } }
}

static class SlowTests
{
	public static bool Enabled => Environment.GetEnvironmentVariable("SLOW_TESTS") == "1";
	public const string Reason = "slow: set SLOW_TESTS=1 to run it";
}
