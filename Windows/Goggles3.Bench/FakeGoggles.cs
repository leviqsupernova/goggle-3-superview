using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Goggles3.Core;

namespace Goggles3.Bench;

/// <summary>Faults the fake goggles inject. Percentages are 0-100, times in seconds, 0 = off.</summary>
public sealed record FakeOptions
{
    public double Fps { get; init; } = 60;
    public int PartSize { get; init; } = 1400;
    public double LossPercent { get; init; }
    public double ReorderPercent { get; init; }
    public double StallEvery { get; init; }
    public double StallFor { get; init; }
    /// <summary>After a stall, send nothing until a new handshake (models a LiveView restart).</summary>
    public bool ResubscribeAfterStall { get; init; }
    /// <summary>Port stays closed at start, so the viewer's handshakes get ICMP port unreachable.</summary>
    public double ClosedFor { get; init; }
    public bool Loop { get; init; } = true;
    public int Seed { get; init; } = 1;
}

public sealed record FakeStats(long Handshakes, long Frames, long Packets, long Dropped, long Acks, long Stalls);

/// <summary>
/// Goggles side of the LiveView UDP session, written from the receiver's view of the protocol:
/// answers the handshake, sends one H.264 access unit per frame as type-2 parts, counts ACKs.
/// Real goggles behaviour beyond what LiveProtocol checks (retransmits, unit size) is unknown.
/// </summary>
public sealed class FakeGoggles(FakeOptions options)
{
    private long handshakes, frames, packets, dropped, acks, stalls;
    private volatile IPEndPoint? viewer;
    private volatile bool subscribed;
    private int session;

    public FakeStats Stats => new(Interlocked.Read(ref handshakes), Interlocked.Read(ref frames), Interlocked.Read(ref packets),
        Interlocked.Read(ref dropped), Interlocked.Read(ref acks), Interlocked.Read(ref stalls));

