using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nullprice.Carry.Core;

public sealed record CableProgress(
    string Stage,
    string CurrentItem,
    long FilesCompleted,
    long TotalFiles,
    long BytesCompleted,
    long TotalBytes);

public sealed class CableTransferService
{
    public const int DefaultPort = 45873;
    private const string Magic = "CARRY-CABLE-1";
    private const int BufferSize = 1024 * 1024;

    public static IReadOnlyList<string> GetLocalAddresses()
    {
        try
        {
            return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork &&
                                  !IPAddress.IsLoopback(address))
                .Select(address => address.ToString())
                .Distinct()
                .ToArray();
        }
        catch { return []; }
    }

    public async Task SendPackageAsync(
        string packagePath,
        string receiverAddress,
        string code,
        IProgress<CableProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        packagePath = Path.GetFullPath(packagePath);
        if (!File.Exists(Path.Combine(packagePath, MigrationManifest.FileName)))
            throw new InvalidDataException("Choose a completed Carry package.");
        if (!IPAddress.TryParse(receiverAddress.Trim(), out var address))
            throw new ArgumentException("Enter the receiver IP address.", nameof(receiverAddress));
        ValidateCode(code);

        var scan = await Task.Run(() =>
        {
            long count = 0;
            long bytes = 0;
            foreach (var file in Directory.EnumerateFiles(packagePath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                count++;
                bytes += new FileInfo(file).Length;
            }
            return (count, bytes);
        }, cancellationToken).ConfigureAwait(false);

        using var client = new TcpClient();
        await client.ConnectAsync(address, DefaultPort, cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(code.Trim());
        writer.Write(Path.GetFileName(Path.TrimEndingDirectorySeparator(packagePath)));
        writer.Write(scan.count);
        writer.Write(scan.bytes);
        writer.Flush();

        long filesDone = 0;
        long bytesDone = 0;
        var buffer = new byte[BufferSize];
        foreach (var file in Directory.EnumerateFiles(packagePath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(packagePath, file);
            var length = new FileInfo(file).Length;
            writer.Write(relative);
            writer.Write(length);
            writer.Flush();
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                bytesDone += read;
                progress?.Report(new("Sending", relative, filesDone, scan.count,
                    bytesDone, scan.bytes));
            }
            filesDone++;
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new("Complete", packagePath, filesDone, scan.count, bytesDone, scan.bytes));
    }

    public async Task<string> ReceivePackageAsync(
        string destinationRoot,
        string code,
        IProgress<CableProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        destinationRoot = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(destinationRoot);
        ValidateCode(code);
        var listener = new TcpListener(IPAddress.Any, DefaultPort);
        listener.Start(1);
        try
        {
            progress?.Report(new("Waiting", "Waiting for the old laptop…", 0, 0, 0, 0));
            using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadString() != Magic)
                throw new InvalidDataException("The sender is not a compatible Carry app.");
            if (!CryptographicEquals(reader.ReadString(), code.Trim()))
                throw new UnauthorizedAccessException("The transfer code does not match.");
            var packageName = SafeFileName(reader.ReadString());
            var totalFiles = reader.ReadInt64();
            var totalBytes = reader.ReadInt64();
            if (totalFiles < 0 || totalBytes < 0)
                throw new InvalidDataException("Invalid transfer header.");
            var packagePath = UniqueDirectory(Path.Combine(destinationRoot, packageName));
            Directory.CreateDirectory(packagePath);
            long filesDone = 0;
            long bytesDone = 0;
            var buffer = new byte[BufferSize];

            for (long i = 0; i < totalFiles; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = reader.ReadString();
                var length = reader.ReadInt64();
                if (length < 0) throw new InvalidDataException("Invalid file length.");
                var destination = Path.GetFullPath(Path.Combine(packagePath, relative));
                if (!IsInside(packagePath, destination))
                    throw new InvalidDataException("The sender supplied an unsafe path.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var partial = destination + ".carrypart";
                try
                {
                    {
                        await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write,
                            FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        long remaining = length;
                        while (remaining > 0)
                        {
                            var read = await stream.ReadAsync(
                                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                                cancellationToken).ConfigureAwait(false);
                            if (read == 0) throw new EndOfStreamException("The cable connection ended early.");
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                            remaining -= read;
                            bytesDone += read;
                            progress?.Report(new("Receiving", relative, filesDone, totalFiles,
                                bytesDone, totalBytes));
                        }
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    File.Move(partial, destination, true);
                }
                catch
                {
                    try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                    throw;
                }
                filesDone++;
            }
            progress?.Report(new("Complete", packagePath, filesDone, totalFiles, bytesDone, totalBytes));
            return packagePath;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void ValidateCode(string code)
    {
        if (code.Length != 6 || !code.All(char.IsDigit))
            throw new ArgumentException("The transfer code must contain 6 digits.");
    }

    private static bool CryptographicEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string SafeFileName(string value)
    {
        var cleaned = string.Concat(value.Select(ch =>
            Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return string.IsNullOrWhiteSpace(cleaned) ? "Carry-Incoming" : cleaned;
    }

    private static string UniqueDirectory(string preferred)
    {
        if (!Directory.Exists(preferred) && !File.Exists(preferred)) return preferred;
        for (var i = 2; ; i++)
        {
            var candidate = $"{preferred}-{i}";
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
    }

    private static bool IsInside(string parent, string candidate)
    {
        var parentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) +
                         Path.DirectorySeparatorChar;
        var candidatePath = Path.GetFullPath(candidate);
        return candidatePath.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase);
    }
}
