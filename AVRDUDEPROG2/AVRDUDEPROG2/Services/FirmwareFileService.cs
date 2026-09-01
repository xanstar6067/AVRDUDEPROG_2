namespace AVRDUDEPROG2.Services;

public enum FirmwareFormat
{
    IntelHex,
    MotorolaSRecord,
    Elf,
    RawBinary,
    NumericByteList
}

public sealed record FirmwareFileInfo(string Path, FirmwareFormat Format, string FormatName, char AvrdudeSpecifier);

public sealed class FirmwareFileService
{
    public const string OpenFileFilter =
        "Все файлы прошивок|*.hex;*.ihex;*.ihx;*.a90;*.eep;*.eeprom;*.srec;*.s19;*.s28;*.s37;*.mot;*.elf;*.axf;*.bin;*.raw;*.rom;*.dat;*.txt|" +
        "Intel HEX (*.hex;*.ihex;*.ihx;*.a90;*.eep)|*.hex;*.ihex;*.ihx;*.a90;*.eep|" +
        "Motorola S-Record (*.srec;*.s19;*.s28;*.s37;*.mot)|*.srec;*.s19;*.s28;*.s37;*.mot|" +
        "ELF (*.elf;*.axf)|*.elf;*.axf|" +
        "Двоичные (*.bin;*.raw;*.rom)|*.bin;*.raw;*.rom|" +
        "Все файлы (*.*)|*.*";

    public FirmwareFileInfo Inspect(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Файл прошивки не найден.", path);

        using var stream = File.OpenRead(path);
        var buffer = new byte[Math.Min(4096, (int)Math.Min(stream.Length, 4096))];
        var read = stream.Read(buffer, 0, buffer.Length);
        if (read == 0)
            throw new InvalidDataException("Файл прошивки пуст.");

        if (read >= 4 && buffer[0] == 0x7F && buffer[1] == (byte)'E' && buffer[2] == (byte)'L' && buffer[3] == (byte)'F')
            return new FirmwareFileInfo(path, FirmwareFormat.Elf, "ELF", 'e');

        var text = System.Text.Encoding.ASCII.GetString(buffer, 0, read).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (LooksLikeIntelHex(text))
            return new FirmwareFileInfo(path, FirmwareFormat.IntelHex, "Intel HEX", 'i');
        if (LooksLikeSRecord(text))
            return new FirmwareFileInfo(path, FirmwareFormat.MotorolaSRecord, "Motorola S-Record", 's');
        if (buffer.Take(read).Any(value => value == 0 || value > 0x7F))
            return new FirmwareFileInfo(path, FirmwareFormat.RawBinary, "Raw binary", 'r');
        if (LooksLikeNumericList(text))
            return new FirmwareFileInfo(path, FirmwareFormat.NumericByteList, "Список байтов", 'a');

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".bin" or ".raw" or ".rom")
            return new FirmwareFileInfo(path, FirmwareFormat.RawBinary, "Raw binary", 'r');

        throw new InvalidDataException(
            "Формат не распознан. Поддерживаются Intel HEX (включая .a90/.eep), Motorola S-Record, AVR ELF, raw binary и текстовые списки байтов.");
    }

    private static bool LooksLikeIntelHex(string text)
    {
        var first = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return first is { Length: >= 11 } && first[0] == ':' && first[1..].All(Uri.IsHexDigit);
    }

    private static bool LooksLikeSRecord(string text)
    {
        var first = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return first is { Length: >= 10 } && first[0] == 'S' && char.IsDigit(first[1]) && first[2..].All(Uri.IsHexDigit);
    }

    private static bool LooksLikeNumericList(string text)
    {
        var tokens = text.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        return tokens.Take(64).All(token =>
        {
            var value = token.Split('#')[0];
            if (value.Length == 0)
                return true;
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return value[2..].Length > 0 && value[2..].All(Uri.IsHexDigit);
            if (value.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                return value[2..].Length > 0 && value[2..].All(character => character is '0' or '1');
            return value.All(char.IsDigit);
        });
    }
}