    public async Task RunAsync(byte[] h264, IPEndPoint listen, CancellationToken token)
    {
        List<ReadOnlyMemory<byte>> units = SplitAccessUnits(h264);
        if (units.Count == 0) throw new InvalidDataException("No H.264 NAL units found in the file.");
        if (options.ClosedFor > 0) await Task.Delay(TimeSpan.FromSeconds(options.ClosedFor), token).ConfigureAwait(false);
        using var udp = new UdpClient(listen);
        Task receiving = ReceiveLoopAsync(udp, token);
        try { await SendLoopAsync(udp, units, token).ConfigureAwait(false); }
        finally
        {
            udp.Close();
            try { await receiving.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private async Task ReceiveLoopAsync(UdpClient udp, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try { datagram = await udp.ReceiveAsync(token).ConfigureAwait(false); }
            catch (SocketException) { continue; } // Windows reports ICMP errors on unconnected sockets too.
            catch (ObjectDisposedException) { return; }
            byte[] m = datagram.Buffer;
            if (m.Length < 8 || m[7] != LiveProtocol.HeaderXor(m)) continue;
            if (m[6] == 0)
            {
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(m.AsSpan(2));
                Volatile.Write(ref session, id);
                viewer = datagram.RemoteEndPoint;
                subscribed = true;
                Interlocked.Increment(ref handshakes);
                byte[] confirm = [0x08, 0x80, (byte)id, (byte)(id >> 8), 0, 0, 0, 0];
                confirm[7] = LiveProtocol.HeaderXor(confirm);
                await udp.SendAsync(confirm, datagram.RemoteEndPoint, token).ConfigureAwait(false);
            }
            else if (m[6] == 4) Interlocked.Increment(ref acks);
        }
    }

    private async Task SendLoopAsync(UdpClient udp, List<ReadOnlyMemory<byte>> units, CancellationToken token)
    {
        var random = new Random(options.Seed);
        var clock = Stopwatch.StartNew();
        double start = 0, nextStall = options.StallEvery;
        long frame = 0; int index = 0; ushort sequence = 0;
        byte[]? held = null;
        while (!token.IsCancellationRequested)
        {
            if (!subscribed)
            {
                await Task.Delay(10, token).ConfigureAwait(false);
                start = clock.Elapsed.TotalSeconds; frame = 0;
                continue;
            }
            if (options.StallEvery > 0 && clock.Elapsed.TotalSeconds >= nextStall)
            {
                Interlocked.Increment(ref stalls);
                await Task.Delay(TimeSpan.FromSeconds(options.StallFor), token).ConfigureAwait(false);
                if (options.ResubscribeAfterStall) subscribed = false;
                nextStall = clock.Elapsed.TotalSeconds + options.StallEvery;
                start = clock.Elapsed.TotalSeconds; frame = 0;
                continue;
            }
            double due = start + frame / options.Fps - clock.Elapsed.TotalSeconds;
            if (due > 0) await Task.Delay(TimeSpan.FromSeconds(due), token).ConfigureAwait(false);
            IPEndPoint target = viewer!;
            foreach (byte[] packet in Packetize(units[index].Span, (ushort)Volatile.Read(ref session), ref sequence, options.PartSize))
            {
                Interlocked.Increment(ref packets);
                if (random.NextDouble() * 100 < options.LossPercent) { Interlocked.Increment(ref dropped); continue; }
                if (held == null && random.NextDouble() * 100 < options.ReorderPercent) { held = packet; continue; }
                await udp.SendAsync(packet, target, token).ConfigureAwait(false);
                if (held != null) { await udp.SendAsync(held, target, token).ConfigureAwait(false); held = null; }
            }
            Interlocked.Increment(ref frames); frame++;
            if (++index == units.Count) { if (!options.Loop) return; index = 0; }
        }
    }

    /// <summary>Splits an Annex-B stream into access units (one per frame), parameter sets stay with the next frame.</summary>
    public static List<ReadOnlyMemory<byte>> SplitAccessUnits(byte[] stream)
    {
        var nals = new List<(int Begin, int Header)>();
        for (int i = 0; i + 3 < stream.Length; i++)
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 1)
            {
                nals.Add((i > 0 && stream[i - 1] == 0 ? i - 1 : i, i + 3));
                i += 2;
            }
        var units = new List<ReadOnlyMemory<byte>>();
        int unitStart = 0; bool hasPicture = false;
        foreach ((int begin, int header) in nals)
        {
            int type = stream[header] & 0x1f;
            bool picture = type is 1 or 5;
            // first_mb_in_slice == 0 (ue(v) "1" bit) starts a new picture.
            bool firstSlice = picture && header + 1 < stream.Length && (stream[header + 1] & 0x80) != 0;
            if (hasPicture && (type is 6 or 7 or 8 or 9 || firstSlice))
            {
                units.Add(stream.AsMemory(unitStart, begin - unitStart));
                unitStart = begin; hasPicture = false;
            }
            hasPicture |= picture;
        }
        if (nals.Count > 0) units.Add(stream.AsMemory(unitStart));
        return units;
    }

    /// <summary>Type-2 parts as LiveProtocol expects them; a frame over 127 parts is sent as several units.</summary>
    public static List<byte[]> Packetize(ReadOnlySpan<byte> frame, ushort session, ref ushort sequence, int partSize)
    {
        var result = new List<byte[]>();
        int maxUnit = 127 * partSize;
        for (int offset = 0; offset < frame.Length; offset += maxUnit)
        {
            ReadOnlySpan<byte> unit = frame.Slice(offset, Math.Min(maxUnit, frame.Length - offset));
            int count = (unit.Length + partSize - 1) / partSize;
            for (int part = 0; part < count; part++)
            {
                ReadOnlySpan<byte> payload = unit.Slice(part * partSize, Math.Min(partSize, unit.Length - part * partSize));
                var packet = new byte[20 + payload.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)(packet.Length | 0x8000));
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), session);
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), unchecked((ushort)(sequence + part * 8)));
                packet[6] = 2;
                packet[17] = (byte)(count | ((part & 1) << 7));
                packet[18] = (byte)(part >> 1);
                packet[19] = (byte)(part >> 9);
                payload.CopyTo(packet.AsSpan(20));
                packet[7] = LiveProtocol.HeaderXor(packet);
                result.Add(packet);
            }
            sequence = unchecked((ushort)(sequence + count * 8));
        }
        return result;
    }
}
