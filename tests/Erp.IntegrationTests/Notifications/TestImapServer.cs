using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Erp.IntegrationTests.Notifications;

/// <summary>
/// A small IMAP mailbox for the bounce tests (slice S1.11b2, plan point P6): one folder (INBOX) with the messages a test
/// puts in, the commands the IMAP adapter (MailKit) uses (LOGIN, CAPABILITY, LIST, SELECT, UID SEARCH UNSEEN, UID FETCH,
/// UID STORE, LOGOUT) and the \Seen flag, so the real adapter is tested against a real exchange. Plain IMAP, no TLS.
/// </summary>
public sealed class TestImapServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _nextUid;

    public TestImapServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ConcurrentDictionary<uint, (byte[] Raw, bool Seen)> Messages { get; } = new();

    public uint Add(string raw)
    {
        var uid = (uint)Interlocked.Increment(ref _nextUid);
        Messages[uid] = (Encoding.ASCII.GetBytes(raw.ReplaceLineEndings("\r\n")), false);
        return uid;
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 8192, leaveOpen: true);
                async Task Write(string text)
                {
                    var bytes = Encoding.ASCII.GetBytes(text);
                    await stream.WriteAsync(bytes, _stop.Token);
                }

                await Write("* OK [CAPABILITY IMAP4rev1] test mailbox ready\r\n");
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    var parts = line.Split(' ', 3);
                    var tag = parts[0];
                    var command = parts.Length > 1 ? parts[1].ToUpperInvariant() : string.Empty;
                    var rest = parts.Length > 2 ? parts[2] : string.Empty;
                    if (command == "UID" && rest.Length > 0)
                    {
                        var sub = rest.Split(' ', 2);
                        command = "UID " + sub[0].ToUpperInvariant();
                        rest = sub.Length > 1 ? sub[1] : string.Empty;
                    }

                    switch (command)
                    {
                        case "CAPABILITY":
                            await Write($"* CAPABILITY IMAP4rev1\r\n{tag} OK CAPABILITY completed\r\n");
                            break;
                        case "LOGIN":
                            await Write($"{tag} OK [CAPABILITY IMAP4rev1] LOGIN completed\r\n");
                            break;
                        case "LIST":
                            await Write(rest.Contains("\"\" \"\"", StringComparison.Ordinal)
                                ? $"* LIST (\\Noselect) \"/\" \"\"\r\n{tag} OK LIST completed\r\n"
                                : $"* LIST (\\HasNoChildren) \"/\" INBOX\r\n{tag} OK LIST completed\r\n");
                            break;
                        case "SELECT":
                        case "EXAMINE":
                            await Write(string.Create(CultureInfo.InvariantCulture,
                                $"* {Messages.Count} EXISTS\r\n* 0 RECENT\r\n* FLAGS (\\Seen)\r\n* OK [PERMANENTFLAGS (\\Seen)] ok\r\n* OK [UIDVALIDITY 1] ok\r\n* OK [UIDNEXT {_nextUid + 1}] ok\r\n{tag} OK [READ-WRITE] SELECT completed\r\n"));
                            break;
                        case "UID SEARCH":
                            var unseen = Messages.Where(m => !m.Value.Seen).Select(m => m.Key).Order().Select(u => u.ToString(CultureInfo.InvariantCulture));
                            await Write($"* SEARCH {string.Join(' ', unseen)}\r\n{tag} OK SEARCH completed\r\n".Replace("SEARCH \r\n", "SEARCH\r\n", StringComparison.Ordinal));
                            break;
                        case "UID FETCH":
                            var fetchUid = uint.Parse(rest.Split(' ')[0], CultureInfo.InvariantCulture);
                            if (Messages.TryGetValue(fetchUid, out var m))
                            {
                                var seq = Messages.Keys.Order().ToList().IndexOf(fetchUid) + 1;
                                await Write(string.Create(CultureInfo.InvariantCulture, $"* {seq} FETCH (UID {fetchUid} BODY[] {{{m.Raw.Length}}}\r\n"));
                                await stream.WriteAsync(m.Raw, _stop.Token);
                                await Write(")\r\n");
                            }

                            await Write($"{tag} OK FETCH completed\r\n");
                            break;
                        case "UID STORE":
                            var storeUid = uint.Parse(rest.Split(' ')[0], CultureInfo.InvariantCulture);
                            if (Messages.TryGetValue(storeUid, out var s) && rest.Contains("\\Seen", StringComparison.OrdinalIgnoreCase))
                            {
                                Messages[storeUid] = (s.Raw, true);
                            }

                            await Write($"{tag} OK STORE completed\r\n");
                            break;
                        case "LOGOUT":
                            await Write($"* BYE logging out\r\n{tag} OK LOGOUT completed\r\n");
                            return;
                        default:
                            await Write($"{tag} OK {command} completed\r\n");
                            break;
                    }
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // the client went away or the server stops
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }
}
