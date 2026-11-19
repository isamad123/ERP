using System.Net.Sockets;
using System.Text;

namespace Erp.Kernel.Infrastructure.Documents;

/// <summary>Result of one scan. <c>Unavailable</c> means the scanner could not be reached: the file keeps waiting (ruling Q21).</summary>
public enum ScanVerdict
{
    Clean,
    Infected,
    Unavailable,
}

/// <summary>The virus-scan hook (KRN-DOC-02, ruling Q21).</summary>
public interface IVirusScanner
{
    Task<(ScanVerdict Verdict, string? Detail)> ScanAsync(byte[] content, CancellationToken cancellationToken);
}

/// <summary>Host settings of the scanner: <c>Files:Scan:Host</c> and <c>Files:Scan:Port</c> (clamd, default 3310). No host means unavailable.</summary>
public sealed record VirusScanOptions(string? Host, int Port);

/// <summary>ClamAV over the network (clamd <c>INSTREAM</c> protocol); ClamAV runs as its own service, nothing of it is linked in.</summary>
public sealed class ClamAvScanner(VirusScanOptions options) : IVirusScanner
{
    public async Task<(ScanVerdict Verdict, string? Detail)> ScanAsync(byte[] content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            return (ScanVerdict.Unavailable, "no scanner is set up");
        }

        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await client.ConnectAsync(options.Host, options.Port, timeout.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token).ConfigureAwait(false);
            for (var offset = 0; offset < content.Length; offset += 65536)
            {
                var count = Math.Min(65536, content.Length - offset);
                var length = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, count);
                await stream.WriteAsync(length, timeout.Token).ConfigureAwait(false);
                await stream.WriteAsync(content.AsMemory(offset, count), timeout.Token).ConfigureAwait(false);
            }

            await stream.WriteAsync(new byte[4], timeout.Token).ConfigureAwait(false);
            var buffer = new byte[512];
            var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            var answer = Encoding.UTF8.GetString(buffer, 0, read).TrimEnd('\0', '\n', ' ');
            if (answer.EndsWith("OK", StringComparison.Ordinal))
            {
                return (ScanVerdict.Clean, null);
            }

            return answer.EndsWith("FOUND", StringComparison.Ordinal)
                ? (ScanVerdict.Infected, answer.Replace("stream: ", string.Empty, StringComparison.Ordinal).Replace(" FOUND", string.Empty, StringComparison.Ordinal))
                : (ScanVerdict.Unavailable, answer);
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (ScanVerdict.Unavailable, e.Message);
        }
    }
}
