using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MatRegistry.Data;
using MatPlatform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatRegistry.Services.Registry;

/// <summary>
/// Who calls the registry API. Docker and the other clients ask the token service (<c>/v2/token</c>, the challenge of every 401) with Basic credentials, or with
/// none for a public pull, and send the token it gives (<c>Bearer</c>); Basic is also taken directly. The credentials are:
/// <list type="bullet">
/// <item>a <b>personal access token</b> (<c>mrt_…</c>, <see cref="AccessTokens"/>), the way for people who sign in through MatIdentity,</item>
/// <item>the <b>key of an app</b> bound through <c>registry.docker</c> (<c>mat_…</c>, kept by MatPlatform): it pulls,</item>
/// <item>the address and the <b>password</b> of a local account (with the lockout of the sign-in).</item>
/// </list>
/// A token names the actor only; what it may do is checked on every request against the roles of now (<see cref="RegistryPermissions"/>).
/// </summary>
public sealed class RegistryAuth(IDataProtectionProvider protection, IMemoryCache cache, MatBindingStore bindings)
{
    /// <summary>The service of the token (the <c>service</c> of the challenge).</summary>
    public const string Service = "matregistry";

    /// <summary>The capability MatRegistry provides: an app gets a key to pull with (MatPlatform, a key capability).</summary>
    public const string Capability = "registry.docker";

    private static readonly TimeSpan BasicCache = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UserCache = TimeSpan.FromSeconds(30);

    private readonly ITimeLimitedDataProtector _tokens = protection.CreateProtector("MatRegistry.RegistryToken.v1").ToTimeLimitedDataProtector();

    /// <summary>A token for the actor, valid for <paramref name="lifetime"/>.</summary>
    public string Issue(RegistryActor actor, TimeSpan lifetime)
    {
        var payload = actor switch
        {
            { UserId: { } id } => string.Create(CultureInfo.InvariantCulture, $"u|{id}|{(actor.PullOnly ? 1 : 0)}"),
            { Consumer: { } consumer } => "c|" + consumer,
            _ => "a",
        };
        return _tokens.Protect(payload, lifetime);
    }

    /// <summary>The actor of an <c>Authorization</c> header: nobody without one, null when the header is wrong (a 401).</summary>
    public async Task<RegistryActor?> AuthenticateAsync(AppDbContext db, AccountService accounts, string? header, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return RegistryActor.Anonymous;
        }

        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return await FromTokenAsync(db, header[7..].Trim(), ct);
        }

        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var key = "registry:basic:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(header)));
        if (cache.TryGetValue(key, out RegistryActor? known) && known is not null)
        {
            return known;
        }

        if (ParseBasic(header) is not { } credentials)
        {
            return null;
        }

        var actor = await CheckCredentialsAsync(db, accounts, credentials.User, credentials.Password, ct);
        if (actor is not null)
        {
            cache.Set(key, actor, BasicCache);
        }

        return actor;
    }

    /// <summary>The user name and the password of a Basic header.</summary>
    public static (string User, string Password)? ParseBasic(string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            return colon < 0 ? null : (text[..colon], text[(colon + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Checks a user name and a password: an access token, the key of a bound app, or a local account. Null when none fits.</summary>
    public async Task<RegistryActor?> CheckCredentialsAsync(AppDbContext db, AccountService accounts, string user, string password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password))
        {
            return null;
        }

        if (password.StartsWith(AccessTokens.Prefix, StringComparison.Ordinal))
        {
            return await AccessTokens.AuthenticateAsync(db, password, ct);
        }

        if (password.StartsWith(MatBindingStore.KeyStart, StringComparison.Ordinal))
        {
            var binding = await bindings.AuthenticateAsync(Capability, password, ct);
            return binding is null ? null : RegistryActor.ForConsumer(binding.Consumer);
        }

        var result = await accounts.SignInAsync(user, password);
        return result.User is { } person ? RegistryActor.ForPerson(person.Id, person.Role == UserRole.Admin, person.Email) : null;
    }

    private async Task<RegistryActor?> FromTokenAsync(AppDbContext db, string token, CancellationToken ct)
    {
        string payload;
        try
        {
            payload = _tokens.Unprotect(token);
        }
        catch (CryptographicException)
        {
            return null;
        }

        var parts = payload.Split('|');
        switch (parts[0])
        {
            case "a":
                return RegistryActor.Anonymous;
            case "c" when parts.Length == 2:
                return RegistryActor.ForConsumer(parts[1]);
            case "u" when parts.Length == 3 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id):
                var key = "registry:user:" + id.ToString(CultureInfo.InvariantCulture);
                if (!cache.TryGetValue(key, out (bool Admin, string Email)? user))
                {
                    var row = await db.Users.AsNoTracking().Where(u => u.Id == id && u.IsActive).Select(u => new { u.Role, u.Email }).FirstOrDefaultAsync(ct);
                    user = row is null ? null : (row.Role == UserRole.Admin, row.Email);
                    cache.Set(key, user, UserCache);
                }

                return user is { } u ? RegistryActor.ForPerson(id, u.Admin, u.Email, parts[2] == "1") : null;
            default:
                return null;
        }
    }
}
