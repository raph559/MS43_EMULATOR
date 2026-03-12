namespace Ms43Sim.Tests.Support;

public static class Hex
{
    /// <summary>Parses "E6 F1 0080" and "E6F10080" alike.</summary>
    public static byte[] Bytes(string hex) =>
        Convert.FromHexString(string.Concat(hex.Where(c => !char.IsWhiteSpace(c))));
}
