namespace Torrentarr.Core.Services;

/// <summary>
/// Hashes and verifies passwords for local auth. Only hashes are stored in config.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Hash a plain password for storage. Never store the return value alongside the password.</summary>
    string HashPassword(string password);

    /// <summary>Verify a plain password against a stored hash. Constant-time when possible.</summary>
    bool VerifyPassword(string password, string hash);
}
