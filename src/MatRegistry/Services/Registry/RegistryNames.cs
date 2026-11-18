using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MatRegistry.Services.Registry;

/// <summary>
/// The names of the distribution spec (OCI Distribution 1.1): repository names, tags, digests. A name served by MatRegistry is
/// <c>&lt;registry&gt;/&lt;repository&gt;</c>: its first part is the registry, the rest the repository in it.
/// </summary>
public static partial class RegistryNames
{
    public const string Sha256 = "sha256:";

    /// <summary>The longest name the spec lets a client rely on (host and name together stay under 255).</summary>
    public const int MaxNameLength = 255;

    [GeneratedRegex("^[a-z0-9]+(?:(?:\\.|_|__|-+)[a-z0-9]+)*$")]
    private static partial Regex ComponentRegex();

    [GeneratedRegex("^[a-zA-Z0-9_][a-zA-Z0-9._-]{0,127}$")]
    private static partial Regex TagRegex();

    [GeneratedRegex("^sha256:[a-f0-9]{64}$")]
    private static partial Regex DigestRegex();

    /// <summary>A part of a name: <c>web</c>, <c>my-app</c>, <c>v1.2</c>.</summary>
    public static bool IsComponent(string? part) => !string.IsNullOrEmpty(part) && part.Length <= 128 && ComponentRegex().IsMatch(part);

    /// <summary>A whole repository name: parts separated by <c>/</c>.</summary>
    public static bool IsName(string? name)
        => !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && name.Split('/').All(IsComponent);

    /// <summary>The name of a registry: one part, at most 64 characters.</summary>
    public static bool IsRegistryName(string? name) => IsComponent(name) && name!.Length <= 64 && name is not "v2" and not "_catalog";

    public static bool IsTag(string? tag) => !string.IsNullOrEmpty(tag) && TagRegex().IsMatch(tag);

    public static bool IsDigest(string? digest) => !string.IsNullOrEmpty(digest) && DigestRegex().IsMatch(digest);

    /// <summary>Splits <c>team/web/api</c> into the registry <c>team</c> and the repository <c>web/api</c>; null when the name has only one part or is not a name.</summary>
    public static (string Registry, string Repository)? Split(string name)
    {
        if (!IsName(name))
        {
            return null;
        }

        var slash = name.IndexOf('/', StringComparison.Ordinal);
        return slash <= 0 || slash == name.Length - 1 ? null : (name[..slash], name[(slash + 1)..]);
    }

    /// <summary>The digest of bytes: <c>sha256:</c> and 64 hex digits.</summary>
    public static string DigestOf(ReadOnlySpan<byte> bytes) => Sha256 + Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string DigestOf(byte[] bytes) => DigestOf(bytes.AsSpan());

    /// <summary>The hex part of a digest.</summary>
    public static string Hex(string digest) => digest[Sha256.Length..];

    /// <summary>A digest for people: <c>sha256:3f1a9c2b7d4e</c>.</summary>
    public static string Short(string? digest) => digest is null ? string.Empty : digest.Length > 19 ? digest[..19] : digest;
}
