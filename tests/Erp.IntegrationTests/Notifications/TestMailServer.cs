using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Erp.Kernel.Infrastructure.Notifications.Email;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MimeKit;

namespace Erp.IntegrationTests.Notifications;

/// <summary>
/// A small SMTP server for the e-mail tests (slice S1.11b2): it takes messages on a local port and keeps them, and answers
/// a recipient with a chosen refusal (for example <c>451 4.3.0 try later</c> or <c>550 5.1.1 no such user</c>), so the
/// real SMTP adapter (MailKit) is tested against a real exchange. Plain SMTP without TLS or login, as a relay on the same
/// machine would be.
/// </summary>
public sealed class TestMailServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public TestMailServer()
    {
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    /// <summary>Points the worker's operator relay at a test server (plan point P2): plain SMTP, sender no-reply@erp.test.</summary>
    public static void Use(IServiceCollection s, int port, string? operatorEmail = null)
    {
        s.RemoveAll<MailOptions>();
        s.AddSingleton(MailOptions.None with { SmtpHost = "127.0.0.1", SmtpPort = port, SmtpSecurity = "none", FromAddress = "no-reply@erp.test", OperatorEmail = operatorEmail });
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Messages received, with the envelope recipient.</summary>
    public ConcurrentQueue<(string To, MimeMessage Message)> Received { get; } = new();

    /// <summary>Recipient address (lower case) → the reply line its RCPT gets instead of 250.</summary>
    public ConcurrentDictionary<string, string> Refuse { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<MimeMessage> To(string address) => [.. Received.Where(r => string.Equals(r.To, address, StringComparison.OrdinalIgnoreCase)).Select(r => r.Message)];

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
                using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 8192, leaveOpen: true);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 8192, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 test.local ESMTP");
                var recipients = new List<string>();
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    var verb = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();
                    switch (verb)
                    {
                        case "EHLO":
                            await writer.WriteAsync("250-test.local\r\n250-8BITMIME\r\n250 SMTPUTF8\r\n");
                            break;
                        case "HELO":
                            await writer.WriteLineAsync("250 test.local");
                            break;
                        case "MAIL":
                            recipients.Clear();
                            await writer.WriteLineAsync("250 2.1.0 OK");
                            break;
                        case "RCPT":
                            var address = Address(line);
                            if (Refuse.TryGetValue(address, out var reply))
                            {
                                await writer.WriteLineAsync(reply);
                            }
                            else
                            {
                                recipients.Add(address);
                                await writer.WriteLineAsync("250 2.1.5 OK");
                            }

                            break;
                        case "DATA":
                            await writer.WriteLineAsync("354 end with <CRLF>.<CRLF>");
                            var data = new StringBuilder();
                            while (await reader.ReadLineAsync(_stop.Token) is { } body && body != ".")
                            {
                                data.Append(body.StartsWith("..", StringComparison.Ordinal) ? body[1..] : body).Append("\r\n");
                            }

                            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(data.ToString())))
                            {
                                var message = await MimeMessage.LoadAsync(ms);
                                foreach (var to in recipients)
                                {
                                    Received.Enqueue((to, message));
                                }
                            }

                            await writer.WriteLineAsync("250 2.0.0 queued as test-" + Received.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            break;
                        case "RSET":
                            recipients.Clear();
                            await writer.WriteLineAsync("250 2.0.0 OK");
                            break;
                        case "NOOP":
                            await writer.WriteLineAsync("250 2.0.0 OK");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 2.0.0 bye");
                            return;
                        default:
                            await writer.WriteLineAsync("502 5.5.2 not implemented");
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

    private static string Address(string line)
    {
        var start = line.IndexOf('<', StringComparison.Ordinal);
        var end = line.IndexOf('>', StringComparison.Ordinal);
        return start >= 0 && end > start ? line[(start + 1)..end] : line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
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
