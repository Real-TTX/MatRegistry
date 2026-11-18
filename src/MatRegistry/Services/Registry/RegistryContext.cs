using MatRegistry.Data;
using Microsoft.AspNetCore.DataProtection;

namespace MatRegistry.Services.Registry;

/// <summary>What the business layer of the registry works with: the database, the blob store, the client of the remote registries and their secrets.</summary>
public sealed record RegistryContext(AppDbContext Db, BlobStore Store, UpstreamClient Upstream, RegistrySecrets Secrets);

/// <summary>The passwords of the remote registries, protected with the data protection keys of the volume (only <see cref="RegistryAdmin"/> and the client see them in clear).</summary>
public sealed class RegistrySecrets(IDataProtectionProvider protection)
{
    private readonly IDataProtector _protector = protection.CreateProtector("MatRegistry.RegistrySecret.v1");

    public string? Protect(string? secret) => string.IsNullOrEmpty(secret) ? null : _protector.Protect(secret);

    public string? Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(protectedSecret);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // the keys of the volume were lost: the secret has to be entered again
            return null;
        }
    }

    /// <summary>The remote registry as the client talks to it; null for a hosted registry or one without a valid address.</summary>
    public UpstreamTarget? TargetOf(Data.Registry registry)
        => registry.Kind == RegistryKind.Remote && UpstreamClient.NormalizeUrl(registry.Url) is { } url
            ? new UpstreamTarget(registry.Id, url, registry.Username, Unprotect(registry.SecretProtected), registry.SkipTlsVerify)
            : null;
}
