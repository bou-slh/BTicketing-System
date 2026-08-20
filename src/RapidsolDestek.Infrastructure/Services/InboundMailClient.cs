using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using RapidsolDestek.Domain.Entities;

namespace RapidsolDestek.Infrastructure.Services;

/// <summary>Shared constants of the S8 mail pipeline's loop protection.</summary>
public static class MailPipelineHeaders
{
    /// <summary>Stamped on every outbound mail (OutboundMailJob → transport); an
    /// inbound message carrying it originated here and came back.</summary>
    public const string LoopTag = "X-RapidsolDestek-Mail";

    /// <summary>Header instances tolerated before an inbound mail counts as a loop
    /// (osTicket allows ~3 passes through the helpdesk before dropping).</summary>
    public const int MaxPasses = 3;
}

/// <summary>Connection settings for one fetch pass over a mailbox channel
/// (decrypted secret included — the caller resolves it, MailConnectionTester shape).
/// <see cref="AccessToken"/> is the S8 slice 6 OAuth2 bearer token the fetch job
/// resolved from the channel's stored consent.</summary>
public sealed record InboundConnection(
    MailProtocol Protocol,
    string Host,
    int Port,
    MailAuthKind Auth,
    string? Username,
    string? Password,
    string? Folder,
    string? AccessToken = null);

/// <summary>
/// The S8 fetch pipeline's mailbox seam: MailFetchJob talks to this, the real
/// implementation is MailKit (IMAP + POP3), and tests swap in a scripted fake so
/// fetching is observable without a mail server (ISmtpMailTransport twin).
/// </summary>
public interface IInboundMailClient
{
    /// <summary>Connects + authenticates + opens the folder. Throws on failure —
    /// the fetch job turns the exception into channel error bookkeeping.</summary>
    Task<IInboundMailSession> ConnectAsync(InboundConnection connection, CancellationToken ct = default);
}

/// <summary>One open mailbox connection; sessions are short-lived (one fetch pass).</summary>
public interface IInboundMailSession : IAsyncDisposable
{
    /// <summary>Server UIDs of up to <paramref name="max"/> unprocessed messages —
    /// IMAP: unseen search; POP3: all pending messages (the persisted
    /// (channel, UID) log is the real cursor there).</summary>
    Task<IReadOnlyList<string>> ListPendingUidsAsync(int max, CancellationToken ct = default);

    Task<MimeMessage> DownloadAsync(string uid, CancellationToken ct = default);

    /// <summary>Applies the channel's post-fetch action to one message: Nothing =
    /// mark seen (IMAP; POP3 no-op — the UID log dedupes), Archive = move to the
    /// archive folder (IMAP; POP3 has no folders → no-op), Delete = delete/expunge.</summary>
    Task ApplyPostFetchAsync(string uid, PostFetchAction action, string? archiveFolder, CancellationToken ct = default);
}

