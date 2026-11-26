using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PyroLauncher.Lan;

/// <summary>
/// End-to-end encryption for every lobby connection.
///
/// Handshake (no fixed bytes on the wire, so it looks like random data to traffic inspection):
///   client → server: client ephemeral P-256 public key (64 bytes, X||Y)
///   server → client: server identity public key (64) || server ephemeral public key (64)
/// Both sides derive keys with HKDF-SHA256 over ECDH(client eph, server identity) || ECDH(client eph, server eph),
/// salted with a hash of the three public keys. Only the real host can compute the first secret, so a fake host
/// cannot decrypt or produce the first message; the ephemeral keys give forward secrecy.
/// The invite code carries a fingerprint of the host identity key, which the client checks before going on.
///
/// Records: 4-byte big-endian length, then AES-256-GCM ciphertext + 16-byte tag; nonce = record counter.
/// </summary>
public static class SecureChannel
{
    private const int PubLen = 64;
    public const int FingerprintBytes = 16;

    public static ECDiffieHellman NewKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public static byte[] PublicBytes(ECDiffieHellman key)
    {
        var p = key.ExportParameters(false);
        return [.. p.Q.X!, .. p.Q.Y!];
    }

    /// <summary>Short hash of a host identity key, put in invite codes ("hex").</summary>
    public static string Fingerprint(byte[] identityPublic) => Convert.ToHexStringLower(SHA256.HashData(identityPublic)[..FingerprintBytes]);

    public static string Fingerprint(ECDiffieHellman identity) => Fingerprint(PublicBytes(identity));

    private static ECDiffieHellman ImportPublic(byte[] xy)
    {
        // ImportParameters validates that the point is on the curve; garbage is rejected.
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = xy[..32], Y = xy[32..] },
        });
    }

    private static (byte[] C2S, byte[] S2C) DeriveKeys(byte[] dh1, byte[] dh2, byte[] clientEph, byte[] serverId, byte[] serverEph)
    {
        var transcript = SHA256.HashData([.. clientEph, .. serverId, .. serverEph]);
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, [.. dh1, .. dh2], 64, transcript, "PyroLauncher LAN v2"u8.ToArray());
        return (okm[..32], okm[32..]);
    }

    /// <summary>Client side. Throws <see cref="LanException"/> when the host's identity does not match the invite code.</summary>
    public static async Task<SecureStream> ClientAsync(Stream inner, string? expectedFingerprint, CancellationToken ct)
    {
        using var eph = NewKey();
        var ephPub = PublicBytes(eph);
        await inner.WriteAsync(ephPub, ct);
        await inner.FlushAsync(ct);

        var reply = new byte[PubLen * 2];
        await inner.ReadExactlyAsync(reply, ct);
        var serverId = reply[..PubLen];
        var serverEph = reply[PubLen..];
        if (expectedFingerprint != null && !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(Fingerprint(serverId)), System.Text.Encoding.ASCII.GetBytes(expectedFingerprint.ToLowerInvariant())))
            throw new LanException(UI.L.T("The host's identity does not match the invite code. Someone may be pretending to be the host - do not continue."));

        byte[] dh1, dh2;
        try
        {
            using var sId = ImportPublic(serverId);
            using var sEph = ImportPublic(serverEph);
            dh1 = eph.DeriveRawSecretAgreement(sId.PublicKey);
            dh2 = eph.DeriveRawSecretAgreement(sEph.PublicKey);
        }
        catch (CryptographicException) { throw new InvalidDataException("Bad host key"); }
        var (c2s, s2c) = DeriveKeys(dh1, dh2, ephPub, serverId, serverEph);
        return new SecureStream(inner, c2s, s2c) { PeerFingerprint = Fingerprint(serverId) };
    }

    public static async Task<SecureStream> ServerAsync(Stream inner, ECDiffieHellman identity, CancellationToken ct)
    {
        var clientEph = new byte[PubLen];
        await inner.ReadExactlyAsync(clientEph, ct);
        using var eph = NewKey();
        var idPub = PublicBytes(identity);
        var ephPub = PublicBytes(eph);
        await inner.WriteAsync((byte[])[.. idPub, .. ephPub], ct);
        await inner.FlushAsync(ct);

        byte[] dh1, dh2;
        try
        {
            using var cEph = ImportPublic(clientEph);
            dh1 = identity.DeriveRawSecretAgreement(cEph.PublicKey);
            dh2 = eph.DeriveRawSecretAgreement(cEph.PublicKey);
        }
        catch (CryptographicException) { throw new InvalidDataException("Bad client key"); }
        var (c2s, s2c) = DeriveKeys(dh1, dh2, clientEph, idPub, ephPub);
        return new SecureStream(inner, s2c, c2s);
    }
}

