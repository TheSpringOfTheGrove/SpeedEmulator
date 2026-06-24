using System.IO;
using System.Text.Json;

namespace SpeedEmulator.Models;

public static class FlowGenerationSeedCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Lazy<FlowGenerationSeedDocument?> SeedDocument = new(LoadSeedDocument);

    public static bool TryCreateSeed(long bankId, out FlowGenerationSnapshot snapshot)
    {
        if (TryCreateBankSeed(bankId, out snapshot))
        {
            return true;
        }

        var document = SeedDocument.Value;
        if (document is null)
        {
            return false;
        }

        if (document.Default is null)
        {
            return false;
        }

        snapshot = Normalize(bankId, document.Default);
        return true;
    }

    public static bool TryCreateBankSeed(long bankId, out FlowGenerationSnapshot snapshot)
    {
        snapshot = new FlowGenerationSnapshot();

        var document = SeedDocument.Value;
        if (document is null)
        {
            return false;
        }

        var bankKey = bankId.ToString();
        if (!document.Banks.TryGetValue(bankKey, out var bankSeed))
        {
            return false;
        }

        snapshot = Normalize(bankId, bankSeed);
        return true;
    }

    private static FlowGenerationSnapshot Normalize(long bankId, FlowGenerationSnapshot source)
    {
        var references = source.References
            .Select((item, index) =>
            {
                var copy = item.Clone();
                copy.BankId = bankId;
                copy.Index = index + 1;
                copy.Id = copy.Id <= 0 ? index + 1 : copy.Id;
                return copy;
            })
            .ToList();

        var constItems = source.ConstItems
            .Select((item, index) =>
            {
                var copy = item.Clone();
                copy.BankId = bankId;
                copy.Index = index + 1;
                copy.Id = copy.Id <= 0 ? index + 1 : copy.Id;
                return copy;
            })
            .ToList();

        return new FlowGenerationSnapshot
        {
            Config = source.Config.Clone(),
            References = references,
            ConstItems = constItems
        };
    }

    private static FlowGenerationSeedDocument? LoadSeedDocument()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "zhencheng-flow-generation-seed.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<FlowGenerationSeedDocument>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private sealed class FlowGenerationSeedDocument
    {
        public FlowGenerationSnapshot? Default { get; set; }

        public Dictionary<string, FlowGenerationSnapshot> Banks { get; set; } = [];
    }
}