/// <summary>
/// Real MailKit mailbox access for the fetch pipeline: the MailConnectionTester
/// client setup (Auto TLS, hard timeout) carried to message listing/download and
/// the post-fetch actions. S8 slice 6: OAuth2 channels authenticate for real via
/// <see cref="SaslMechanismOAuth2"/> over the access token MailFetchJob resolved from
/// the stored consent; a channel with no grant throws before any listing, and the
/// job's catch turns that into the usual ErrorCount / LastErrorMessage / syslog trail.
/// </summary>
public sealed class MailKitInboundMailClient : IInboundMailClient
{
    /// <summary>Overall budget per network operation; fetch runs on a worker but
    /// must never wedge the recurring job.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<IInboundMailSession> ConnectAsync(InboundConnection connection, CancellationToken ct = default)
    {
        var host = (connection.Host ?? "").Trim();
        if (host.Length == 0 || connection.Port is < 1 or > 65535)
            throw new ArgumentException("Inbound channel host/port not configured.");

        if (connection.Protocol == MailProtocol.Pop)
        {
            var pop = new Pop3Client { Timeout = (int)Timeout.TotalMilliseconds };
            try
            {
                await pop.ConnectAsync(host, connection.Port, SecureSocketOptions.Auto, ct);
                await AuthenticateAsync(pop, connection, ct);
                return await Pop3Session.OpenAsync(pop, ct);
            }
            catch
            {
                pop.Dispose();
                throw;
            }
        }

        var imap = new ImapClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            await imap.ConnectAsync(host, connection.Port, SecureSocketOptions.Auto, ct);
            await AuthenticateAsync(imap, connection, ct);

            var folder = string.IsNullOrWhiteSpace(connection.Folder)
                ? imap.Inbox
                : await imap.GetFolderAsync(connection.Folder.Trim(), ct);
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            return new ImapSession(imap, folder);
        }
        catch
        {
            imap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One sign-in for either protocol: XOAUTH2 for OAuth2 channels (S8 slice 6),
    /// username/password for Basic, and nothing at all for a channel with no
    /// credential (an open dev mailbox). A missing OAuth2 token throws — silently
    /// continuing unauthenticated would fetch nothing and blame the mailbox.
    /// </summary>
    private static async Task AuthenticateAsync(IMailService client, InboundConnection connection, CancellationToken ct)
    {
        if (connection.Auth == MailAuthKind.OAuth2)
        {
            if (string.IsNullOrEmpty(connection.AccessToken))
            {
                throw new MailOAuthException(MailOAuthException.ConsentStage,
                    "OAuth2 mailbox channel has no access token — an administrator must grant consent.");
            }
            await client.AuthenticateAsync(
                new SaslMechanismOAuth2(connection.Username ?? "", connection.AccessToken), ct);
            return;
        }
        if (!string.IsNullOrEmpty(connection.Username))
            await client.AuthenticateAsync(connection.Username, connection.Password ?? "", ct);
    }

    private sealed class ImapSession(ImapClient client, IMailFolder folder) : IInboundMailSession
    {
        public async Task<IReadOnlyList<string>> ListPendingUidsAsync(int max, CancellationToken ct = default)
        {
            var uids = await folder.SearchAsync(SearchQuery.NotSeen, ct);
            return [.. uids.OrderBy(u => u.Id).Take(Math.Max(0, max)).Select(u => u.Id.ToString())];
        }

        public Task<MimeMessage> DownloadAsync(string uid, CancellationToken ct = default) =>
            folder.GetMessageAsync(new UniqueId(uint.Parse(uid)), ct);

        public async Task ApplyPostFetchAsync(string uid, PostFetchAction action, string? archiveFolder,
            CancellationToken ct = default)
        {
            var id = new UniqueId(uint.Parse(uid));
            switch (action)
            {
                case PostFetchAction.Delete:
                    await folder.AddFlagsAsync(id, MessageFlags.Deleted, true, ct);
                    await folder.ExpungeAsync(ct);
                    break;
                case PostFetchAction.Archive when !string.IsNullOrWhiteSpace(archiveFolder):
                    var target = await client.GetFolderAsync(archiveFolder.Trim(), ct);
                    await folder.MoveToAsync(id, target, ct);
                    break;
                default:
                    // Nothing (and Archive without a folder): mark seen so the unseen
                    // search stops re-listing it — the UID log dedupes regardless.
                    await folder.AddFlagsAsync(id, MessageFlags.Seen, true, ct);
                    break;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (client.IsConnected)
                    await client.DisconnectAsync(true, CancellationToken.None);
            }
            catch
            {
                // Best-effort teardown only (tester precedent).
            }
            client.Dispose();
        }
    }

    private sealed class Pop3Session(Pop3Client client, Dictionary<string, int> uidToIndex) : IInboundMailSession
    {
        public static async Task<Pop3Session> OpenAsync(Pop3Client client, CancellationToken ct)
        {
            // POP3 has no seen-flag: list everything; the persisted (channel, UID)
            // log is the cursor. UIDL when the server supports it, else the 1-based
            // index (unstable across deletes — the Message-Id backstop covers it).
            var map = new Dictionary<string, int>();
            if (client.Capabilities.HasFlag(Pop3Capabilities.UIDL))
            {
                var uids = await client.GetMessageUidsAsync(ct);
                for (var i = 0; i < uids.Count; i++)
                    map[uids[i]] = i;
            }
            else
            {
                for (var i = 0; i < client.Count; i++)
                    map[(i + 1).ToString()] = i;
            }
            return new Pop3Session(client, map);
        }

        public Task<IReadOnlyList<string>> ListPendingUidsAsync(int max, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(
                [.. uidToIndex.OrderBy(p => p.Value).Take(Math.Max(0, max)).Select(p => p.Key)]);

        public Task<MimeMessage> DownloadAsync(string uid, CancellationToken ct = default) =>
            client.GetMessageAsync(uidToIndex[uid], ct);

        public async Task ApplyPostFetchAsync(string uid, PostFetchAction action, string? archiveFolder,
            CancellationToken ct = default)
        {
            // POP3 knows no folders or flags: Delete is real, Nothing/Archive are
            // honest no-ops (the UID log keeps reprocessing away).
            if (action == PostFetchAction.Delete)
                await client.DeleteMessageAsync(uidToIndex[uid], ct);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (client.IsConnected)
                    await client.DisconnectAsync(true, CancellationToken.None); // commits deletes
            }
            catch
            {
                // Best-effort teardown only.
            }
            client.Dispose();
        }
    }
}
