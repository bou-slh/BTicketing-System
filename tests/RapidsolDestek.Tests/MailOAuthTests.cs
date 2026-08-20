using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;
using RapidsolDestek.Web.Services;

namespace RapidsolDestek.Tests;

/// <summary>
/// S8 slice 6: OAuth2 mailbox authentication. The provider token endpoint is scripted
/// through the <see cref="IMailOAuthTokenClient"/> seam — no test ever talks to
/// Microsoft or Google — and every assertion is either on the persisted channel
/// columns or on the connect seam the MailKit clients sit behind.
/// </summary>
[Collection("Postgres")]
public class MailOAuthTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2020, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ---- seams ---------------------------------------------------------------------------

    /// <summary>Scripted token endpoint: records every form it was asked to POST and
    /// answers from a queue (an unscripted call is an explicit failure, never a
    /// silent success).</summary>
    private sealed class ScriptedTokenClient : IMailOAuthTokenClient
    {
        public List<MailOAuthTokenRequest> Requests { get; } = [];
        private readonly Queue<MailOAuthTokenResponse> _script = new();

        public ScriptedTokenClient Script(MailOAuthTokenResponse response)
        {
            _script.Enqueue(response);
            return this;
        }

        public Task<MailOAuthTokenResponse> RequestAsync(MailOAuthTokenRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(_script.Count > 0
                ? _script.Dequeue()
                : new MailOAuthTokenResponse(false, Error: "unscripted_call"));
        }
    }

    /// <summary>Mailbox seam that records the resolved connection and returns an empty
    /// session — the fetch job's credential choice is the whole assertion.</summary>
    private sealed class RecordingInboundClient : IInboundMailClient
    {
        public List<InboundConnection> Connections { get; } = [];

        public Task<IInboundMailSession> ConnectAsync(InboundConnection connection, CancellationToken ct = default)
        {
            Connections.Add(connection);
            return Task.FromResult<IInboundMailSession>(new EmptySession());
        }

        private sealed class EmptySession : IInboundMailSession
        {
            public Task<IReadOnlyList<string>> ListPendingUidsAsync(int max, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<string>>([]);

            public Task<MimeMessage> DownloadAsync(string uid, CancellationToken ct = default) =>
                throw new InvalidOperationException("no messages");

            public Task ApplyPostFetchAsync(string uid, PostFetchAction action, string? archiveFolder,
                CancellationToken ct = default) => Task.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>SMTP seam that records the resolved credentials of each send.</summary>
    private sealed class RecordingSmtpTransport : ISmtpMailTransport
    {
        public List<SmtpTransportSettings> Settings { get; } = [];

        public Task<MailTestResult> SendAsync(SmtpTransportSettings smtp, OutboundSmtpMessage message,
            CancellationToken ct = default)
        {
            Settings.Add(smtp);
            return Task.FromResult(new MailTestResult(true, "sent"));
        }
    }

    // ---- helpers -------------------------------------------------------------------------

    /// <summary>
    /// A private OAuth2 account (never the shared canon rows): one Mailbox + one Smtp
    /// channel on the given provider's app registration. The mailbox stays INACTIVE
    /// unless a test actually runs the fetch sweep — MailFetchJob walks every active
    /// mailbox in the shared container database, so idle test rows would join other
    /// classes' fetch passes.
    /// </summary>
    private async Task<EmailAccount> OAuthAccountAsync(
        ServiceScopeBundle s, MailOAuthProvider provider = MailOAuthProvider.Microsoft,
        bool fetching = false)
    {
        var secrets = s.Get<IMailCredentialResolver>();
        // "a…" keeps these rows below the canon addresses in the admin list's default
        // address-desc order, so they never displace destek@/bordro@/bilgi@ on page 1.
        var tag = $"aoa{Guid.NewGuid():N}"[..12];
        var address = $"{tag}@rapidsol.com.tr";
        // Hosts are unique per account: the fetch job sweeps EVERY active mailbox in
        // the shared container database, so a test must be able to find its own dial.
        var account = new EmailAccount
        {
            Address = address,
            DisplayName = "S8 OAuth",
            Channels =
            [
                new EmailChannel
                {
                    Kind = EmailChannelKind.Mailbox, IsActive = fetching,
                    Protocol = MailProtocol.Imap, AuthKind = MailAuthKind.OAuth2,
                    Host = $"imap.{tag}.example.test", Port = 993, Folder = "INBOX",
                    Username = address, OAuthClientId = "client-abc", OAuthProvider = provider,
                    OAuthClientSecretProtected = secrets.Protect("client-secret-xyz"),
                    FetchFrequencyMinutes = 5, FetchMax = 30,
                },
                new EmailChannel
                {
                    Kind = EmailChannelKind.Smtp, IsActive = true,
                    Protocol = MailProtocol.Smtp, AuthKind = MailAuthKind.OAuth2,
                    Host = $"smtp.{tag}.example.test", Port = 587,
                    Username = address, OAuthClientId = "client-abc", OAuthProvider = provider,
                    OAuthClientSecretProtected = secrets.Protect("client-secret-xyz"),
                },
            ],
        };
        s.Db.EmailAccounts.Add(account);
        await s.Db.SaveChangesAsync();
        return account;
    }

    /// <summary>Stores a consent (refresh token) and, optionally, a cached access
    /// token with the given absolute expiry.</summary>
    private static void GrantConsent(ServiceScopeBundle s, EmailChannel channel,
        string refreshToken = "refresh-1", string? accessToken = null, DateTimeOffset? expiresAt = null)
    {
        var secrets = s.Get<IMailCredentialResolver>();
        channel.OAuthRefreshTokenProtected = secrets.Protect(refreshToken);
        channel.OAuthConsentAt = Now.AddDays(-1);
        channel.OAuthConsentAccount = channel.Username;
        channel.OAuthAccessTokenProtected = accessToken is null ? null : secrets.Protect(accessToken);
        channel.OAuthAccessTokenExpiresAt = expiresAt;
    }

    /// <summary>Retires a test's channels so the shared container's fetch sweep (and
    /// every later test class that runs it) stops seeing them.</summary>
    private static async Task RetireAsync(ServiceScopeBundle s, EmailAccount account)
    {
        foreach (var channel in account.Channels)
            channel.IsActive = false;
        await s.Db.SaveChangesAsync();
    }

    private MailOAuthTokenService TokenService(ServiceScopeBundle s, IMailOAuthTokenClient client,
        DateTimeOffset? now = null) =>
        new(s.Db, client, s.Get<IMailCredentialResolver>(), new FixedTime(now ?? Now));

    private MailFetchJob FetchJob(ServiceScopeBundle s, IInboundMailClient mailbox,
        IMailOAuthTokenService oauth, DateTimeOffset? now = null) =>
        new(s.Db, s.Get<ISettingsService>(), mailbox, s.Get<IMailCredentialResolver>(), oauth,
            s.Get<InboundMailProcessor>(), s.Get<ISystemLogService>(),
            new FixedTime(now ?? Now), NullLogger<MailFetchJob>.Instance);

    private OutboundMailJob SendJob(ServiceScopeBundle s, ISmtpMailTransport transport,
        IMailOAuthTokenService oauth) =>
        new(s.Db, s.Get<ISettingsService>(), transport, s.Get<IMailCredentialResolver>(), oauth,
            s.Get<ISystemLogService>(), s.Get<IMailThreadTokenService>(),
            s.Get<RapidsolDestek.Domain.Services.IFileStore>(), NullLogger<OutboundMailJob>.Instance);

    /// <summary>An unsigned JWT whose payload carries the given email claim (the token
    /// service reads the claim for evidence only, never for authorization).</summary>
    private static string IdTokenWithEmail(string email)
    {
        string Segment(string json) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Segment("{\"alg\":\"none\"}")}.{Segment(JsonSerializer.Serialize(new { email }))}.";
    }

    private static async Task<int> ErrorLogCountAsync(PostgresFixture fixture, string needle)
    {
        await using var db = fixture.CreateContext();
        return await db.Set<SystemLogEntry>()
            .CountAsync(l => l.Type == SystemLogType.Error && l.Log.Contains(needle));
    }

    // ---- consent state (CSRF) -------------------------------------------------------------

    [Fact]
    public void ConsentState_RoundTrips_ButTamperedOrForeignPayloadIsRefused()
    {
        var codec = fixture.Factory.Services.GetRequiredService<IMailOAuthStateService>();

        var (state, challenge) = codec.Create(channelId: 4242, staffId: 7);
        var verified = codec.Validate(state);
        Assert.NotNull(verified);
        Assert.Equal(4242, verified!.ChannelId);
        Assert.Equal(7, verified.StaffId);
        Assert.NotEmpty(verified.CodeVerifier);

        // PKCE: the challenge is the S256 hash, never the verifier itself, and the
        // verifier never travels outside the encrypted state.
        Assert.NotEqual(verified.CodeVerifier, challenge);
        Assert.DoesNotContain(verified.CodeVerifier, state);

        // Any mutation of the payload fails the MAC; so does an outright forgery.
        var tampered = state[..^2] + (state[^1] == 'A' ? "B" : "A");
        Assert.Null(codec.Validate(tampered));
        Assert.Null(codec.Validate("4242|7|forged-verifier"));
        Assert.Null(codec.Validate(""));
        Assert.Null(codec.Validate(null));
    }

    [Fact]
    public void AuthorizeUrl_UsesProviderPreset_ScopesAndPkce()
    {
        var microsoft = new EmailChannel
        {
            Kind = EmailChannelKind.Mailbox, Protocol = MailProtocol.Imap,
            AuthKind = MailAuthKind.OAuth2, OAuthClientId = "cid", OAuthTenant = "contoso.example",
            OAuthProvider = MailOAuthProvider.Microsoft,
        };
        var url = MailOAuthProviders.BuildAuthorizeUrl(microsoft, "https://d.example/cb", "st4te", "ch4llenge");
        Assert.StartsWith("https://login.microsoftonline.com/contoso.example/oauth2/v2.0/authorize?", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("code_challenge=ch4llenge", url);
        Assert.Contains("state=st4te", url);
        Assert.Contains(Uri.EscapeDataString("https://outlook.office.com/IMAP.AccessAsUser.All"), url);
        Assert.Contains(Uri.EscapeDataString("offline_access"), url);

        // The SMTP channel of the same address consents for send only.
        var smtp = new EmailChannel
        {
            Kind = EmailChannelKind.Smtp, Protocol = MailProtocol.Smtp,
            AuthKind = MailAuthKind.OAuth2, OAuthClientId = "cid",
        };
        Assert.Contains("SMTP.Send", MailOAuthProviders.ScopesFor(smtp));
        Assert.DoesNotContain("IMAP", MailOAuthProviders.ScopesFor(smtp));

        // Google: its own endpoints, the full-mailbox scope, and offline access —
        // without which no refresh token is ever issued.
        var google = new EmailChannel
        {
            Kind = EmailChannelKind.Mailbox, Protocol = MailProtocol.Imap,
            AuthKind = MailAuthKind.OAuth2, OAuthClientId = "cid",
            OAuthProvider = MailOAuthProvider.Google,
        };
        var googleUrl = MailOAuthProviders.BuildAuthorizeUrl(google, "https://d.example/cb", "s", "c");
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", googleUrl);
        Assert.Contains("access_type=offline", googleUrl);
        Assert.Contains(Uri.EscapeDataString("https://mail.google.com/"), googleUrl);

        // Explicit override wins over every preset default.
        google.OAuthScopes = "custom.scope";
        Assert.Equal("custom.scope", MailOAuthProviders.ScopesFor(google));
    }

    // ---- code exchange --------------------------------------------------------------------

    [Fact]
    public async Task CodeExchange_PersistsEncryptedRefreshToken_AndConsentEvidence()
    {
        using var s = new ServiceScopeBundle(fixture);
        var account = await OAuthAccountAsync(s);
        var mailbox = account.Channels.Single(c => c.Kind == EmailChannelKind.Mailbox);

        var client = new ScriptedTokenClient().Script(new MailOAuthTokenResponse(
            true, AccessToken: "access-1", RefreshToken: "refresh-1", ExpiresInSeconds: 3600,
            IdToken: IdTokenWithEmail("mailbox@contoso.example")));

        var result = await TokenService(s, client)
            .RedeemCodeAsync(mailbox, "auth-code-1", "https://d.example/cb", "verifier-1");

        Assert.Equal("mailbox@contoso.example", result.Account);
        Assert.Equal(Now, result.ConsentAt);

        // The exchange is a real authorization_code grant carrying PKCE + the client secret.
        var form = Assert.Single(client.Requests).Form;
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("auth-code-1", form["code"]);
        Assert.Equal("verifier-1", form["code_verifier"]);
        Assert.Equal("https://d.example/cb", form["redirect_uri"]);
        Assert.Equal("client-secret-xyz", form["client_secret"]);

        // Persisted: ciphertext at rest, never the bearer strings themselves.
        var secrets = s.Get<IMailCredentialResolver>();
        await using var db = fixture.CreateContext();
        var stored = await db.Set<EmailChannel>().SingleAsync(c => c.Id == mailbox.Id);
        Assert.NotNull(stored.OAuthRefreshTokenProtected);
        Assert.DoesNotContain("refresh-1", stored.OAuthRefreshTokenProtected!);
        Assert.Equal("refresh-1", secrets.Unprotect(stored.OAuthRefreshTokenProtected));
        Assert.Equal("access-1", secrets.Unprotect(stored.OAuthAccessTokenProtected));
        Assert.Equal(Now.AddSeconds(3600), stored.OAuthAccessTokenExpiresAt);
        Assert.Equal(Now, stored.OAuthConsentAt);
        Assert.Equal("mailbox@contoso.example", stored.OAuthConsentAccount);
    }

    [Fact]
    public async Task CodeExchange_WithoutRefreshToken_IsRefusedRatherThanStored()
    {
        using var s = new ServiceScopeBundle(fixture);
        var account = await OAuthAccountAsync(s);
        var mailbox = account.Channels.Single(c => c.Kind == EmailChannelKind.Mailbox);

        // An access token alone dies in an hour and cannot be renewed — storing it
        // would mean a channel that "works" until the first job runs.
        var client = new ScriptedTokenClient().Script(
            new MailOAuthTokenResponse(true, AccessToken: "access-1", ExpiresInSeconds: 3600));

        var ex = await Assert.ThrowsAsync<MailOAuthException>(() =>
            TokenService(s, client).RedeemCodeAsync(mailbox, "code", "https://d.example/cb", "v"));
        Assert.Equal(MailOAuthException.ConsentStage, ex.Stage);

        await using var db = fixture.CreateContext();
        var stored = await db.Set<EmailChannel>().SingleAsync(c => c.Id == mailbox.Id);
        Assert.Null(stored.OAuthRefreshTokenProtected);
        Assert.Null(stored.OAuthConsentAt);
    }

    // ---- access-token lifecycle ------------------------------------------------------------

    [Fact]
    public async Task AccessToken_IsReusedWhileFresh_AndRefreshedInsideTheSafetyMargin()
    {
        using var s = new ServiceScopeBundle(fixture);
        var account = await OAuthAccountAsync(s);
        var mailbox = account.Channels.Single(c => c.Kind == EmailChannelKind.Mailbox);

        // Comfortably valid: no provider round trip at all.
        GrantConsent(s, mailbox, accessToken: "cached-1", expiresAt: Now.AddMinutes(30));
        await s.Db.SaveChangesAsync();
        var fresh = new ScriptedTokenClient();
        Assert.Equal("cached-1", await TokenService(s, fresh).GetAccessTokenAsync(mailbox));
        Assert.Empty(fresh.Requests);

        // Inside the margin the token counts as already expired — a long fetch must
        // not have it die mid-pass.
        var insideMargin = Now.AddMinutes(MailOAuthTokenService.RefreshMargin.TotalMinutes - 1);
        mailbox.OAuthAccessTokenExpiresAt = insideMargin;
        await s.Db.SaveChangesAsync();

        var refreshing = new ScriptedTokenClient().Script(new MailOAuthTokenResponse(
            true, AccessToken: "cached-2", RefreshToken: "refresh-2", ExpiresInSeconds: 3600));
        Assert.Equal("cached-2", await TokenService(s, refreshing).GetAccessTokenAsync(mailbox));

        var form = Assert.Single(refreshing.Requests).Form;
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("refresh-1", form["refresh_token"]);

        // The rotated refresh token replaces the used one, and the new expiry sticks.
        var secrets = s.Get<IMailCredentialResolver>();
        await using var db = fixture.CreateContext();
        var stored = await db.Set<EmailChannel>().SingleAsync(c => c.Id == mailbox.Id);
        Assert.Equal("cached-2", secrets.Unprotect(stored.OAuthAccessTokenProtected));
        Assert.Equal("refresh-2", secrets.Unprotect(stored.OAuthRefreshTokenProtected));
        Assert.Equal(Now.AddSeconds(3600), stored.OAuthAccessTokenExpiresAt);
    }

    [Fact]
    public async Task ChannelWithoutConsent_FailsFast_WithoutCallingTheProvider()
    {
        using var s = new ServiceScopeBundle(fixture);
        var account = await OAuthAccountAsync(s);
        var smtp = account.Channels.Single(c => c.Kind == EmailChannelKind.Smtp);

        var client = new ScriptedTokenClient();
        var ex = await Assert.ThrowsAsync<MailOAuthException>(() =>
            TokenService(s, client).GetAccessTokenAsync(smtp));
        Assert.Equal(MailOAuthException.ConsentStage, ex.Stage);
        Assert.Empty(client.Requests);
    }

    // ---- revoked grant ----------------------------------------------------------------------

    [Fact]
    public async Task RevokedRefreshToken_OnFetch_StampsChannelBookkeeping_Syslog_AndDropsTheGrant()
    {
        using var s = new ServiceScopeBundle(fixture);
        var account = await OAuthAccountAsync(s, fetching: true);
        var mailbox = account.Channels.Single(c => c.Kind == EmailChannelKind.Mailbox);
        GrantConsent(s, mailbox);
        await s.Db.SaveChangesAsync();

        var before = await ErrorLogCountAsync(fixture, "invalid_grant");
        var client = new ScriptedTokenClient().Script(new MailOAuthTokenResponse(
            false, Error: "invalid_grant", ErrorDescription: "AADSTS700082 refresh token expired"));

        try
        {
            await FetchJob(s, new RecordingInboundClient(), TokenService(s, client)).RunAsync(CancellationToken.None);
        }
        finally
        {
            await RetireAsync(s, account);
        }

        await using var db = fixture.CreateContext();
        var stored = await db.Set<EmailChannel>().SingleAsync(c => c.Id == mailbox.Id);
        Assert.Equal(1, stored.ErrorCount);
        Assert.Contains("revoked or expired", stored.LastErrorMessage);
        Assert.Equal(Now, stored.LastErrorAt);
        // The dead grant is dropped, so the page stops claiming consent exists and the
        // next pass fails fast instead of hammering the provider.
        Assert.Null(stored.OAuthRefreshTokenProtected);
        Assert.Null(stored.OAuthConsentAt);
        Assert.True(await ErrorLogCountAsync(fixture, "invalid_grant") > before,
            "expected a syslog Error row for the revoked grant");
    }

    [Fact]
    public async Task RevokedRefreshToken_OnSend_FailsTheOutboxRow_WithoutBurningRetries()
    {
        using var s = new ServiceScopeBundle(fixture);
        var account = await OAuthAccountAsync(s);
        var smtp = account.Channels.Single(c => c.Kind == EmailChannelKind.Smtp);
        GrantConsent(s, smtp);

        var row = new EmailOutbound
        {
            ToAddress = "musteri@disariden.example", Subject = "OAuth2 gönderim",
            HtmlBody = "<p>deneme</p>", FromEmailAccountId = account.Id,
        };
        s.Db.EmailOutbounds.Add(row);
        await s.Db.SaveChangesAsync();

        var transport = new RecordingSmtpTransport();
        var client = new ScriptedTokenClient().Script(
            new MailOAuthTokenResponse(false, Error: "invalid_grant"));
        await SendJob(s, transport, TokenService(s, client)).SendAsync(row.Id, CancellationToken.None);

        // No SMTP dial at all — the credential was known-dead before the transport ran.
        Assert.Empty(transport.Settings);

        await using var db = fixture.CreateContext();
        var stored = await db.EmailOutbounds.SingleAsync(o => o.Id == row.Id);
        Assert.Equal(EmailOutboundStatus.Failed, stored.Status);
        Assert.StartsWith("oauth-consent:", stored.LastError);
        Assert.Equal(1, stored.Attempts); // re-consent is the only fix; retrying is noise

        var channel = await db.Set<EmailChannel>().SingleAsync(c => c.Id == smtp.Id);
        Assert.Equal(1, channel.ErrorCount);
        Assert.StartsWith("oauth-consent:", channel.LastErrorMessage);
    }

    // ---- credential selection at the connect seams -------------------------------------------

    [Fact]
    public async Task OAuth2Channels_ReachTheConnectSeams_WithABearerToken_NotAPassword()
    {
        using var s = new ServiceScopeBundle(fixture);
        var account = await OAuthAccountAsync(s, fetching: true);
        var mailbox = account.Channels.Single(c => c.Kind == EmailChannelKind.Mailbox);
        var smtp = account.Channels.Single(c => c.Kind == EmailChannelKind.Smtp);
        // A stored basic password must be ignored entirely once the mode is OAuth2.
        var secrets = s.Get<IMailCredentialResolver>();
        mailbox.PasswordProtected = secrets.Protect("legacy-basic-password");
        smtp.PasswordProtected = secrets.Protect("legacy-basic-password");
        GrantConsent(s, mailbox, accessToken: "bearer-in", expiresAt: Now.AddMinutes(30));
        GrantConsent(s, smtp, accessToken: "bearer-out", expiresAt: Now.AddMinutes(30));

        var row = new EmailOutbound
        {
            ToAddress = "musteri@disariden.example", Subject = "OAuth2 gönderim",
            HtmlBody = "<p>deneme</p>", FromEmailAccountId = account.Id,
        };
        s.Db.EmailOutbounds.Add(row);
        await s.Db.SaveChangesAsync();

        var mailboxSeam = new RecordingInboundClient();
        try
        {
            await FetchJob(s, mailboxSeam, TokenService(s, new ScriptedTokenClient()))
                .RunAsync(CancellationToken.None);
        }
        finally
        {
            mailbox.IsActive = false;
            await s.Db.SaveChangesAsync();
        }

        var connection = Assert.Single(mailboxSeam.Connections, c => c.Host == mailbox.Host);
        Assert.Equal(MailAuthKind.OAuth2, connection.Auth);
        Assert.Equal("bearer-in", connection.AccessToken);
        // MailKitInboundMailClient picks SaslMechanismOAuth2 exactly on this shape;
        // the legacy password rides along unused and must never be what signs in.
        Assert.NotEqual(connection.Password, connection.AccessToken);

        var transport = new RecordingSmtpTransport();
        await SendJob(s, transport, TokenService(s, new ScriptedTokenClient()))
            .SendAsync(row.Id, CancellationToken.None);

        var sent = Assert.Single(transport.Settings);
        Assert.Equal(MailAuthKind.OAuth2, sent.Auth);
        Assert.Equal("bearer-out", sent.AccessToken);
    }

    [Fact]
    public async Task BasicChannels_KeepUsingThePassword_AndCarryNoToken()
    {
        using var s = new ServiceScopeBundle(fixture);
        var account = await OAuthAccountAsync(s, fetching: true);
        var mailbox = account.Channels.Single(c => c.Kind == EmailChannelKind.Mailbox);
        mailbox.AuthKind = MailAuthKind.Basic;
        mailbox.PasswordProtected = s.Get<IMailCredentialResolver>().Protect("basic-pass-1");
        await s.Db.SaveChangesAsync();

        var mailboxSeam = new RecordingInboundClient();
        // An unscripted token client would fail loudly if the OAuth2 path were taken.
        try
        {
            await FetchJob(s, mailboxSeam, TokenService(s, new ScriptedTokenClient()))
                .RunAsync(CancellationToken.None);
        }
        finally
        {
            await RetireAsync(s, account);
        }

        var connection = Assert.Single(mailboxSeam.Connections, c => c.Host == mailbox.Host);
        Assert.Equal(MailAuthKind.Basic, connection.Auth);
        Assert.Equal("basic-pass-1", connection.Password);
        Assert.Null(connection.AccessToken);
    }

    /// <summary>The tester never downgrades a tokenless OAuth2 channel to an
    /// unauthenticated "connected" answer — it refuses before any network I/O.</summary>
    [Fact]
    public async Task ConnectionTester_RefusesOAuth2WithoutAToken_BeforeAnyNetworkIO()
    {
        var result = await new MailConnectionTester().TestAsync(new MailTestRequest(
            EmailChannelKind.Smtp, MailProtocol.Smtp, "smtp.example.test", 587,
            MailAuthKind.OAuth2, "user@example.test", Password: null, AccessToken: null));
        Assert.False(result.Success);
        Assert.Equal(MailOAuthException.ConsentStage, result.Stage);
    }
}
