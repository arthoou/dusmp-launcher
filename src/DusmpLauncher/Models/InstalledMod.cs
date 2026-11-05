namespace DusmpLauncher.Models;

/// <summary>A local JAR file. FileName is an opaque basename for subsequent operations.</summary>
public sealed record InstalledMod(string FileName, string Name, string? Version, string? Icon, bool Enabled);
