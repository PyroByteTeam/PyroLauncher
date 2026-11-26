using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using PyroLauncher.Core;

namespace PyroLauncher.Lan;

/// <summary>
/// UDP hole punching plus a small reliable, ordered stream protocol on top, so lobby connections can work when
/// neither side can open a port. Both sides learn their public UDP address from STUN, swap it through invite and
/// reply codes, and send packets at each other until both routers let them through.
///
/// Packet: 0xB7, type, 8-byte session id, body. Session ids are random 64-bit values from the codes, so stray or
/// spoofed packets are ignored; everything inside streams is additionally end-to-end encrypted (SecureChannel).
/// </summary>
public sealed class UdpMux : IDisposable
{
    internal const byte Magic = 0xB7;
    internal const byte TPunch = 1, TPunchAck = 2, TData = 3, TAck = 4, TReset = 5, TPing = 6, TBye = 7;
    private const int MaxSessions = 64;

    private readonly Socket _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<ulong, PunchSession> _sessions = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IPEndPoint>> _stunPending = new();
    private readonly bool _acceptUnsolicited;

    /// <summary>Host side: new sessions from unknown peers are accepted when this returns true (ban check).</summary>
    public Func<IPAddress, bool>? AcceptPeer { get; init; }

    /// <summary>Raised on a background thread for each stream a peer opens (host side).</summary>
    public event Action<ReliableStream, PunchSession>? StreamAccepted;

    public int LocalPort { get; }
    public IPEndPoint? PublicEndpoint { get; private set; }
    /// <summary>Two STUN servers saw different ports: the router changes ports per destination (symmetric NAT), punching will likely fail.</summary>
    public bool LikelySymmetricNat { get; private set; }

    public UdpMux(bool acceptUnsolicited)
    {
        _acceptUnsolicited = acceptUnsolicited;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            // Without this, an ICMP "port unreachable" from a peer that is not listening yet kills ReceiveFrom on Windows.
            const int SIO_UDP_CONNRESET = -1744830452;
            try { _socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch { }
        }
        // The default 64 KB buffers overflow on a single burst of world data.
        try { _socket.ReceiveBufferSize = 4 * 1024 * 1024; _socket.SendBufferSize = 1024 * 1024; } catch { }
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        LocalPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        _ = ReceiveLoop(_cts.Token);
        _ = TickLoop(_cts.Token);
    }

    // ---------- STUN ----------

    /// <summary>Learns the public address of this socket. Returns null when no STUN server answers.</summary>
    public async Task<IPEndPoint?> DiscoverAsync(CancellationToken ct)
    {
        var servers = await Stun.ResolveServersAsync(ct);
        var answers = new List<IPEndPoint>();
        var tasks = servers.Select(async server =>
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var req = Stun.Request(out var tid);
                var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
                var key = Convert.ToHexString(tid);
                _stunPending[key] = tcs;
                try
                {
                    _socket.SendTo(req, server);
                    using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    t.CancelAfter(800);
                    return await tcs.Task.WaitAsync(t.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                catch (SocketException) { }
                finally { _stunPending.TryRemove(key, out _); }
            }
            return null;
        }).ToList();
        foreach (var r in await Task.WhenAll(tasks))
            if (r != null) answers.Add(r);
        if (answers.Count == 0) return null;
        PublicEndpoint = answers.GroupBy(a => a.ToString()).OrderByDescending(g => g.Count()).First().First();
        LikelySymmetricNat = answers.Select(a => a.Port).Distinct().Count() > 1;
        return PublicEndpoint;
    }

    // ---------- sessions ----------

    /// <summary>Starts punching towards <paramref name="remote"/> with session id <paramref name="sid"/>.</summary>
    public PunchSession Connect(IPEndPoint remote, ulong sid, bool opener)
    {
        var s = _sessions.GetOrAdd(sid, id => new PunchSession(this, id, remote, opener));
        s.StartPunching(remote);
        return s;
    }

