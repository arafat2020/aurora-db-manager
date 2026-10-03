using System.Security.Cryptography;
using System.Text;
using AuroraDbManager.Api.Infrastructure.Backups;

namespace AuroraDbManager.Api.Tests.Backups;

public sealed class Sha256ArtifactHasherTests : IDisposable
{
    private readonly Sha256ArtifactHasher _hasher = new();
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aurora-hasher-tests-{Guid.NewGuid():N}");

    public void Dispose() => File.Delete(_path);

    [Theory]
    // The published SHA-256 test vectors.
    [InlineData("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq", "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1")]
    public async Task KnownInput_ProducesItsSha256_AsLowercaseHex(string input, string expected)
    {
        await File.WriteAllTextAsync(_path, input, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var checksum = await _hasher.ComputeAsync(_path, default);

        Assert.Equal(expected, checksum);
        Assert.Equal(64, checksum.Length);
        Assert.Equal(checksum.ToLowerInvariant(), checksum);
    }

    [Fact]
    public async Task EmptyFile_ProducesTheSha256OfNothing()
    {
        await File.WriteAllBytesAsync(_path, []);

        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            await _hasher.ComputeAsync(_path, default));
    }

    [Fact]
    public async Task BinaryData_IsHashedByteForByte()
    {
        // Every byte value, including zero and what is not valid text.
        var content = Enumerable.Range(0, 4096).Select(i => (byte)(i % 256)).ToArray();
        await File.WriteAllBytesAsync(_path, content);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), await _hasher.ComputeAsync(_path, default));
    }

    [Fact]
    public async Task OneChangedByte_SameSize_ChangesTheChecksum()
    {
        var content = new byte[10_000];
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(_path, content);
        var before = await _hasher.ComputeAsync(_path, default);

        content[5_000] ^= 0x01;
        await File.WriteAllBytesAsync(_path, content);

        Assert.Equal(10_000, new FileInfo(_path).Length);
        Assert.NotEqual(before, await _hasher.ComputeAsync(_path, default));
    }

    [Fact]
    public async Task LargeFile_IsHashedCorrectly_ABlockAtATime()
    {
        // 96 MB, written a block at a time; the expected value is built the same way.
        const int blocks = 96;
        var block = new byte[1024 * 1024];
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var file = File.Create(_path))
        {
            for (var i = 0; i < blocks; i++)
            {
                Array.Fill(block, (byte)i);
                await file.WriteAsync(block);
                expected.AppendData(block);
            }
        }

        Assert.Equal(Convert.ToHexStringLower(expected.GetHashAndReset()), await _hasher.ComputeAsync(_path, default));
    }

    [Fact]
    public async Task ContentLargerThanAGigabyte_IsReadInSmallPieces_NeverAsAWhole()
    {
        // 1.2 GB that exists nowhere: produced as it is read. Had the hasher asked for it all at
        // once, the largest read would show it.
        const long length = 1_200_000_000;
        await using var content = new GeneratedStream(length);

        var checksum = await _hasher.ComputeAsync(content, default);

        Assert.Equal(length, content.BytesRead);
        Assert.InRange(content.LargestRead, 1, 4 * 1024 * 1024);
        Assert.Equal(64, checksum.Length);

        // The same content hashed independently, a megabyte at a time.
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var block = new byte[1_000_000];
        for (long done = 0; done < length; done += block.Length)
        {
            expected.AppendData(block);
        }

        Assert.Equal(Convert.ToHexStringLower(expected.GetHashAndReset()), checksum);
    }

    [Fact]
    public async Task Stream_IsHashedFromItsPositionToItsEnd()
    {
        var content = "header|payload"u8.ToArray();
        using var stream = new MemoryStream(content);
        stream.Position = 7;

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("payload"u8)), await _hasher.ComputeAsync(stream, default));
    }

    [Fact]
    public async Task Cancellation_StopsTheCalculation()
    {
        await File.WriteAllBytesAsync(_path, new byte[8 * 1024 * 1024]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _hasher.ComputeAsync(_path, new CancellationToken(canceled: true)));

        using var cancellation = new CancellationTokenSource();
        await using var slow = new CancellingStream(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _hasher.ComputeAsync(slow, cancellation.Token));
    }

    [Fact]
    public async Task MissingFile_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => _hasher.ComputeAsync(_path, default));
    }

    /// <summary>A read-only stream of zeros of a given length that records how it was read.</summary>
    private sealed class GeneratedStream(long length) : Stream
    {
        public long BytesRead { get; private set; }

        public int LargestRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - BytesRead);
            buffer[..count].Clear();
            BytesRead += count;
            LargestRead = Math.Max(LargestRead, buffer.Length);
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A stream without end that cancels the token after a few reads, like a job being stopped mid-hash.</summary>
    private sealed class CancellingStream(CancellationTokenSource cancellation) : Stream
    {
        private int _reads;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_reads == 3)
            {
                cancellation.Cancel();
            }

            cancellationToken.ThrowIfCancellationRequested();
            buffer.Span.Clear();
            return ValueTask.FromResult(buffer.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
