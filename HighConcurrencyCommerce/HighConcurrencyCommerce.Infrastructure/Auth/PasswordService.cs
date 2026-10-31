using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace HighConcurrencyCommerce.Infrastructure.Auth;

public sealed class PasswordService
{
    private readonly PasswordHasher<UserAccount> hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 210000 }));
    private readonly UserAccount dummy = new();
    private readonly string dummyHash;
    public PasswordService() => dummyHash = hasher.HashPassword(dummy, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    public string Hash(UserAccount user, string password) => hasher.HashPassword(user, password);
    public PasswordVerificationResult Verify(UserAccount? user, string password)
    {
        try { return hasher.VerifyHashedPassword(user ?? dummy, user?.PasswordHash ?? dummyHash, password); }
        catch (FormatException) { return PasswordVerificationResult.Failed; }
    }
}