    public static ulong NewSessionId() => BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8)) | 1;

    /// <summary>Fraction of outgoing packets to drop on purpose (0 in the app; only set by tests).</summary>
    internal static double SimulatedLoss { get; set; }

    internal void Send(IPEndPoint to, byte type, ulong sid, ReadOnlySpan<byte> body)
    {
        if (SimulatedLoss > 0 && Random.Shared.NextDouble() < SimulatedLoss) return;
        var buf = new byte[10 + body.Length];
        buf[0] = Magic;
        buf[1] = type;
        BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(2), sid);
        body.CopyTo(buf.AsSpan(10));
        try { _socket.SendTo(buf, to); } catch { }
    }

    internal void Remove(PunchSession s) => _sessions.TryRemove(s.Id, out _);

    internal void RaiseAccepted(ReliableStream stream, PunchSession session) => StreamAccepted?.Invoke(stream, session);

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try { r = await _socket.ReceiveFromAsync(buf, SocketFlags.None, any, ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            var from = (IPEndPoint)r.RemoteEndPoint;
            var data = buf.AsSpan(0, r.ReceivedBytes);
            try
            {
                if (Stun.IsStun(data))
                {
                    var mapped = Stun.ParseResponse(data, out var tid);
                    if (mapped != null && _stunPending.TryGetValue(Convert.ToHexString(tid), out var tcs)) tcs.TrySetResult(mapped);
                    continue;
                }
                if (data.Length < 10 || data[0] != Magic) continue;
                var type = data[1];
                var sid = BinaryPrimitives.ReadUInt64LittleEndian(data[2..]);
                if (!_sessions.TryGetValue(sid, out var session))
                {
                    // A peer that reached us without us punching first (their router let our packets through earlier,
                    // or ours is permissive). Only a host accepts these, and only "hello" packets start a session.
                    if (!_acceptUnsolicited || type != TPunch || _sessions.Count >= MaxSessions) continue;
                    if (AcceptPeer != null && !AcceptPeer(from.Address)) continue;
                    session = _sessions.GetOrAdd(sid, id => new PunchSession(this, id, from, opener: false));
                }
                session.OnPacket(from, type, data[10..]);
            }
            catch (Exception ex) { Log.Info("LAN UDP packet error: " + ex.Message); }
        }
    }

    private async Task TickLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                foreach (var s in _sessions.Values)
                {
                    try { s.Tick(); } catch (Exception ex) { Log.Info("LAN UDP tick error: " + ex.Message); }
                }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        foreach (var s in _sessions.Values) s.Close();
        _cts.Cancel();
        try { _socket.Dispose(); } catch { }
    }
}

/// <summary>One peer reached through hole punching. Carries any number of reliable streams.</summary>
public sealed class PunchSession
{
    private const int MaxStreams = 48;
    private static readonly TimeSpan PunchFor = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(25);

    private readonly UdpMux _mux;
    private readonly bool _opener;
    private readonly ConcurrentDictionary<uint, ReliableStream> _streams = new();
    private readonly TaskCompletionSource _established = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _nextStreamId, _highestRemoteStream;
    private long _lastHeard, _lastSent, _punchUntil, _lastPunch;
    private volatile bool _closed;

    internal PunchSession(UdpMux mux, ulong id, IPEndPoint remote, bool opener)
    {
        _mux = mux;
        Id = id;
        Remote = remote;
        _opener = opener;
        _nextStreamId = opener ? 1u : 2u;
        _lastHeard = Environment.TickCount64;
    }

    public ulong Id { get; }
    /// <summary>Where the peer's packets come from (updated if their router changes the port).</summary>
    public IPEndPoint Remote { get; private set; }
    public bool IsEstablished => _established.Task.IsCompleted;
    public Task Established => _established.Task;
    /// <summary>Raised once when the session dies (peer gone or closed).</summary>
    public event Action? Closed;

    internal void StartPunching(IPEndPoint remote)
    {
        Remote = remote;
        _punchUntil = Environment.TickCount64 + (long)PunchFor.TotalMilliseconds;
        _lastPunch = 0;
    }

    public ReliableStream OpenStream()
    {
        if (_closed) throw new IOException("Session closed");
        var id = Interlocked.Add(ref _nextStreamId, 2) - 2;
        var s = new ReliableStream(this, id);
        _streams[id] = s;
        return s;
    }

    internal void Send(byte type, ReadOnlySpan<byte> body)
    {
        _lastSent = Environment.TickCount64;
        _mux.Send(Remote, type, Id, body);
    }

