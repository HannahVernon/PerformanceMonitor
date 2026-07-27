namespace PerformanceMonitor.Ui;

/// <summary>
/// Lenient parsing and display-normalization of product version strings, shared by the Dashboard and Lite
/// UIs. Replaces the copy-pasted "<c>Version.TryParse(v, out p) ? new Version(p.Major, p.Minor, p.Build) …</c>"
/// snippets that were scattered across both apps' server-list / landing / health views.
///
/// The snippets carried a latent bug this centralizes the fix for: <see cref="System.Version.TryParse(string, out System.Version)"/>
/// leaves <c>Build == -1</c> for a two-part version like <c>"3.1"</c>, and the <c>Version(int, int, int)</c>
/// constructor throws <see cref="System.ArgumentOutOfRangeException"/> on a negative build — so every one of
/// those sites would have thrown on a two-part version string. <see cref="Parse"/> clamps it.
///
/// Mirrors the installer-side hardening in <c>Installer.Core.ScriptProvider.TryParseVersionCore</c> (that
/// one lives in the installer layer, which the UI does not reference; this is the UI-layer twin). Distinct
/// from <see cref="SingleInstanceDecision.ParseProductVersion"/>, which parses the running processes'
/// always-four-part assembly versions for comparison and deliberately does not clamp.
/// </summary>
public static class VersionText
{
    /// <summary>
    /// Parse a version string leniently: strip a SemVer <c>+build</c> and/or <c>-prerelease</c> suffix, and
    /// clamp <c>Build</c> up from the <c>-1</c> that a two-part version parses to. Returns null for a
    /// null/blank/unparseable input, so callers can null-check instead of catching.
    /// </summary>
    public static System.Version? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string core = raw.Trim();

        int plus = core.IndexOf('+', System.StringComparison.Ordinal);
        if (plus >= 0)
        {
            core = core[..plus];
        }

        int dash = core.IndexOf('-', System.StringComparison.Ordinal);
        if (dash >= 0)
        {
            core = core[..dash];
        }

        if (!System.Version.TryParse(core, out var parsed))
        {
            return null;
        }

        return new System.Version(parsed.Major, parsed.Minor, System.Math.Max(parsed.Build, 0));
    }

    /// <summary>
    /// The display form of a version string: "Major.Minor.Build" when it parses, otherwise the trimmed
    /// original (and <see cref="string.Empty"/> for null/blank). This is what the server-list views show,
    /// so "3.1.0.0" collapses to "3.1.0" and an unreadable value is passed through rather than lost.
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return Parse(raw)?.ToString() ?? raw.Trim();
    }
}
