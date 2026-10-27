using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace Osoko.Core.Services;

/// <summary>
/// The temporary cache (<c>%LOCALAPPDATA%\Osoko\Scratch</c>) that backs the shelf.
/// Files live here only until they are saved, dropped or pruned.
/// </summary>
public sealed class ScratchStore
{
    private readonly object _nameGate = new();
    private int _sequence;

    public ScratchStore(string? rootDirectory = null)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? AppPaths.ScratchDirectory
            : Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    /// <summary>Creates the scratch directory if needed.</summary>
    public void EnsureCreated() => Directory.CreateDirectory(RootDirectory);

    /// <summary>True when the path belongs to this scratch store.</summary>
    public bool IsScratchFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootDirectory));
            var candidate = Path.GetFullPath(path);
            return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Produces a unique, timestamped scratch path.</summary>
    public string CreateFilePath(string extension = ".png", DateTimeOffset? timestamp = null)
    {
        if (!extension.StartsWith('.'))
        {
            extension = "." + extension;
        }

        EnsureCreated();
        var stamp = (timestamp ?? DateTimeOffset.Now).ToLocalTime().ToString("yyyyMMdd_HHmmss_fff");

        lock (_nameGate)
        {
            string candidate;
            do
            {
                var suffix = _sequence++ == 0
                    ? string.Empty
                    : $"_{_sequence:D2}";
                candidate = Path.Combine(RootDirectory, $"Osoko_{stamp}{suffix}{extension}");
            }
            while (File.Exists(candidate));

            return candidate;
        }
    }

    /// <summary>Encodes a bitmap as PNG into the scratch folder and returns the path.</summary>
    public string SaveBitmap(BitmapSource bitmap, DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var path = CreateFilePath(".png", timestamp);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            encoder.Save(stream);
        }

        return path;
    }

    /// <summary>Writes raw bytes (PNG/JPEG) into the scratch folder.</summary>
    public string SaveBytes(ReadOnlySpan<byte> bytes, string extension = ".png", DateTimeOffset? timestamp = null)
    {
        var path = CreateFilePath(extension, timestamp);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        return path;
    }

    /// <summary>
    /// Moves an external screenshot into the scratch cache, optionally deleting the
    /// source (used to keep <c>Pictures\Screenshots</c> clean).
    /// </summary>
    public string MoveInto(string sourcePath, bool deleteSource = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        EnsureCreated();

        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".png";
        }

        var target = CreateFilePath(extension);
        try
        {
            if (deleteSource)
            {
                File.Move(sourcePath, target);
            }
            else
            {
                File.Copy(sourcePath, target, overwrite: false);
            }
        }
        catch (IOException)
        {
            // Cross-volume move or a transient lock: fall back to copy + delete.
            TryDelete(target);
            File.Copy(sourcePath, target, overwrite: true);
            if (deleteSource)
            {
                TryDelete(sourcePath);
            }
        }

        return target;
    }

    /// <summary>Copies an external screenshot into the scratch cache, leaving the source untouched.</summary>
    public string CopyInto(string sourcePath) => MoveInto(sourcePath, deleteSource: false);

    /// <summary>Deletes a scratch file; refuses to touch anything outside the cache.</summary>
    public bool DeleteFile(string? path)
    {
        if (!IsScratchFile(path))
        {
            return false;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path!);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Osoko] Could not delete scratch file '{path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Removes files older than <paramref name="maxAge"/>; used on startup.</summary>
    public int CleanupOlderThan(TimeSpan maxAge)
    {
        if (!Directory.Exists(RootDirectory))
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow - maxAge;
        var removed = 0;

        foreach (var file in Directory.EnumerateFiles(RootDirectory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff && DeleteFile(file))
                {
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[Osoko] Scratch cleanup skipped '{file}': {ex.Message}");
            }
        }

        return removed;
    }

    /// <summary>SHA-256 fingerprint of a file's bytes, used to suppress duplicate captures.</summary>
    public static string ComputeFingerprint(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return ComputeFingerprint(stream);
    }

    /// <summary>SHA-256 fingerprint of a byte buffer.</summary>
    public static string ComputeFingerprint(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>SHA-256 fingerprint of an encoded bitmap, without touching disk.</summary>
    public static string ComputeFingerprint(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return ComputeFingerprint(stream.ToArray());
    }

    public override string ToString() => $"Scratch({RootDirectory})";

    private static string ComputeFingerprint(Stream stream)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return new StringBuilder(hash.Length * 2)
            .Append(Convert.ToHexStringLower(hash))
            .ToString();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
