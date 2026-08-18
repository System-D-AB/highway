namespace Highway.Assurance.Notifications.Mailer;

using System.Collections.Concurrent;

public sealed class ClaimTracker
{
    private readonly string? _claimsDir;
    private readonly ConcurrentDictionary<string, int> _fallbackTracker = new(StringComparer.Ordinal);

    public ClaimTracker(string? claimsDir = null)
    {
        _claimsDir = claimsDir;
        if (!string.IsNullOrEmpty(_claimsDir) && !Directory.Exists(_claimsDir))
        {
            try { Directory.CreateDirectory(_claimsDir); } catch { }
        }
    }

    public int RecordClaim(string cid)
    {
        if (string.IsNullOrEmpty(_claimsDir))
        {
            return _fallbackTracker.AddOrUpdate(cid, 1, (_, c) => c + 1);
        }

        try
        {
            var filePath = Path.Combine(_claimsDir, $"{SanitizeFileName(cid)}.claim");
            for (var retry = 0; retry < 5; retry++)
            {
                try
                {
                    if (!File.Exists(filePath))
                    {
                        using var fs = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        using var writer = new StreamWriter(fs);
                        writer.Write("1");
                        return 1;
                    }

                    using var fsExisting = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    using var reader = new StreamReader(fsExisting);
                    var text = reader.ReadToEnd();
                    var count = int.TryParse(text, out var parsed) ? parsed + 1 : 2;
                    fsExisting.SetLength(0);
                    using var writerExisting = new StreamWriter(fsExisting);
                    writerExisting.Write(count);
                    return count;
                }
                catch (IOException)
                {
                    Thread.Sleep(10);
                }
            }
        }
        catch
        {
            // Fallback to in-memory if disk access fails
        }

        return _fallbackTracker.AddOrUpdate(cid, 1, (_, c) => c + 1);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }
}
