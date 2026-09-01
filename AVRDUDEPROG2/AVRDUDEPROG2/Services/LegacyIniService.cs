using System.Text;
using AVRDUDEPROG2.Models;

namespace AVRDUDEPROG2.Services;

public sealed class LegacyIniService
{
    private static readonly (string Prefix, string Memory, string Display)[] FuseGroups =
    [
        ("lowbyte", "lfuse", "Low fuse"),
        ("highbyte", "hfuse", "High fuse"),
        ("extendedbyte", "efuse", "Extended fuse"),
        ("extendedbyte", "fuse", "Fuse"),
        ("lockbyte", "lock", "Lock byte")
    ];

    static LegacyIniService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public IReadOnlyList<DeviceDefinition> LoadDevices(string path, string avrdudeConfigPath)
    {
        var sections = Parse(path);
        var supportedMemories = ParsePartMemories(avrdudeConfigPath);
        var devices = new List<DeviceDefinition>();

        foreach (var section in sections)
        {
            if (!section.Value.TryGetValue("mcuavrdude", out var id) || string.IsNullOrWhiteSpace(id))
                continue;

            id = id.Trim();
            supportedMemories.TryGetValue(id, out var memories);

            var bytes = new List<FuseByteDefinition>();
            foreach (var (prefix, memory, display) in FuseGroups)
            {
                if (memories is not null && !memories.Contains(memory))
                    continue;
                var bits = new List<FuseBitDefinition>();
                for (var bit = 7; bit >= 0; bit--)
                {
                    var nameKey = $"{prefix}bit{bit}name";
                    var enabledKey = $"{prefix}bit{bit}enabled";
                    var defaultKey = $"{prefix}bit{bit}def";
                    if (!section.Value.TryGetValue(nameKey, out var name))
                        continue;

                    bits.Add(new FuseBitDefinition
                    {
                        Index = bit,
                        Name = string.IsNullOrWhiteSpace(name) ? "NOT USED" : name.Trim(),
                        IsEditable = section.Value.TryGetValue(enabledKey, out var enabled) && enabled.Trim() == "1",
                        DefaultRawValue = !section.Value.TryGetValue(defaultKey, out var defaultValue) || defaultValue.Trim() != "0"
                    });
                }

                if (bits.Count > 0)
                {
                    bytes.Add(new FuseByteDefinition
                    {
                        MemoryName = memory,
                        DisplayName = display,
                        Bits = bits
                    });
                }
            }

            devices.Add(new DeviceDefinition
            {
                DisplayName = section.Key,
                AvrdudeId = id,
                FuseBytes = bytes
            });
        }

        return devices.OrderBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public IReadOnlyList<ProgrammerDefinition> LoadProgrammers(string path)
    {
        var sections = Parse(path);
        return sections
            .Where(section => section.Value.ContainsKey("progisp"))
            .Select(section => new ProgrammerDefinition(
                section.Key,
                section.Value["progisp"].Trim(),
                section.Value.GetValueOrDefault("portprog", "usb").Trim(),
                section.Value.GetValueOrDefault("portenabled", "0").Trim() == "1"))
            .ToArray();
    }

    private static Dictionary<string, Dictionary<string, string>> Parse(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;

        foreach (var originalLine in File.ReadLines(path, Encoding.GetEncoding(1251)))
        {
            var line = originalLine.Trim();
            if (line.Length == 0 || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[name] = current;
                continue;
            }

            if (current is null)
                continue;

            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            var comment = value.IndexOf(';');
            if (comment >= 0)
                value = value[..comment].Trim();
            current[key] = value;
        }

        return result;
    }

    private static Dictionary<string, HashSet<string>> ParsePartMemories(string path)
    {
        var parts = new Dictionary<string, (string? Parent, HashSet<string> Memories)>(StringComparer.OrdinalIgnoreCase);
        string? currentId = null;
        string? currentParent = null;
        HashSet<string>? currentMemories = null;

        foreach (var originalLine in File.ReadLines(path, Encoding.GetEncoding(1251)))
        {
            var line = originalLine.Trim();
            if (line.Equals("part", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("part parent", StringComparison.OrdinalIgnoreCase))
            {
                Commit();
                currentId = null;
                currentParent = line.StartsWith("part parent", StringComparison.OrdinalIgnoreCase)
                    ? QuotedValue(line)
                    : null;
                currentMemories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            if (currentMemories is null)
                continue;

            if (line.StartsWith("id ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("id\t", StringComparison.OrdinalIgnoreCase))
            {
                currentId = QuotedValue(line);
                continue;
            }

            if (line.StartsWith("memory", StringComparison.OrdinalIgnoreCase))
            {
                var memory = QuotedValue(line);
                if (!string.IsNullOrWhiteSpace(memory))
                    currentMemories.Add(memory);
            }
        }

        Commit();
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in parts.Keys)
            Resolve(id, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return result;

        void Commit()
        {
            if (!string.IsNullOrWhiteSpace(currentId) && currentMemories is not null)
                parts[currentId] = (currentParent, currentMemories);
        }

        HashSet<string> Resolve(string id, HashSet<string> chain)
        {
            if (result.TryGetValue(id, out var resolved))
                return resolved;
            if (!parts.TryGetValue(id, out var part) || !chain.Add(id))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var memories = !string.IsNullOrWhiteSpace(part.Parent)
                ? new HashSet<string>(Resolve(part.Parent, chain), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            memories.UnionWith(part.Memories);
            chain.Remove(id);
            result[id] = memories;
            return memories;
        }

        static string? QuotedValue(string line)
        {
            var firstQuote = line.IndexOf('"');
            var lastQuote = line.LastIndexOf('"');
            return firstQuote >= 0 && lastQuote > firstQuote ? line[(firstQuote + 1)..lastQuote] : null;
        }
    }
}