    internal void OnPacket(IPEndPoint from, byte type, ReadOnlySpan<byte> body)
    {
        if (_closed) return;
        _lastHeard = Environment.TickCount64;
        Remote = from;
        switch (type)
        {
            case UdpMux.TPunch:
                _mux.Send(from, UdpMux.TPunchAck, Id, []);
                _established.TrySetResult();
                break;
            case UdpMux.TPunchAck:
                _established.TrySetResult();
                break;
            case UdpMux.TPing:
                break;
            case UdpMux.TBye:
                Close(sendBye: false);
                break;
            case UdpMux.TData when body.Length >= 9:
            {
                _established.TrySetResult();
                var sid = BinaryPrimitives.ReadUInt32LittleEndian(body);
                if (!_streams.TryGetValue(sid, out var stream))
                {
                    // A new stream from the peer: ids from the other side have the other parity and only go up.
                    var theirs = (sid & 1) == (_opener ? 0u : 1u);
                    if (!theirs || sid <= _highestRemoteStream || _streams.Count >= MaxStreams)
                    {
                        Span<byte> rst = stackalloc byte[4];
                        BinaryPrimitives.WriteUInt32LittleEndian(rst, sid);
                        Send(UdpMux.TReset, rst);
                        return;
                    }
                    _highestRemoteStream = sid;
                    stream = new ReliableStream(this, sid);
                    _streams[sid] = stream;
                    stream.OnData(body[4..]);
                    _mux.RaiseAccepted(stream, this);
                    return;
                }
                stream.OnData(body[4..]);
                break;
            }
            case UdpMux.TAck when body.Length >= 16:
                if (_streams.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(body), out var s2)) s2.OnAck(body[4..]);
                break;
            case UdpMux.TReset when body.Length >= 4:
                if (_streams.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(body), out var s3)) s3.Fail();
                break;
        }
    }

    internal void Tick()
    {
        if (_closed) return;
        var now = Environment.TickCount64;
        if (!IsEstablished)
        {
            if (now < _punchUntil && now - _lastPunch >= 250)
            {
                _lastPunch = now;
                Send(UdpMux.TPunch, []);
            }
            else if (now >= _punchUntil && _punchUntil != 0) Close(sendBye: false);
            return;
        }
        if (now - _lastHeard > IdleTimeout.TotalMilliseconds) { Close(sendBye: false); return; }
        if (now - _lastSent > 4000) Send(UdpMux.TPing, []);
        foreach (var s in _streams.Values) s.Tick(now);
    }

    internal void RemoveStream(ReliableStream s) => _streams.TryRemove(s.Id, out _);

    public void Close() => Close(sendBye: true);

    private void Close(bool sendBye)
    {
        if (_closed) return;
        _closed = true;
        if (sendBye && IsEstablished) Send(UdpMux.TBye, []);
        foreach (var s in _streams.Values) s.Fail();
        _established.TrySetException(new IOException("Hole punching timed out"));
        _mux.Remove(this);
        Closed?.Invoke();
    }
}

/// <summary>
/// Reliable, ordered byte stream over a punch session: numbered segments, selective acknowledgements,
/// retransmission with an RTT-based timeout, and a simple congestion window.
/// </summary>
public sealed class ReliableStream : Stream
{
    private const int SegmentSize = 1150;   // keeps packets under typical internet MTU
    private const int MaxWindow = 512;
    private const int ReceiveWindow = 1024;
    private const byte FlagFin = 1;

    private readonly PunchSession _session;
    private readonly object _lock = new();
    private readonly SortedDictionary<uint, Segment> _unacked = new();
    private readonly Dictionary<uint, (byte[] Data, byte Flags)> _outOfOrder = new();
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _ackSignal = new(0);
    private uint _nextSeq, _expected;
    private double _cwnd = 32, _srtt = -1, _rttVar;
    private long _lastCut;
    private byte[]? _current;
    private int _currentPos;
    private bool _finSent, _failed, _remoteFin;

    private sealed class Segment
    {
        public required byte[] Packet;
        public long SentAt;
        public int Tries;
    }

    internal ReliableStream(PunchSession session, uint id)
    {
        _session = session;
        Id = id;
    }

    public uint Id { get; }

    private double Rto => _srtt < 0 ? 400 : Math.Clamp(_srtt + 4 * _rttVar, 120, 3000);

