using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AVRDUDEPROG2.Models;

namespace AVRDUDEPROG2.Services;

public sealed class AvrdudeService
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public string ExecutablePath { get; } = Path.Combine(AppContext.BaseDirectory, "Tools", "avrdude.exe");
    public string ConfigPath { get; } = Path.Combine(AppContext.BaseDirectory, "Tools", "avrdude.conf");
    public bool IsRunning { get; private set; }
    public event EventHandler<string>? OutputReceived;
    public event EventHandler<string>? ProgressReceived;

    // AVRDUDE detects that stderr isn't a TTY (always true once redirected into a pipe) and
    // switches to update_progress_no_tty(): it prints "Reading | " once, then appends '#'/'-'
    // characters one at a time with NO \r or \n between them, only closing the line with
    // " | 100% ...\n" at the very end. So a line-boundary match alone is not enough; we also
    // match the still-growing, unterminated line so the bar can be shown as it fills in.
    private static readonly Regex ProgressPattern = new(
        @"^\s*(?:Reading|Writing)\s*\|",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public IReadOnlyList<string> BuildArguments(
        DeviceDefinition device,
        ProgrammerDefinition programmer,
        string port,
        IEnumerable<string> operationArguments)
    {
        var arguments = new List<string>
        {
            "-C", ConfigPath,
            "-p", device.AvrdudeId,
            "-c", programmer.Id,
            // Disable AVRDUDE 6.1 safemode: it can silently restore fuses. This app performs
            // explicit read-only pre/post checks and never writes a snapshot automatically.
            "-u"
        };
        if (!string.IsNullOrWhiteSpace(port))
        {
            arguments.Add("-P");
            arguments.Add(port.Trim());
        }
        arguments.AddRange(operationArguments);
        return arguments;
    }

    public async Task<AvrdudeRunResult> RunAsync(
        DeviceDefinition device,
        ProgrammerDefinition programmer,
        string port,
        IEnumerable<string> operationArguments,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ExecutablePath) || !File.Exists(ConfigPath))
            throw new FileNotFoundException("Не найдены Tools\\avrdude.exe или Tools\\avrdude.conf. Пересоберите приложение.");

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            IsRunning = true;
            var arguments = BuildArguments(device, programmer, port, operationArguments).ToList();

            var commandPreview = $"> avrdude {string.Join(' ', arguments.Select(QuoteForDisplay))}";
            OutputReceived?.Invoke(this, commandPreview);

            var startInfo = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var output = new StringBuilder();

            if (!process.Start())
                throw new InvalidOperationException("Не удалось запустить AVRDUDE.");

            // AVRDUDE redraws its Reading/Writing bar with bare carriage returns.
            // Reading the streams ourselves preserves those intermediate updates;
            // Process.*DataReceived can defer or merge them into a single line.
            var standardOutputTask = PumpOutputAsync(process.StandardOutput, cancellationToken);
            var standardErrorTask = PumpOutputAsync(process.StandardError, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(standardOutputTask, standardErrorTask);

            return new AvrdudeRunResult(process.ExitCode, output.ToString(), arguments);

            async Task PumpOutputAsync(StreamReader reader, CancellationToken token)
            {
                var parser = new AvrdudeStreamParser();
                var buffer = new char[1024];
                while (true)
                {
                    var count = await reader.ReadAsync(buffer.AsMemory(), token);
                    if (count == 0)
                        break;
                    string? pending;
                    foreach (var line in parser.Append(buffer.AsSpan(0, count), out pending))
                        AppendLine(line);
                    if (pending is not null && ProgressPattern.IsMatch(pending))
                        ProgressReceived?.Invoke(this, pending.Trim());
                }

                var finalLine = parser.Complete();
                if (finalLine is not null)
                    AppendLine(finalLine);
            }

            void AppendLine(string line)
            {
                lock (output)
                    output.AppendLine(line);
                if (ProgressPattern.IsMatch(line))
                    ProgressReceived?.Invoke(this, line.Trim());
                else
                    OutputReceived?.Invoke(this, line);
            }
        }
        finally
        {
            IsRunning = false;
            _operationLock.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, byte>> ReadFuseBytesAsync(
        DeviceDefinition device,
        ProgrammerDefinition programmer,
        string port,
        bool includeLock,
        CancellationToken cancellationToken = default)
    {
        var definitions = device.FuseBytes
            .Where(definition => includeLock || !definition.MemoryName.Equals("lock", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (definitions.Length == 0)
            return new Dictionary<string, byte>();

        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "AVRDUDEPROG2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var arguments = new List<string>();
            foreach (var definition in definitions)
            {
                arguments.Add("-U");
                arguments.Add($"{definition.MemoryName}:r:{Path.Combine(temporaryDirectory, definition.MemoryName + ".bin")}:r");
            }

            var result = await RunAsync(device, programmer, port, arguments, cancellationToken);
            if (!result.Success)
                throw new InvalidOperationException("AVRDUDE не смог прочитать fuse-байты. Подробности находятся в журнале.");

            var values = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in definitions)
            {
                var path = Path.Combine(temporaryDirectory, definition.MemoryName + ".bin");
                var data = await File.ReadAllBytesAsync(path, cancellationToken);
                if (data.Length < 1)
                    throw new InvalidDataException($"AVRDUDE вернул пустое значение для {definition.MemoryName}.");
                values[definition.MemoryName] = data[0];
            }
            return values;
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryDirectory, true);
            }
            catch
            {
                // Temporary diagnostic files can safely be removed by the OS later.
            }
        }
    }

    public async Task<IReadOnlyList<byte>> ReadCalibrationBytesAsync(
        DeviceDefinition device,
        ProgrammerDefinition programmer,
        string port,
        CancellationToken cancellationToken = default)
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "AVRDUDEPROG2", Guid.NewGuid().ToString("N"));
        var temporaryFile = Path.Combine(temporaryDirectory, "calibration.bin");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var result = await RunAsync(device, programmer, port,
                ["-U", $"calibration:r:{temporaryFile}:r"], cancellationToken);
            if (!result.Success)
                throw new InvalidOperationException("Калибровочную ячейку прочитать не удалось. Эта память поддерживается не всеми МК.");

            var data = await File.ReadAllBytesAsync(temporaryFile, cancellationToken);
            if (data.Length < 1)
                throw new InvalidDataException("AVRDUDE вернул пустое значение калибровочной ячейки.");

            return data;
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryDirectory, true);
            }
            catch
            {
                // Temporary diagnostic files can safely be removed by the OS later.
            }
        }
    }

    private static string QuoteForDisplay(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
}

internal sealed class AvrdudeStreamParser
{
    private readonly StringBuilder _line = new();
    private bool _previousWasCarriageReturn;

    public IReadOnlyList<string> Append(ReadOnlySpan<char> text, out string? pending)
    {
        var lines = new List<string>();
        foreach (var character in text)
        {
            switch (character)
            {
                case '\r':
                    Flush(lines);
                    _previousWasCarriageReturn = true;
                    break;
                case '\n':
                    if (!_previousWasCarriageReturn)
                        Flush(lines);
                    _previousWasCarriageReturn = false;
                    break;
                case '\b':
                    if (_line.Length > 0)
                        _line.Length--;
                    _previousWasCarriageReturn = false;
                    break;
                case '\0':
                    break;
                default:
                    _line.Append(character);
                    _previousWasCarriageReturn = false;
                    break;
            }
        }
        pending = _line.Length > 0 ? _line.ToString() : null;
        return lines;
    }

    public string? Complete()
    {
        if (_line.Length == 0)
            return null;
        var line = _line.ToString();
        _line.Clear();
        return line;
    }

    private void Flush(List<string> lines)
    {
        if (_line.Length == 0)
            return;
        lines.Add(_line.ToString());
        _line.Clear();
    }
}
