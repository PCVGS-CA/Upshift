using Upshift.Core.Services;

namespace Upshift.App.Helpers;

/// <summary>Error text for Upshift's window: full paths become file names, and the Windows user name never shows.</summary>
public static class ScreenText
{
    public static string Clean(string text) =>
        ProblemReport.HidePaths(text, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName);
}
