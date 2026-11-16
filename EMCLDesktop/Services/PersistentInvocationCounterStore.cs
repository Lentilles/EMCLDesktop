using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using EMCL.Protocol.Security;

namespace EMCLDesktop.Services;

/// <summary>Reserves counters durably before EMCL uses them, across processes and restarts.</summary>
public sealed class PersistentInvocationCounterStore(string directory, uint initialOutgoingCounter) : IInvocationCounterStore
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EMCLDesktop", "security");

    public uint Reserve(string scope) => Update(values =>
    {
        uint next = Math.Max(values.GetValueOrDefault(scope), initialOutgoingCounter);
        if (next == uint.MaxValue) throw new CryptographicException("Счётчик сообщений исчерпан.");
        values[scope] = next + 1;
        return next;
    });

    public bool TryAccept(string scope, uint counter) => Update(values =>
    {
        if (counter == uint.MaxValue || counter < values.GetValueOrDefault(scope)) return false;
        values[scope] = counter + 1;
        return true;
    });

    private T Update<T>(Func<Dictionary<string, uint>, T> update)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "invocation-counters.json");
        using var fileLock = AcquireLock(Path.Combine(directory, "invocation-counters.lock"));
        var values = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, uint>>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Файл счётчиков пуст или повреждён.")
            : new Dictionary<string, uint>(StringComparer.Ordinal);
        T result = update(values);
        string temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, values);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return result;
    }

    private static FileStream AcquireLock(string path)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(20); }
        }
    }
}
