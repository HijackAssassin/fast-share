using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace FastShare;

/// <summary>
/// A log file, and a check for whether the share port is actually reachable.
///
/// The Activity tab says what happened; this says why it failed. When something goes wrong on
/// the other machine there is nobody sitting in front of a debugger, so the alternative to a
/// file on disk is asking someone to describe an error from memory.
/// </summary>
public static class Diagnostics
{
    private static readonly Lock Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    public static string FilePath => Path.Combine(AppConfig.Dir, "log.txt");

    /// <summary>
    /// The log kept beside the program itself, written only in debug mode. Handy precisely
    /// because it is where the app is rather than buried in AppData - it can be zipped up with
    /// the build and sent over.
    /// </summary>
    public static string DebugFilePath => Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
        "fastshare-debug.log");

    // =======================================================================
    // Debug mode
    // =======================================================================

    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();

    private static bool _consoleOpen;

    /// <summary>When on, every line also goes to a console window and to the file by the exe.</summary>
    public static bool Verbose { get; private set; }

    /// <summary>
    /// Turns debug mode on or off. A WPF program has no console of its own, so one is
    /// allocated on demand - which is why this can be switched at runtime rather than needing
    /// a restart or a separate console build.
    /// </summary>
    public static void SetVerbose(bool on)
    {
        if (on == Verbose) return;
        Verbose = on;

        try
        {
            if (on)
            {
                if (!_consoleOpen && AllocConsole())
                {
                    _consoleOpen = true;
                    var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                    Console.SetOut(output);
                    Console.Title = "FastShare — debug";
                }

                Log("debug", $"debug mode on — also logging to {DebugFilePath}");
            }
            else
            {
                Log("debug", "debug mode off");
                if (_consoleOpen && FreeConsole()) _consoleOpen = false;
            }
        }
        catch (Exception ex)
        {
            Verbose = on;
            Log("debug", "could not attach a console", ex);
        }
    }

    /// <summary>
    /// Appends one line. Never throws: logging a failure must not become a second failure,
    /// and this is called from catch blocks that are already handling something.
    /// </summary>
    /// <summary>Every line as it is written - dev mode forwards them to the connected host.</summary>
    public static event Action<string>? Written;

    public static void Log(string category, string message, Exception? error = null)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append("  [").Append(category).Append("]  ")
                .Append(message);

            if (error is not null)
            {
                var baseError = error is AggregateException aggregate ? aggregate.GetBaseException() : error;
                line.Append("  || ").Append(baseError.GetType().Name).Append(": ").Append(baseError.Message);

                // The stack only for real faults: an expected network error's message says it
                // all, but a crash with no stack is one nobody can fix.
                if (category == "crash" && error.StackTrace is not null)
                    line.AppendLine().Append(error);
            }

            var text = line.AppendLine().ToString();
            try { Written?.Invoke(text); } catch { }

            lock (Gate)
            {
                Directory.CreateDirectory(AppConfig.Dir);

                // Rotate rather than grow without limit. One previous file is kept, which is
                // enough to cover "it broke, then I restarted it".
                try
                {
                    if (new FileInfo(FilePath).Length > MaxBytes)
                        File.Move(FilePath, FilePath + ".old", overwrite: true);
                }
                catch (FileNotFoundException) { }

                File.AppendAllText(FilePath, text);

                if (!Verbose) return;

                try { Console.Write(text); } catch { }
                try { File.AppendAllText(DebugFilePath, text); } catch { }
            }
        }
        catch { }
    }

    /// <summary>What a port probe concluded.</summary>
    public enum PortState { Unknown, Open, Closed }

    /// <summary>
    /// Whether this PC's share port can be reached from outside.
    ///
    /// Tested by connecting to our own public address, which works because the router sends
    /// the traffic back to us - the same hairpin that makes "local testing" against a public
    /// IP look slow. Not every router does it, so a refusal is reported honestly as closed
    /// rather than as proof, and anything unexpected reports Unknown rather than guessing.
    /// </summary>
    public static async Task<PortState> CheckPortAsync(string? publicHost, int port,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(publicHost)) return PortState.Unknown;

        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            await client.ConnectAsync(publicHost, port, timeout.Token);
            Log("port", $"{publicHost}:{port} reachable");
            return PortState.Open;
        }
        catch (OperationCanceledException)
        {
            // No answer at all. Usually a closed or unforwarded port, but a router that
            // silently drops hairpinned traffic looks the same, so do not overclaim.
            Log("port", $"{publicHost}:{port} timed out");
            return PortState.Unknown;
        }
        catch (SocketException ex)
        {
            Log("port", $"{publicHost}:{port} refused ({ex.SocketErrorCode})");
            return PortState.Closed;
        }
        catch (Exception ex)
        {
            Log("port", $"{publicHost}:{port} check failed", ex);
            return PortState.Unknown;
        }
    }
}
