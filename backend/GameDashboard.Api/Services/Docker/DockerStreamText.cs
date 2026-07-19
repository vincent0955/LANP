using System.Runtime.CompilerServices;
using System.Text;
using Docker.DotNet;

namespace GameDashboard.Api.Services.Docker;

/// <summary>
/// Helpers for consuming Docker's multiplexed stdout/stderr streams (logs,
/// exec output) as text. Containers are created without a TTY, so the engine
/// interleaves stdout/stderr frames; <see cref="MultiplexedStream"/> demuxes
/// the frames and these helpers turn them into strings/lines.
/// </summary>
public static class DockerStreamText
{
    public static async Task<string> ReadAllAsync(MultiplexedStream stream, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var builder = new StringBuilder();

        while (true)
        {
            var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct);
            if (read.EOF)
            {
                break;
            }
            builder.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Streams complete lines (both stdout and stderr, in arrival order) until
    /// EOF or cancellation. A trailing unterminated line is emitted at EOF.
    /// </summary>
    public static async IAsyncEnumerable<string> ReadLinesAsync(
        MultiplexedStream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new byte[4096];
        var pending = new StringBuilder();

        while (true)
        {
            var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct);
            if (read.EOF)
            {
                break;
            }

            pending.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));

            while (true)
            {
                var text = pending.ToString();
                var newline = text.IndexOf('\n');
                if (newline < 0)
                {
                    break;
                }

                yield return text[..newline].TrimEnd('\r');
                pending.Remove(0, newline + 1);
            }
        }

        if (pending.Length > 0)
        {
            yield return pending.ToString().TrimEnd('\r');
        }
    }
}
