namespace AVRDUDEPROG2.Models;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; }
    public string SelectedDeviceId { get; set; } = "usb647";
    public string SelectedProgrammerId { get; set; } = "usbasp";
    public string Port { get; set; } = "usb";
    public string FlashFile { get; set; } = "";
    public string EepromFile { get; set; } = "";
    public double WindowWidth { get; set; } = 1060;
    public double WindowHeight { get; set; } = 700;
    public List<ProgrammingProfile> Profiles { get; set; } = [];
}

public sealed class ProgrammingProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string ProgrammerId { get; set; } = "";
    public string Port { get; set; } = "";
    public string FlashFile { get; set; } = "";
    public string EepromFile { get; set; } = "";
    public bool WriteFlash { get; set; } = true;
    public bool WriteEeprom { get; set; }
    public bool WriteFuses { get; set; }
    public Dictionary<string, byte> FuseValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public override string ToString() => Name;
}

public sealed record ProgrammerDefinition(string DisplayName, string Id, string DefaultPort, bool PortEnabled)
{
    public override string ToString() => DisplayName;
}

public sealed record AvrdudeRunResult(int ExitCode, string Output, IReadOnlyList<string> Arguments)
{
    public bool Success => ExitCode == 0;
}
