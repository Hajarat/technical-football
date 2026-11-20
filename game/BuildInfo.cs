using System;
using System.Diagnostics;
using System.IO;

namespace FootballSim.Game;

/// <summary>
/// The commit this build was made from (<see cref="Commit"/>, stamped by the StampBuildCommit target in the project file) against
/// the commit the checkout is at now, so a game started on an old build after a pull says so.
/// </summary>
internal static partial class BuildInfo
{
    public const string Unknown = "unknown";

    /// <summary>The checkout's HEAD, short hash, or "unknown" (git missing, not a repository, no answer within 3 s).</summary>
    public static string ReadHead(string projectDir)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return Unknown;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(3000)) { try { p.Kill(true); } catch (Exception) { } return Unknown; }
            string head = output.GetAwaiter().GetResult().Trim();
            return p.ExitCode == 0 && head.Length > 0 ? head : Unknown;
        }
        catch (Exception) { return Unknown; }
    }

    /// <summary>True only when both commits are known and differ (a "-dirty" suffix is ignored).</summary>
    public static bool IsStale(string build, string head)
    {
        build = build.Replace("-dirty", "");
        head = head.Replace("-dirty", "");
        return build != Unknown && head != Unknown && build != head;
    }
}