    // ---------- sending ----------

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        while (!buffer.IsEmpty)
        {
            var chunk = buffer[..Math.Min(SegmentSize, buffer.Length)];
            buffer = buffer[chunk.Length..];
            await SendSegmentAsync(chunk.Span.ToArray(), 0, ct);
        }
    }

    private async Task SendSegmentAsync(byte[] payload, byte flags, CancellationToken ct)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_failed) throw new IOException("Connection lost");
                if (_unacked.Count < (int)_cwnd)
                {
                    var seq = _nextSeq++;
                    var pkt = new byte[9 + payload.Length];
                    BinaryPrimitives.WriteUInt32LittleEndian(pkt, Id);
                    BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(4), seq);
                    pkt[8] = flags;
                    payload.CopyTo(pkt, 9);
                    _unacked[seq] = new Segment { Packet = pkt, SentAt = Environment.TickCount64, Tries = 1 };
                    _session.Send(UdpMux.TData, pkt);
                    return;
                }
            }
            await _ackSignal.WaitAsync(100, ct);
        }
    }

    internal void OnAck(ReadOnlySpan<byte> body)
    {
        var cum = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var bitmap = BinaryPrimitives.ReadUInt64LittleEndian(body[4..]);
        var now = Environment.TickCount64;
        var freed = 0;
        lock (_lock)
        {
            foreach (var seq in _unacked.Keys.ToList())
            {
                var acked = seq < cum || (seq > cum && seq - cum - 1 < 64 && (bitmap & (1UL << (int)(seq - cum - 1))) != 0);
                if (!acked) continue;
                var seg = _unacked[seq];
                if (seg.Tries == 1) UpdateRtt(now - seg.SentAt); // Karn: no samples from retransmissions
                _unacked.Remove(seq);
                freed++;
                _cwnd = _cwnd < 128 ? _cwnd + 1 : _cwnd + 1.0 / _cwnd; // slow start, then gentle growth
                _cwnd = Math.Min(_cwnd, MaxWindow);
            }
        }
        if (freed > 0 && _ackSignal.CurrentCount == 0) _ackSignal.Release();
    }

    private void UpdateRtt(long sample)
    {
        if (_srtt < 0) { _srtt = sample; _rttVar = sample / 2.0; return; }
        _rttVar = 0.75 * _rttVar + 0.25 * Math.Abs(_srtt - sample);
        _srtt = 0.875 * _srtt + 0.125 * sample;
    }

    internal void Tick(long now)
    {
        lock (_lock)
        {
            if (_failed) return;
            // Oldest first, and only a limited number per tick, so recovering from loss does not itself flood the link.
            var resent = 0;
            foreach (var seg in _unacked.Values)
            {
                var timeout = Math.Min(Rto * (1 << Math.Min(seg.Tries - 1, 4)), 4000);
                if (now - seg.SentAt < timeout) continue;
                if (seg.Tries >= 25) { FailLocked(); return; }
                seg.Tries++;
                seg.SentAt = now;
                _session.Send(UdpMux.TData, seg.Packet);
                if (++resent >= 16) break;
            }
            if (resent > 0 && now - _lastCut > Rto)
            {
                // At most one window cut per round trip.
                _lastCut = now;
                _cwnd = Math.Max(8, _cwnd / 2);
            }
            if (_finSent && _remoteFin && _unacked.Count == 0) _session.RemoveStream(this);
        }
    }

    // ---------- receiving ----------

    internal void OnData(ReadOnlySpan<byte> body)
    {
        var seq = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var flags = body[4];
        var data = body[5..].ToArray();
        lock (_lock)
        {
            if (seq >= _expected && seq < _expected + ReceiveWindow && !_outOfOrder.ContainsKey(seq))
            {
                _outOfOrder[seq] = (data, flags);
                while (_outOfOrder.Remove(_expected, out var next))
                {
                    _expected++;
                    if (next.Data.Length > 0) _incoming.Writer.TryWrite(next.Data);
                    if ((next.Flags & FlagFin) != 0) { _remoteFin = true; _incoming.Writer.TryComplete(); }
                }
            }
            // Acknowledge everything we have: cumulative point plus a bitmap of the 64 segments after it.
            ulong bitmap = 0;
            for (var i = 0; i < 64; i++)
                if (_outOfOrder.ContainsKey(_expected + 1 + (uint)i)) bitmap |= 1UL << i;
            Span<byte> ack = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(ack, Id);
            BinaryPrimitives.WriteUInt32LittleEndian(ack[4..], _expected);
            BinaryPrimitives.WriteUInt64LittleEndian(ack[8..], bitmap);
            _session.Send(UdpMux.TAck, ack);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_current == null || _currentPos >= _current.Length)
        {
            try
            {
                if (!await _incoming.Reader.WaitToReadAsync(ct)) { if (_failed) throw new IOException("Connection lost"); return 0; }
            }
            catch (ChannelClosedException) { throw new IOException("Connection lost"); }
            if (!_incoming.Reader.TryRead(out _current)) return 0;
            _currentPos = 0;
        }
        var n = Math.Min(buffer.Length, _current.Length - _currentPos);
        _current.AsMemory(_currentPos, n).CopyTo(buffer);
        _currentPos += n;
        return n;
    }

    // ---------- closing ----------

    internal void Fail()
    {
        lock (_lock) FailLocked();
    }

    private void FailLocked()
    {
        if (_failed) return;
        _failed = true;
        _incoming.Writer.TryComplete(new IOException("Connection lost"));
        _session.RemoveStream(this);
        if (_ackSignal.CurrentCount == 0) _ackSignal.Release();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            bool send;
            lock (_lock) { send = !_finSent && !_failed; _finSent = true; }
            if (send) _ = SendSegmentAsync([], FlagFin, CancellationToken.None).ContinueWith(_ => { });
        }
        base.Dispose(disposing);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override void Flush() { }
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