/// <summary>AES-256-GCM record layer over another stream. A tampered or replayed record ends the connection.</summary>
public sealed class SecureStream : Stream
{
    private const int MaxPlain = 16 * 1024;
    private const int TagLen = 16;
    private readonly Stream _inner;
    private readonly AesGcm _send, _recv;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private ulong _sendCounter, _recvCounter;
    private byte[] _plain = new byte[MaxPlain];
    private int _pos, _len;
    private readonly byte[] _header = new byte[4];

    public SecureStream(Stream inner, byte[] sendKey, byte[] recvKey)
    {
        _inner = inner;
        _send = new AesGcm(sendKey, TagLen);
        _recv = new AesGcm(recvKey, TagLen);
    }

    /// <summary>Fingerprint of the host identity this connection was made with (client side).</summary>
    public string? PeerFingerprint { get; init; }

    private static void Nonce(Span<byte> nonce, ulong counter)
    {
        nonce.Clear();
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], counter);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_pos >= _len)
        {
            // Record header; a clean end of stream only at a record boundary.
            var got = 0;
            while (got < 4)
            {
                var n = await _inner.ReadAsync(_header.AsMemory(got), ct);
                if (n == 0)
                {
                    if (got == 0) return 0;
                    throw new IOException("Connection closed mid-record");
                }
                got += n;
            }
            var len = (int)BinaryPrimitives.ReadUInt32BigEndian(_header);
            if (len < TagLen || len > MaxPlain + TagLen) throw new InvalidDataException("Bad record length");
            var rec = new byte[len];
            await _inner.ReadExactlyAsync(rec, ct);
            Span<byte> nonce = stackalloc byte[12];
            Nonce(nonce, _recvCounter++);
            var plainLen = len - TagLen;
            try { _recv.Decrypt(nonce, rec.AsSpan(0, plainLen), rec.AsSpan(plainLen), _plain.AsSpan(0, plainLen)); }
            catch (CryptographicException) { throw new InvalidDataException("Record failed authentication"); }
            _pos = 0;
            _len = plainLen;
            if (plainLen == 0) return await ReadAsync(buffer, ct);
        }
        var count = Math.Min(buffer.Length, _len - _pos);
        _plain.AsMemory(_pos, count).CopyTo(buffer);
        _pos += count;
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            while (!buffer.IsEmpty)
            {
                var chunk = buffer[..Math.Min(MaxPlain, buffer.Length)];
                buffer = buffer[chunk.Length..];
                var rec = new byte[4 + chunk.Length + TagLen];
                BinaryPrimitives.WriteUInt32BigEndian(rec, (uint)(chunk.Length + TagLen));
                var nonce = new byte[12];
                Nonce(nonce, _sendCounter++);
                _send.Encrypt(nonce, chunk.Span, rec.AsSpan(4, chunk.Length), rec.AsSpan(4 + chunk.Length, TagLen));
                await _inner.WriteAsync(rec, ct);
            }
            await _inner.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override void Flush() => _inner.Flush();

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _send.Dispose();
            _recv.Dispose();
        }
        base.Dispose(disposing);
    }
}
