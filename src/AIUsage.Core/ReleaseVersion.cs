using System.Globalization;
using System.Text.RegularExpressions;

namespace AIUsage.Core;

public enum UpdateChannel { Stable, Preview }
public enum UpdateAction { None, Upgrade, ReturnToStable }

/// <summary>Release precedence, including prerelease identifiers and legacy Windows revisions.</summary>
public sealed class ReleaseVersion : IComparable<ReleaseVersion>, IEquatable<ReleaseVersion>
{
    private static readonly Regex Pattern = new(
        @"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*))?(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private ReleaseVersion(int major, int minor, int patch, int revision, string? prerelease)
        => (Major, Minor, Patch, Revision, Prerelease) = (major, minor, patch, revision, prerelease);

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public int Revision { get; }
    public string? Prerelease { get; }
    public bool IsPrerelease => Prerelease is not null;

    public static ReleaseVersion Parse(string text)
        => TryParse(text, out var value) ? value : throw new FormatException("Invalid release version.");

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = null!;
        if (text is null || text.Length > 256) return false;
        var match = Pattern.Match(text);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch)) return false;
        var revision = 0;
        if (match.Groups[4].Success && !int.TryParse(match.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out revision)) return false;
        var prerelease = match.Groups[5].Success ? match.Groups[5].Value : null;
        if (prerelease is not null && (match.Groups[4].Success || prerelease.Split('.').Any(id => IsNumeric(id) && id.Length > 1 && id[0] == '0')))
            return false;
        version = new(major, minor, patch, revision, prerelease);
        return true;
    }

    public static ReleaseVersion FromVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision), null);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        foreach (var comparison in new[] { Major.CompareTo(other.Major), Minor.CompareTo(other.Minor), Patch.CompareTo(other.Patch), Revision.CompareTo(other.Revision) })
            if (comparison != 0) return comparison;
        if (!IsPrerelease || !other.IsPrerelease) return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        var left = Prerelease!.Split('.');
        var right = other.Prerelease!.Split('.');
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var leftNumeric = IsNumeric(left[index]);
            var rightNumeric = IsNumeric(right[index]);
            var comparison = leftNumeric && rightNumeric
                ? left[index].Length.CompareTo(right[index].Length)
                : leftNumeric != rightNumeric ? leftNumeric ? -1 : 1 : 0;
            if (comparison == 0) comparison = StringComparer.Ordinal.Compare(left[index], right[index]);
            if (comparison != 0) return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }

    public bool Equals(ReleaseVersion? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is ReleaseVersion other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Revision, Prerelease);
    public override string ToString()
        => $"{Major}.{Minor}.{Patch}" + (Revision == 0 ? "" : $".{Revision}") + (IsPrerelease ? $"-{Prerelease}" : "");

    private static bool IsNumeric(string value) => value.All(character => character is >= '0' and <= '9');
}

public static class UpdateDecision
{
    public static UpdateAction GetAction(ReleaseVersion current, UpdateChannel channel, ReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(release);
        if (!Enum.IsDefined(channel) || !ReleaseVersion.TryParse(release.Version, out var target) ||
            target.IsPrerelease != release.IsPrerelease || channel == UpdateChannel.Stable && target.IsPrerelease)
            return UpdateAction.None;
        if (target.CompareTo(current) > 0) return UpdateAction.Upgrade;
        return current.IsPrerelease && channel == UpdateChannel.Stable && !target.IsPrerelease
            ? UpdateAction.ReturnToStable : UpdateAction.None;
    }
}
