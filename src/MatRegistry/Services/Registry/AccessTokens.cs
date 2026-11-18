using System.Security.Cryptography;
using System.Text;
using MatRegistry.Data;
using MatRegistry.Localization;
using Microsoft.EntityFrameworkCore;

namespace MatRegistry.Services.Registry;

public sealed record AccessTokenRow(Guid Token, string Name, string SecretPrefix, TokenScope Scope, DateTime Created, DateTime? Expires, DateTime? LastUsed)
{
    public bool IsExpired => Expires is { } e && e < DateTime.UtcNow;
}

/// <summary>A token that was just made: its secret is shown once and never again.</summary>
public sealed record NewAccessToken(Guid Token, string Name, string Secret);

/// <summary>
/// Personal access tokens for <c>docker login</c> (the password of a person who signs in through MatIdentity is not MatRegistry's). A person manages only their own;
/// a token never does more than its person may (<see cref="RegistryPermissions"/>), and one with <see cref="TokenScope.Pull"/> only pulls.
/// </summary>
public static class AccessTokens
{
    /// <summary>The start of every token: <c>mrt_</c> (MatRegistry token), so a secret scanner and the registry tell it from a password.</summary>
    public const string Prefix = "mrt_";

    public const int MaxPerPerson = 50;

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public static async Task<List<AccessTokenRow>> ListAsync(AppDbContext db, long userId, CancellationToken ct)
        => await db.AccessTokens.AsNoTracking().Where(t => t.UserId == userId).OrderByDescending(t => t.CreateDate)
            .Select(t => new AccessTokenRow(t.Token, t.Name, t.SecretPrefix, t.Scope, t.CreateDate, t.ExpiresDate, t.LastUsedDate)).ToListAsync(ct);

    /// <summary>Makes a token of the person; answers the secret once, or the message for the field that is wrong.</summary>
    public static async Task<(NewAccessToken? Token, FieldError? Error)> CreateAsync(AppDbContext db, long userId, string name, TokenScope scope, int? days, CancellationToken ct)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length is 0 or > 120)
        {
            return (null, new FieldError("Name", Text.T("Common.NameRequired")));
        }

        if (!Enum.IsDefined(scope))
        {
            return (null, new FieldError("Scope", Text.T("Tokens.Scope.Invalid")));
        }

        if (days is < 1 or > 3650)
        {
            return (null, new FieldError("Days", Text.T("Tokens.Days.Invalid")));
        }

        if (await db.AccessTokens.CountAsync(t => t.UserId == userId, ct) >= MaxPerPerson)
        {
            return (null, new FieldError("Name", Text.T("Tokens.TooMany", MaxPerPerson)));
        }

        var secret = Prefix + Random(36);
        var token = new AccessToken
        {
            UserId = userId,
            Name = name,
            Scope = scope,
            SecretHash = Hash(secret),
            SecretPrefix = secret[..10],
            ExpiresDate = days is { } d ? DateTime.UtcNow.AddDays(d) : null,
        };
        db.AccessTokens.Add(token);
        await db.SaveChangesAsync(ct);
        await ActivityLog.AddAsync(db, RegistryActor.ForPerson(userId, false, string.Empty), "token", name, scope.ToString(), null, null, ct);
        return (new NewAccessToken(token.Token, name, secret), null);
    }

    /// <summary>Removes a token of the person; false when it is not theirs or not there.</summary>
    public static async Task<bool> DeleteAsync(AppDbContext db, long userId, Guid token, CancellationToken ct)
        => await db.AccessTokens.Where(t => t.UserId == userId && t.Token == token).ExecuteDeleteAsync(ct) > 0;

    /// <summary>The person of a secret (not expired, the account active); notes when it was used.</summary>
    public static async Task<RegistryActor?> AuthenticateAsync(AppDbContext db, string secret, CancellationToken ct)
    {
        var hash = Hash(secret);
        var now = DateTime.UtcNow;
        var row = await db.AccessTokens.AsNoTracking().Where(t => t.SecretHash == hash && (t.ExpiresDate == null || t.ExpiresDate > now) && t.User!.IsActive)
            .Select(t => new { t.Id, t.UserId, t.Scope, t.User!.Role, t.User.Email, t.LastUsedDate }).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }

        if (row.LastUsedDate is null || row.LastUsedDate < now.AddMinutes(-5))
        {
            await db.AccessTokens.Where(t => t.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedDate, now), ct);
        }

        return RegistryActor.ForPerson(row.UserId, row.Role == UserRole.Admin, row.Email, row.Scope == TokenScope.Pull);
    }

    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Random(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }
}
