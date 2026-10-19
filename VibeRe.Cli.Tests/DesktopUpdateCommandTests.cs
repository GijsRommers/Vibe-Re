using VibeRe.Cli;

namespace VibeRe.Cli.Tests;

public sealed class DesktopUpdateCommandTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ResolvesOnlyPackagedDesktop(bool windows, bool legacy)
    {
        var root = Path.Combine(Path.GetTempPath(), "VibeRe update command " + Guid.NewGuid());
        var cli = Path.Combine(root, "cli");
        Directory.CreateDirectory(cli);
        try
        {
            Assert.Null(DesktopUpdateCommand.FindDesktop(cli, windows));
            var desktop = Path.Combine(legacy ? Path.Combine(root, "desktop") : root, "VibeRe.Desktop" + (windows ? ".exe" : ""));
            Directory.CreateDirectory(Path.GetDirectoryName(desktop)!);
            File.WriteAllText(desktop, "test fixture; never executed");
            Assert.Equal(desktop, DesktopUpdateCommand.FindDesktop(cli + Path.DirectorySeparatorChar, windows));
        }
        finally { Directory.Delete(root, true); }
    }
}
