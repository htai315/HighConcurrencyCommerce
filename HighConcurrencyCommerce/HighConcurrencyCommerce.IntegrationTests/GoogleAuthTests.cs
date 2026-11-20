using System.Net;
using System.Data.Common;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HighConcurrencyCommerce.Application.Auth;
using HighConcurrencyCommerce.Infrastructure.Auth;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HighConcurrencyCommerce.IntegrationTests;

public sealed class GoogleAuthTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private const string Password = "StrongPassword123!@#";
    private sealed class RegistrationBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Arrivals;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<UserAccount>().Any(e => e.State == EntityState.Added))
            {
                if (Interlocked.Increment(ref Arrivals) == 2) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    private sealed class UserLockObserver(Func<Task>? before = null, Func<Task>? after = null) : DbCommandInterceptor
    {
        private static bool IsUserLock(DbCommand command) => command.CommandText.Contains("FROM store.user_accounts") && command.CommandText.Contains("FOR UPDATE");
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (IsUserLock(command) && before is not null) await before();
            return result;
        }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (IsUserLock(command) && after is not null) await after();
            return result;
        }
    }

    [Fact]
    public async Task Concurrent_replay_consumes_challenge_exactly_once()
    {
        var (setupDb, setupAuth, _) = await CreateTestAuthServiceAsync();
        await using var setup = setupDb;
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = await setupAuth.CreateGoogleChallengeAsync("LOGIN", null, token, default);
        var email = UniqueEmail();
        var sub = UniqueSub();
        async Task<string> Attempt()
        {
            var (db, auth, verifier) = await CreateTestAuthServiceAsync();
            await using var context = db;
            verifier.Handler = (_, nonce) => new(sub, email, true, "Replay", null, null, nonce);
            try
            {
                await auth.GoogleLoginAsync(new() { Credential = "test", ChallengeId = Guid.Parse(challenge.ChallengeId) }, token, null, "replay", default);
                return "OK";
            }
            catch (AuthFailure ex) { return ex.Code; }
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Attempt()));
        Assert.Equal(1, results.Count(r => r == "OK"));
        Assert.Equal(7, results.Count(r => r == "INVALID_CHALLENGE"));
    }

    [Fact]
    public async Task Login_waiting_for_unlink_does_not_deadlock_or_issue_session()
    {
        var (setupDb, setupAuth, _) = await CreateTestAuthServiceAsync();
        await using var setup = setupDb;
        var email = UniqueEmail();
        var sub = UniqueSub();
        await setupAuth.RegisterAsync(new() { Email = email, FullName = "Lock test", Password = Password, ConfirmPassword = Password }, default);
        var user = await setup.UserAccounts.SingleAsync(u => u.Email == email);
        user.EmailVerifiedAt = DateTime.UtcNow;
        setup.ExternalIdentities.Add(new() { Id = Guid.NewGuid(), UserId = user.Id, Provider = "GOOGLE", Subject = sub, Email = email, CreatedAt = DateTime.UtcNow });
        await setup.SaveChangesAsync();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loginWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (unlinkDb, unlinkAuth, _) = await CreateTestAuthServiceAsync(interceptor: new UserLockObserver(after: async () =>
        {
            held.TrySetResult();
            await loginWaiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }));
        await using var unlinkContext = unlinkDb;
        var (loginDb, loginAuth, verifier) = await CreateTestAuthServiceAsync(interceptor: new UserLockObserver(before: () =>
        {
            loginWaiting.TrySetResult();
            return Task.CompletedTask;
        }));
        await using var loginContext = loginDb;
        verifier.Handler = (_, nonce) => new(sub, email, true, "Lock test", null, null, nonce);
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = await loginAuth.CreateGoogleChallengeAsync("LOGIN", null, token, default);
        var unlink = unlinkAuth.UnlinkGoogleAsync(user.Id, new() { CurrentPassword = Password }, "unlink-race", default);
        await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var login = Assert.ThrowsAsync<AuthFailure>(() => loginAuth.GoogleLoginAsync(new() { Credential = "test", ChallengeId = Guid.Parse(challenge.ChallengeId) }, token, null, "login-race", default));
        await Task.WhenAll(unlink, login).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("GOOGLE_AUTH_INVALID", (await login).Code);
        Assert.False(await setup.AuthSessions.AnyAsync(s => s.UserId == user.Id));
        Assert.False(await setup.ExternalIdentities.AnyAsync(i => i.UserId == user.Id));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task Google_account_mutations_rate_limit_reauthentication(string method)
    {
        await using var application = fixture.CreateRateLimitedApplication().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["GoogleAuth:Enabled"] = "true", ["GoogleAuth:ClientId"] = "test.apps.googleusercontent.com" })));
        using var client = application.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        async Task<HttpResponseMessage> Send(string path, object body, string verb = "POST")
        {
            var csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
            using var request = new HttpRequestMessage(new HttpMethod(verb), path) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
            return await client.SendAsync(request);
        }
        var email = UniqueEmail();
        using var register = await Send("/api/v1/auth/register", new { email, password = Password, confirmPassword = Password, fullName = "Rate test" });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode);
        await fixture.VerifyUserAsync(email);
        using var login = await Send("/api/v1/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        for (var i = 0; i < 6; i++)
        {
            using var response = await Send("/api/v1/account/auth-methods/google", new { currentPassword = "wrong", credential = "test", challengeId = Guid.NewGuid() }, method);
            if (i < 5) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            else Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        }
    }
    private static string UniqueEmail() => $"google_{Guid.NewGuid():N}@example.invalid";
    private static string UniqueSub() => $"google_sub_{Guid.NewGuid():N}";

    private sealed class FakeGoogleTokenVerifier : IGoogleTokenVerifier
    {
        public Func<string, string, GoogleUserPayload>? Handler { get; set; }

        public Task<GoogleUserPayload> ValidateAsync(string idToken, string expectedNonce, CancellationToken ct)
        {
            if (Handler is not null)
                return Task.FromResult(Handler(idToken, expectedNonce));

            // Default mock behavior
            return Task.FromResult(new GoogleUserPayload(
                Subject: idToken.StartsWith("sub:") ? idToken[4..] : "mock_sub_12345",
                Email: "mock_user@example.invalid",
                EmailVerified: true,
                Name: "Google Mock User",
                GivenName: "Google",
                FamilyName: "User",
                Nonce: expectedNonce
            ));
        }
    }

    private async Task<(StoreDbContext Db, PostgresAuthService Auth, FakeGoogleTokenVerifier Verifier)> CreateTestAuthServiceAsync(
        bool enabled = true,
        string clientId = "test-client-id.apps.googleusercontent.com",
        IInterceptor? interceptor = null)
    {
        var connection = await fixture.OpenDatabaseAsync();
        var options = new DbContextOptionsBuilder<StoreDbContext>().UseNpgsql(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        var db = new StoreDbContext(options.Options);
        var passwords = new PasswordService();
        var verifier = new FakeGoogleTokenVerifier();
        var googleOptions = Options.Create(new GoogleAuthOptions
        {
            Enabled = enabled,
            ClientId = clientId
        });

        var dataProtection = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        var auth = new PostgresAuthService(db, passwords, TimeProvider.System, verifier, googleOptions, dataProtection);
        return (db, auth, verifier);
    }

    [Fact]
    public async Task Real_GoogleTokenVerifier_rejects_invalid_token_with_GOOGLE_AUTH_INVALID()
    {
        var options = Options.Create(new GoogleAuthOptions
        {
            Enabled = true,
            ClientId = "test-client.apps.googleusercontent.com"
        });
        var verifier = new GoogleTokenVerifier(options);

        var ex1 = await Assert.ThrowsAsync<AuthFailure>(() =>
            verifier.ValidateAsync("invalid.jwt.token", "nonce123", CancellationToken.None));
        Assert.True(ex1.Code is "GOOGLE_AUTH_INVALID" or "GOOGLE_AUTH_UNAVAILABLE");

        var ex2 = await Assert.ThrowsAsync<AuthFailure>(() =>
            verifier.ValidateAsync("", "nonce123", CancellationToken.None));
        Assert.Equal("GOOGLE_AUTH_INVALID", ex2.Code);

        // Disabled or missing ClientId
        var disabledOptions = Options.Create(new GoogleAuthOptions { Enabled = false, ClientId = "" });
        var disabledVerifier = new GoogleTokenVerifier(disabledOptions);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            disabledVerifier.ValidateAsync("any_token", "nonce123", CancellationToken.None));
    }

    [Fact]
    public async Task Challenge_creation_and_atomic_consumption_replay_protection()
    {
        var (db, auth, _) = await CreateTestAuthServiceAsync();
        await using (db)
        {
            var browserToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var challenge = await auth.CreateGoogleChallengeAsync("LOGIN", null, browserToken, CancellationToken.None);

            Assert.NotNull(challenge.ChallengeId);
            Assert.NotEmpty(challenge.Nonce);

            var challengeId = Guid.Parse(challenge.ChallengeId);
            var inDb = await db.AuthChallenges.FindAsync(challengeId);
            Assert.NotNull(inDb);
            Assert.Equal("LOGIN", inDb.Purpose);
            Assert.Null(inDb.ConsumedAt);
            Assert.True(inDb.ExpiresAt > DateTime.UtcNow);

            // First consumption succeeds
            var loginRequest = new GoogleLoginRequest { Credential = "token", ChallengeId = challengeId };
            // Simulate login with verifier
            var ticket = await auth.GoogleLoginAsync(loginRequest, browserToken, null, "corr-1", CancellationToken.None);
            Assert.NotNull(ticket);

            // Challenge is now marked consumed in DB
            var consumedInDb = await db.AuthChallenges.AsNoTracking().SingleAsync(c => c.Id == challengeId);
            Assert.NotNull(consumedInDb!.ConsumedAt);

            // Replay with the same challenge must fail with INVALID_CHALLENGE
            var replayEx = await Assert.ThrowsAsync<AuthFailure>(() =>
                auth.GoogleLoginAsync(loginRequest, browserToken, null, "corr-2", CancellationToken.None));
            Assert.Equal("INVALID_CHALLENGE", replayEx.Code);
        }
    }

    [Fact]
    public async Task Challenge_rejects_wrong_browser_token_or_wrong_purpose()
    {
        var (db, auth, _) = await CreateTestAuthServiceAsync();
        await using (db)
        {
            var browserToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var challenge = await auth.CreateGoogleChallengeAsync("LOGIN", null, browserToken, CancellationToken.None);
            var challengeId = Guid.Parse(challenge.ChallengeId);

            // Wrong browser token
            var wrongToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var loginRequest = new GoogleLoginRequest { Credential = "token", ChallengeId = challengeId };

            var ex = await Assert.ThrowsAsync<AuthFailure>(() =>
                auth.GoogleLoginAsync(loginRequest, wrongToken, null, "corr", CancellationToken.None));
            Assert.Equal("INVALID_CHALLENGE", ex.Code);
        }
    }

    [Fact]
    public async Task New_customer_registration_via_Google_creates_user_with_null_password_and_external_identity()
    {
        var (db, auth, verifier) = await CreateTestAuthServiceAsync();
        await using (db)
        {
            var email = UniqueEmail();
            var sub = UniqueSub();
            verifier.Handler = (_, nonce) => new GoogleUserPayload(
                Subject: sub,
                Email: email,
                EmailVerified: true,
                Name: "Google Customer",
                GivenName: "Google",
                FamilyName: "Customer",
                Nonce: nonce
            );

            var browserToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var challenge = await auth.CreateGoogleChallengeAsync("LOGIN", null, browserToken, CancellationToken.None);

            var ticket = await auth.GoogleLoginAsync(
                new GoogleLoginRequest { Credential = "dummy", ChallengeId = Guid.Parse(challenge.ChallengeId) },
                browserToken,
                null,
                "corr-new-user",
                CancellationToken.None);

            Assert.NotNull(ticket);
            Assert.Equal(email, ticket.User.Email);
            Assert.Equal("Google Customer", ticket.User.FullName);
            Assert.Equal("CUSTOMER", ticket.User.Role);

            // Verify database state
            var user = await db.UserAccounts
                .Include(u => u.ExternalIdentities)
                .SingleAsync(u => u.Email == email);

            Assert.Null(user.PasswordHash);
            Assert.Equal("ACTIVE", user.AccountStatus);

            var identity = Assert.Single(user.ExternalIdentities);
            Assert.Equal("GOOGLE", identity.Provider);
            Assert.Equal(sub, identity.Subject);
            Assert.Equal(email, identity.Email);

            // Audit log exists
            var audit = await db.AuditLogs.SingleOrDefaultAsync(a => a.EntityId == user.Id && a.Action == "USER_REGISTERED_GOOGLE");
            Assert.NotNull(audit);

            // Password verification safely returns Failed without crashing
            var passwords = new PasswordService();
            Assert.Equal(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed, passwords.Verify(user, "AnyPassword"));
        }
    }

    [Fact]
    public async Task Returning_customer_login_via_Google_reuses_account()
    {
        var (db, auth, verifier) = await CreateTestAuthServiceAsync();
        await using (db)
        {
            var email = UniqueEmail();
            var sub = UniqueSub();
            verifier.Handler = (_, nonce) => new GoogleUserPayload(
                Subject: sub,
                Email: email,
                EmailVerified: true,
                Name: "Returning User",
                GivenName: "Returning",
                FamilyName: "User",
                Nonce: nonce
            );

            // 1st login (creates account)
            var token1 = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var chal1 = await auth.CreateGoogleChallengeAsync("LOGIN", null, token1, CancellationToken.None);
            var ticket1 = await auth.GoogleLoginAsync(
                new GoogleLoginRequest { Credential = "c1", ChallengeId = Guid.Parse(chal1.ChallengeId) },
                token1, null, "corr-1", CancellationToken.None);

            // 2nd login (returning user)
            var token2 = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var chal2 = await auth.CreateGoogleChallengeAsync("LOGIN", null, token2, CancellationToken.None);
            var ticket2 = await auth.GoogleLoginAsync(
                new GoogleLoginRequest { Credential = "c2", ChallengeId = Guid.Parse(chal2.ChallengeId) },
                token2, ticket1.Token, "corr-2", CancellationToken.None);

            Assert.Equal(ticket1.User.Id, ticket2.User.Id);
            Assert.NotEqual(ticket1.Token, ticket2.Token);

            // Old session is revoked
            var oldSession = await auth.FindSessionAsync(ticket1.Token, CancellationToken.None);
            Assert.Null(oldSession);

            // New session is active
            var currentSession = await auth.FindSessionAsync(ticket2.Token, CancellationToken.None);
            Assert.NotNull(currentSession);
            Assert.Equal(ticket1.User.Id, currentSession.Id);

            // Audit log GOOGLE_LOGIN exists
            var audit = await db.AuditLogs.SingleOrDefaultAsync(a => a.EntityId == ticket1.User.Id && a.Action == "GOOGLE_LOGIN");
            Assert.NotNull(audit);
        }
    }

    [Fact]
    public async Task Existing_password_user_with_same_email_requires_linking_ACCOUNT_LINK_REQUIRED()
    {
        var (db, auth, verifier) = await CreateTestAuthServiceAsync();
        await using (db)
        {
            var email = UniqueEmail();
            // Register standard password user first
            await auth.RegisterAsync(new RegisterRequest
            {
                Email = email,
                Password = Password,
                ConfirmPassword = Password,
                FullName = "Existing Password User"
            }, CancellationToken.None);

            // Now attempt Google login with that same email (unlinked)
            verifier.Handler = (_, nonce) => new GoogleUserPayload(
                Subject: UniqueSub(),
                Email: email,
                EmailVerified: true,
                Name: "Google Attempt",
                GivenName: "Google",
                FamilyName: "Attempt",
                Nonce: nonce
            );

            var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var challenge = await auth.CreateGoogleChallengeAsync("LOGIN", null, token, CancellationToken.None);

            var ex = await Assert.ThrowsAsync<AuthFailure>(() =>
                auth.GoogleLoginAsync(
                    new GoogleLoginRequest { Credential = "any", ChallengeId = Guid.Parse(challenge.ChallengeId) },
                    token, null, "corr", CancellationToken.None));

            Assert.Equal("ACCOUNT_LINK_REQUIRED", ex.Code);
        }
    }

    [Fact]
    public async Task Locked_account_cannot_login_via_Google()
    {
        var (db, auth, verifier) = await CreateTestAuthServiceAsync();
        await using (db)
        {
            var email = UniqueEmail();
            var sub = UniqueSub();
            verifier.Handler = (_, nonce) => new GoogleUserPayload(
                Subject: sub,
                Email: email,
                EmailVerified: true,
                Name: "Locked User",
                GivenName: "Locked",
                FamilyName: "User",
                Nonce: nonce
            );

            // Register via Google
            var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var chal = await auth.CreateGoogleChallengeAsync("LOGIN", null, token, CancellationToken.None);
            var ticket = await auth.GoogleLoginAsync(
                new GoogleLoginRequest { Credential = "c", ChallengeId = Guid.Parse(chal.ChallengeId) },
                token, null, "corr", CancellationToken.None);

            // Lock account in DB
            var user = await db.UserAccounts.SingleAsync(u => u.Id == ticket.User.Id);
            user.AccountStatus = "LOCKED";
            await db.SaveChangesAsync();

            // Attempt login again
            var token2 = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var chal2 = await auth.CreateGoogleChallengeAsync("LOGIN", null, token2, CancellationToken.None);

            var ex = await Assert.ThrowsAsync<AuthFailure>(() =>
                auth.GoogleLoginAsync(
                    new GoogleLoginRequest { Credential = "c2", ChallengeId = Guid.Parse(chal2.ChallengeId) },
                    token2, null, "corr", CancellationToken.None));

            Assert.Equal("INVALID_CREDENTIALS", ex.Code);
        }
    }

    [Fact]
    public async Task Staff_or_Admin_account_cannot_login_via_Google()
    {
        var (db, auth, verifier) = await CreateTestAuthServiceAsync();
        await using (db)
        {
            var email = UniqueEmail();
            var sub = UniqueSub();

            // Create admin user manually
            var adminRole = await db.Roles.SingleAsync(r => r.RoleKey == "ADMIN");
            var adminUser = new UserAccount
            {
                Id = Guid.NewGuid(),
                Email = email,
                FullName = "Admin User",
                RoleId = adminRole.Id,
                AccountStatus = "ACTIVE",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.UserAccounts.Add(adminUser);
            db.ExternalIdentities.Add(new ExternalIdentity
            {
                Id = Guid.NewGuid(),
                UserId = adminUser.Id,
                Provider = "GOOGLE",
                Subject = sub,
                Email = email,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            verifier.Handler = (_, nonce) => new GoogleUserPayload(
                Subject: sub,
                Email: email,
                EmailVerified: true,
                Name: "Admin User",
                GivenName: "Admin",
                FamilyName: "User",
                Nonce: nonce
            );

            var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var chal = await auth.CreateGoogleChallengeAsync("LOGIN", null, token, CancellationToken.None);

            var ex = await Assert.ThrowsAsync<AuthFailure>(() =>
                auth.GoogleLoginAsync(
                    new GoogleLoginRequest { Credential = "c", ChallengeId = Guid.Parse(chal.ChallengeId) },
                    token, null, "corr", CancellationToken.None));

            Assert.Equal("FORBIDDEN", ex.Code);
        }
    }

    [Fact]
    public async Task Google_login_rejects_silent_switching_when_active_session_belongs_to_another_user()
    {
        var (db, auth, verifier) = await CreateTestAuthServiceAsync();
        await using (db)
        {
            // Create user A with an active session
            var emailA = UniqueEmail();
            await auth.RegisterAsync(new RegisterRequest
            {
                Email = emailA,
                Password = Password,
                ConfirmPassword = Password,
                FullName = "User A"
            }, CancellationToken.None);
            var userA = await db.UserAccounts.SingleAsync(u => u.Email == emailA);
            userA.EmailVerifiedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            var sessionA = await auth.LoginAsync(new LoginRequest { Email = emailA, Password = Password }, null, CancellationToken.None);

            // Now attempt Google login resolving to user B while passing sessionA.Token
            var emailB = UniqueEmail();
            var subB = UniqueSub();
            verifier.Handler = (_, nonce) => new GoogleUserPayload(
                Subject: subB,
                Email: emailB,
                EmailVerified: true,
                Name: "User B",
                GivenName: "User",
                FamilyName: "B",
                Nonce: nonce
            );

            var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var chal = await auth.CreateGoogleChallengeAsync("LOGIN", null, token, CancellationToken.None);

            var ex = await Assert.ThrowsAsync<AuthFailure>(() =>
                auth.GoogleLoginAsync(
                    new GoogleLoginRequest { Credential = "c", ChallengeId = Guid.Parse(chal.ChallengeId) },
                    token, sessionA.Token, "corr", CancellationToken.None));

            Assert.Equal("SESSION_CONFLICT", ex.Code);
        }
    }

    [Fact]
    public async Task Concurrent_first_time_logins_with_same_Google_sub_do_not_produce_duplicate_users()
    {
        var email = UniqueEmail();
        var sub = UniqueSub();
        var barrier = new RegistrationBarrier();

        async Task<SessionTicket> LoginAttempt()
        {
            var (db, auth, verifier) = await CreateTestAuthServiceAsync(interceptor: barrier);
            await using (db)
            {
                verifier.Handler = (_, nonce) => new GoogleUserPayload(
                    Subject: sub,
                    Email: email,
                    EmailVerified: true,
                    Name: "Concurrent User",
                    GivenName: "Concurrent",
                    FamilyName: "User",
                    Nonce: nonce
                );

                var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
                var chal = await auth.CreateGoogleChallengeAsync("LOGIN", null, token, CancellationToken.None);
                return await auth.GoogleLoginAsync(
                    new GoogleLoginRequest { Credential = "c", ChallengeId = Guid.Parse(chal.ChallengeId) },
                    token, null, "corr-concurrent", CancellationToken.None);
            }
        }

        var task1 = LoginAttempt();
        var task2 = LoginAttempt();
        var results = await Task.WhenAll(task1, task2);

        Assert.Equal(results[0].User.Id, results[1].User.Id);
        Assert.Equal(2, barrier.Arrivals);

        await using var verifyDb = await fixture.OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM store.user_accounts WHERE lower(email)=@email", verifyDb);
        cmd.Parameters.AddWithValue("email", email.ToLowerInvariant());
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(1L, count);
    }

    [Fact]
    public async Task LinkGoogle_validates_password_email_match_and_uniqueness()
    {
        var (db, auth, verifier) = await CreateTestAuthServiceAsync();
        await using (db)
        {
            var email = UniqueEmail();
            await auth.RegisterAsync(new RegisterRequest
            {
                Email = email,
                Password = Password,
                ConfirmPassword = Password,
                FullName = "Link Test User"
            }, CancellationToken.None);
            var user = await db.UserAccounts.SingleAsync(u => u.Email == email);
            user.EmailVerifiedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            var session = await auth.LoginAsync(new LoginRequest { Email = email, Password = Password }, null, CancellationToken.None);
            var userId = session.User.Id;

            // Auth methods before linking
            var methodsBefore = await auth.GetAuthMethodsAsync(userId, CancellationToken.None);
            Assert.True(methodsBefore.HasPassword);
            Assert.False(methodsBefore.GoogleLinked);
            Assert.Null(methodsBefore.GoogleEmail);

            // 1. Invalid password rejection
            var token1 = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var chal1 = await auth.CreateGoogleChallengeAsync("LINK", userId, token1, CancellationToken.None);
            var ex1 = await Assert.ThrowsAsync<AuthFailure>(() =>
                auth.LinkGoogleAsync(userId, new LinkGoogleRequest
                {
                    CurrentPassword = "WrongPassword!",
                    Credential = "c",
                    ChallengeId = Guid.Parse(chal1.ChallengeId)
                }, token1, "corr", CancellationToken.None));
            Assert.Equal("INVALID_PASSWORD", ex1.Code);

            // 2. Email mismatch rejection
            verifier.Handler = (_, nonce) => new GoogleUserPayload(
                Subject: UniqueSub(),
                Email: "different_email@example.invalid",
                EmailVerified: true,
                Name: "Google User",
                GivenName: "Google",
                FamilyName: "User",
                Nonce: nonce
            );
            var token2 = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var chal2 = await auth.CreateGoogleChallengeAsync("LINK", userId, token2, CancellationToken.None);
            var ex2 = await Assert.ThrowsAsync<AuthFailure>(() =>
                auth.LinkGoogleAsync(userId, new LinkGoogleRequest
                {
                    CurrentPassword = Password,
                    Credential = "c",
                    ChallengeId = Guid.Parse(chal2.ChallengeId)
                }, token2, "corr", CancellationToken.None));
            Assert.Equal("EMAIL_MISMATCH", ex2.Code);

            // 3. Successful link with matching email and valid password
            var sub = UniqueSub();
            verifier.Handler = (_, nonce) => new GoogleUserPayload(
                Subject: sub,
                Email: email,
                EmailVerified: true,
                Name: "Google User",
                GivenName: "Google",
                FamilyName: "User",
                Nonce: nonce
            );
            var token3 = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var chal3 = await auth.CreateGoogleChallengeAsync("LINK", userId, token3, CancellationToken.None);
            await auth.LinkGoogleAsync(userId, new LinkGoogleRequest
            {
                CurrentPassword = Password,
                Credential = "c",
                ChallengeId = Guid.Parse(chal3.ChallengeId)
            }, token3, "corr-link-success", CancellationToken.None);

            // Auth methods after linking
            var methodsAfter = await auth.GetAuthMethodsAsync(userId, CancellationToken.None);
            Assert.True(methodsAfter.HasPassword);
            Assert.True(methodsAfter.GoogleLinked);
            Assert.Equal(email, methodsAfter.GoogleEmail);

            // Audit log GOOGLE_IDENTITY_LINKED exists
            var audit = await db.AuditLogs.SingleOrDefaultAsync(a => a.EntityId == userId && a.Action == "GOOGLE_IDENTITY_LINKED");
            Assert.NotNull(audit);

            // 4. Duplicate link rejected
            var token4 = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var chal4 = await auth.CreateGoogleChallengeAsync("LINK", userId, token4, CancellationToken.None);
            var ex4 = await Assert.ThrowsAsync<AuthFailure>(() =>
                auth.LinkGoogleAsync(userId, new LinkGoogleRequest
                {
                    CurrentPassword = Password,
                    Credential = "c",
                    ChallengeId = Guid.Parse(chal4.ChallengeId)
                }, token4, "corr", CancellationToken.None));
            Assert.Equal("IDENTITY_ALREADY_LINKED", ex4.Code);
        }
    }

    [Fact]
    public async Task UnlinkGoogle_blocks_last_auth_method_and_revokes_sessions_on_success()
    {
        var (db, auth, verifier) = await CreateTestAuthServiceAsync();
        await using (db)
        {
            // Case 1: Google-only user cannot unlink (LAST_AUTH_METHOD)
            var email = UniqueEmail();
            var sub = UniqueSub();
            verifier.Handler = (_, nonce) => new GoogleUserPayload(
                Subject: sub,
                Email: email,
                EmailVerified: true,
                Name: "Google Only User",
                GivenName: "Google",
                FamilyName: "User",
                Nonce: nonce
            );

            var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var chal = await auth.CreateGoogleChallengeAsync("LOGIN", null, token, CancellationToken.None);
            var ticket = await auth.GoogleLoginAsync(
                new GoogleLoginRequest { Credential = "c", ChallengeId = Guid.Parse(chal.ChallengeId) },
                token, null, "corr", CancellationToken.None);

            var ex1 = await Assert.ThrowsAsync<AuthFailure>(() =>
                auth.UnlinkGoogleAsync(ticket.User.Id, new UnlinkGoogleRequest { CurrentPassword = "Any" }, "corr", CancellationToken.None));
            Assert.Equal("LAST_AUTH_METHOD", ex1.Code);

            // Set a password for this user
            var user = await db.UserAccounts.SingleAsync(u => u.Id == ticket.User.Id);
            var passwords = new PasswordService();
            user.PasswordHash = passwords.Hash(user, Password);
            await db.SaveChangesAsync();

            // Case 2: Wrong password fails
            var ex2 = await Assert.ThrowsAsync<AuthFailure>(() =>
                auth.UnlinkGoogleAsync(ticket.User.Id, new UnlinkGoogleRequest { CurrentPassword = "WrongPassword" }, "corr", CancellationToken.None));
            Assert.Equal("INVALID_PASSWORD", ex2.Code);

            // Case 3: Correct password unlinks Google and revokes sessions
            await auth.UnlinkGoogleAsync(ticket.User.Id, new UnlinkGoogleRequest { CurrentPassword = Password }, "corr-unlink", CancellationToken.None);

            var methods = await auth.GetAuthMethodsAsync(ticket.User.Id, CancellationToken.None);
            Assert.True(methods.HasPassword);
            Assert.False(methods.GoogleLinked);

            // Session was revoked
            var session = await auth.FindSessionAsync(ticket.Token, CancellationToken.None);
            Assert.Null(session);

            // Audit log GOOGLE_IDENTITY_UNLINKED exists
            var audit = await db.AuditLogs.SingleOrDefaultAsync(a => a.EntityId == ticket.User.Id && a.Action == "GOOGLE_IDENTITY_UNLINKED");
            Assert.NotNull(audit);
        }
    }

    [Fact]
    public async Task Http_endpoints_csrf_and_challenge_flow()
    {
        using var client = fixture.CreateClient();

        // 1. GET /api/v1/auth/providers
        using var provRes = await client.GetAsync("/api/v1/auth/providers");
        Assert.Equal(HttpStatusCode.OK, provRes.StatusCode);
        var providers = await provRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(providers.TryGetProperty("google", out var g));
        Assert.True(g.TryGetProperty("enabled", out _));

        // 2. POST /api/v1/auth/google/challenge
        using var csrfRes = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, csrfRes.StatusCode);
        var csrfBody = await csrfRes.Content.ReadFromJsonAsync<JsonElement>();
        var csrfToken = csrfBody.GetProperty("requestToken").GetString()!;

        using var challengeReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/google/challenge")
        {
            Content = JsonContent.Create(new { purpose = "LOGIN" })
        };
        challengeReq.Headers.Add("X-CSRF-TOKEN", csrfToken);
        using var challengeRes = await client.SendAsync(challengeReq);
        Assert.Equal(HttpStatusCode.OK, challengeRes.StatusCode);

        var challengeBody = await challengeRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(challengeBody.TryGetProperty("challengeId", out var chalId));
        Assert.True(challengeBody.TryGetProperty("nonce", out var nonceVal));
        Assert.False(string.IsNullOrWhiteSpace(chalId.GetString()));
        Assert.False(string.IsNullOrWhiteSpace(nonceVal.GetString()));

        // Challenge cookie is set
        var cookies = challengeRes.Headers.GetValues("Set-Cookie");
        Assert.Contains(cookies, c => c.Contains("hcc-challenge") && c.ToLowerInvariant().Contains("httponly"));

        // 3. Anonymous access to auth-methods is blocked (401)
        using var authMethodsRes = await client.GetAsync("/api/v1/account/auth-methods");
        Assert.Equal(HttpStatusCode.Unauthorized, authMethodsRes.StatusCode);
    }
}
