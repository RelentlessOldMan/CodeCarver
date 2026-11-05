using System.Security.Cryptography;

namespace CodeCarver.Core.Util;

/// <summary>A write-only stream that passes what it is given to <paramref name="inner"/> and hashes it (SHA-256) on the way:
/// a file's content hash taken as it is written, with no read back (the output can be on a share).</summary>
public sealed class HashingStream(Stream inner) : Stream
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? hex;

    /// <summary>The SHA-256 of everything written, lower-case hex; the stream takes no more writes once asked.</summary>
    public string Hex => hex ??= Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

    /// <summary>The SHA-256 of <paramref name="bytes"/>, as <see cref="Hex"/> gives it.</summary>
    public static string Of(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (hex is not null) throw new InvalidOperationException("The hash was already taken.");
        hash.AppendData(buffer);
        inner.Write(buffer);
    }
    public override void Flush() => inner.Flush();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { hex ??= Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(); inner.Dispose(); hash.Dispose(); }
        base.Dispose(disposing);
    }
}
