using System.Net;

namespace Goggles3.Core;

public enum AdapterState { Missing, NeedsSetup, Ready }

/// <summary>Goggles USB adapter as found on this PC; <see cref="Address"/> is set when <see cref="AdapterState.Ready"/>.</summary>
public sealed record Adapter(AdapterState State, IPAddress? Address = null);

/// <summary>
/// Keeps a live session: find the adapter, run a session until it ends, wait, repeat. Never asks anything:
/// every state becomes status text (superflash starts the viewer with --connect and does not watch it).
/// </summary>
public sealed class AutoConnect(
    Func<CancellationToken, Task<Adapter>> find,
    Func<IPAddress, CancellationToken, Task<string>> runSession,
    Action<string> status)
{
    public const string WaitingForGoggles = "Waiting for the goggles · plug in the USB cable";
    public const string NeedsSetup = "USB connection not set up yet · click Connect once to set it up";
    public const string Connecting = "Connecting to the goggles…";

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Runs until cancelled; ends with <see cref="OperationCanceledException"/>.</summary>
    public async Task RunAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                Adapter adapter = await find(token).ConfigureAwait(false);
                if (adapter.State == AdapterState.Missing) status(WaitingForGoggles);
                else if (adapter.State == AdapterState.NeedsSetup) status(NeedsSetup);
                else
                {
                    status(Connecting);
                    string reason = await runSession(adapter.Address!, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    status(reason + " · Reconnecting…");
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // A device problem or a missing decoder: show it and keep trying, the operator may fix it meanwhile.
                status(error.Message + " · Retrying…");
            }
            await Task.Delay(RetryDelay, token).ConfigureAwait(false);
        }
    }
}
