using System.Collections.Concurrent;

namespace Goggles3.Core;

/// <summary>
/// The last lines for the Diagnostics view and a text file. <see cref="Add"/> from any thread; <see cref="Flush"/>
/// (once per second) and <see cref="CloseAsync"/> from one thread. The file is written off that thread.
/// </summary>
public sealed class DiagnosticLog(string path, int capacity = 100)
{
    private readonly ConcurrentQueue<string> incoming = new();
    private readonly Queue<string> lines = new();
    private int writing;
    private bool dirty;
    private Task write = Task.CompletedTask;

    /// <summary>Lines moved in by the last <see cref="Flush"/>, oldest first.</summary>
    public IReadOnlyCollection<string> Lines => lines;

    public void Add(string text) => incoming.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + text);

    public void Flush()
    {
        while (incoming.TryDequeue(out string? line))
        {
            if (lines.Count >= capacity) lines.Dequeue();
            lines.Enqueue(line); dirty = true;
        }
        // If the last write is still running, a later Flush writes the newer lines.
        if (!dirty || Interlocked.CompareExchange(ref writing, 1, 0) != 0) return;
        dirty = false;
        string[] snapshot = [.. lines];
        write = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllLines(path, snapshot);
            }
            catch (IOException) { /* Diagnostics must not interrupt the viewer. */ }
            catch (UnauthorizedAccessException) { }
            finally { Interlocked.Exchange(ref writing, 0); }
        });
    }

    /// <summary>Writes the last lines (why it stopped) before the app exits.</summary>
    public async Task CloseAsync()
    {
        await write.ConfigureAwait(false);
        Flush();
        await write.ConfigureAwait(false);
    }
}
