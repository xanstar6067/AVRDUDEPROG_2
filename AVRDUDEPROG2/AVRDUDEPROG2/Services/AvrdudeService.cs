using System.Diagnostics;
using System.Text;
using AVRDUDEPROG2.Models;

namespace AVRDUDEPROG2.Services;

public sealed class AvrdudeService
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    public string ExecutablePath { get; } = Path.Combine(AppContext.BaseDirectory, "Tools", "avrdude.exe");
    public string ConfigPath { get; } = Path.Combine(AppContext.BaseDirectory, "Tools", "avrdude.conf");
    public bool IsRunning { get; private set; }
    public event EventHandler<string>? OutputReceived;

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
            process.OutputDataReceived += (_, args) => AppendLine(args.Data);
            process.ErrorDataReceived += (_, args) => AppendLine(args.Data);

            if (!process.Start())
                throw new InvalidOperationException("Не удалось запустить AVRDUDE.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);
            process.WaitForExit();

            return new AvrdudeRunResult(process.ExitCode, output.ToString(), arguments);

            void AppendLine(string? line)
            {
                if (line is null)
                    return;
                lock (output)
                    output.AppendLine(line);
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
