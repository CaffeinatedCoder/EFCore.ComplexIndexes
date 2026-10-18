using System.Globalization;
using System.Text.RegularExpressions;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// A package version as the release path spells it: <c>10.1.0</c>, or a prerelease such as
/// <c>11.0.0-preview.2</c>. <see cref="Version"/> cannot hold the second and orders nothing by
/// SemVer's rules, so every convention test that reads a version reads it through this — the first
/// <c>11.0.0-preview.1</c> would otherwise have failed the release gate in the test step, not at
/// the tag check.
/// </summary>
/// <remarks>
/// Precedence is SemVer 2.0's: a prerelease sorts before its release (<c>11.0.0-preview.2</c> &lt;
/// <c>11.0.0</c>), and prerelease identifiers compare numerically when both are numeric, ordinally
/// otherwise (<c>preview.10</c> &gt; <c>preview.9</c>). Build metadata is not part of a version
/// here; nothing in the release path writes it. Ported from the sibling repository.
/// </remarks>
internal sealed record ReleaseVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<ReleaseVersion>
{
    /// <summary>The pattern a version heading or property holds, without anchors.</summary>
    internal const string Pattern = @"\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?";

    private static readonly Regex Exact =
        new(@"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-(?<pre>[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*))?$", RegexOptions.Compiled);

    internal bool IsPrerelease => Prerelease is not null;

    /// <summary>A minor: <c>x.y.0</c> with <c>y &gt; 0</c>, not a prerelease.</summary>
    internal bool IsMinor => !IsPrerelease && Minor > 0 && Patch == 0;

    internal static ReleaseVersion Parse(string text)
    {
        var match = Exact.Match(text.Trim());

        if (!match.Success)
            throw new FormatException($"'{text}' is not a version of the form x.y.z or x.y.z-prerelease.");

        return new ReleaseVersion(
            int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture),
            match.Groups["pre"].Success ? match.Groups["pre"].Value : null);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;

        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0) return core;

        if (Prerelease == other.Prerelease) return 0;
        if (Prerelease is null) return 1;
        if (other.Prerelease is null) return -1;

        var left  = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');

        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var leftNumeric  = int.TryParse(left[index], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = int.TryParse(right[index], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

            var order = (leftNumeric, rightNumeric) switch
            {
                (true, true)  => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _             => string.CompareOrdinal(left[index], right[index]),
            };

            if (order != 0) return order;
        }

        return left.Length.CompareTo(right.Length);
    }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}
