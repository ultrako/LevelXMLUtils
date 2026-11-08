using System.Reflection;
namespace LevelXMLUtils.Controls;

// The vehicles the controls are built from, as levelXML embedded in the package
internal static class EmbeddedLevels
{
	public static string Read(string name)
	{
		using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"LevelXMLUtils.Controls.{name}")
			?? throw new InvalidOperationException($"{name} is not embedded in the package");
		using StreamReader reader = new(stream);
		return reader.ReadToEnd();
	}
}
