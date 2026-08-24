using System.IO;
using System.Text.Json;
using SpeedEmulator.Models;

namespace SpeedEmulator.Services;

public sealed record StoredFrontToken(string Token, string TokenType);

public interface IFrontTokenStore
{
    void Save(FrontSession session);

    void Clear();
}

public sealed class FrontTokenStore : IFrontTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string storagePath;

    public FrontTokenStore()
    {
        storagePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpeedEmulator",
            "front-session.json");
    }

    public void Save(FrontSession session)
    {
        if (!session.HasToken)
        {
            Clear();
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(storagePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var storedToken = new StoredFrontToken(session.Token, session.TokenType);
            File.WriteAllText(storagePath, JsonSerializer.Serialize(storedToken, JsonOptions));
        }
        catch
        {
            // A local cache failure must not turn a successful backend login into a failed login.
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(storagePath))
            {
                File.Delete(storagePath);
            }
        }
        catch
        {
            // Session cleanup should continue even when the optional cache cannot be deleted.
        }
    }
}
