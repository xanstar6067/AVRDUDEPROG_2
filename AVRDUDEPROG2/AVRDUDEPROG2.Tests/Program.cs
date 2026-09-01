using AVRDUDEPROG2.Models;
using AVRDUDEPROG2.Services;
using System.IO;
using System.Threading;

var repositoryRoot = args.Length > 0 ? Path.GetFullPath(args[0]) : Environment.CurrentDirectory;
var legacyDirectory = Path.Combine(repositoryRoot, "references", "avrdudeprog33");
var ini = new LegacyIniService();
var devices = ini.LoadDevices(Path.Combine(legacyDirectory, "atmel.ini"), Path.Combine(legacyDirectory, "avrdude.conf"));
var programmers = ini.LoadProgrammers(Path.Combine(legacyDirectory, "programm.ini"));

Assert(devices.Count == 72, $"Ожидалось 72 устройства, получено {devices.Count}");
Assert(programmers.Count == 11, $"Ожидалось 11 программаторов, получено {programmers.Count}");
Assert(devices.Any(device => device.AvrdudeId == "usb647" && device.DisplayName == "AT90USB647"), "AT90USB647 не загружен");
Assert(programmers.Any(programmer => programmer.Id == "usbasp" && programmer.DefaultPort == "usb"), "USBasp не загружен");

var mega8 = devices.Single(device => device.AvrdudeId == "m8");
Assert(mega8.FuseBytes.Select(item => item.MemoryName).SequenceEqual(["lfuse", "hfuse", "lock"]),
    "ATmega8 должен иметь lfuse/hfuse/lock без несуществующего efuse");
var tiny13 = devices.Single(device => device.AvrdudeId == "t13");
Assert(!tiny13.FuseBytes.Any(item => item.MemoryName == "efuse"), "ATtiny13 не должен показывать несуществующий efuse");
var usb647 = devices.Single(device => device.AvrdudeId == "usb647");
Assert(usb647.FuseBytes.Any(item => item.MemoryName == "efuse"), "AT90USB647 efuse не распознан");

var lowState = new FuseByteState(tiny13.FuseBytes.Single(item => item.MemoryName == "lfuse"));
var oldValue = lowState.Value;
var editableBit = lowState.Bits.First(bit => bit.IsEditable);
editableBit.IsRawOne = !editableBit.IsRawOne;
Assert(lowState.Value != oldValue, "Изменение raw fuse-флага не изменило байт");
lowState.SetDeviceValue(0x5A);
lowState.ApplyEditableValue(0xFF);
Assert(lowState.ReadOnlyBitsMatchDevice, "Профиль изменил недоступные fuse-биты");

var temporary = Path.Combine(Path.GetTempPath(), "AVRDUDEPROG2-selftest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
try
{
    var files = new FirmwareFileService();
    var a90 = Path.Combine(temporary, "firmware.a90");
    File.WriteAllText(a90, ":020000000102FB\n:00000001FF\n");
    Assert(files.Inspect(a90).Format == FirmwareFormat.IntelHex, ".a90 не распознан как Intel HEX");

    var srec = Path.Combine(temporary, "firmware.s19");
    File.WriteAllText(srec, "S10500000102F7\nS9030000FC\n");
    Assert(files.Inspect(srec).Format == FirmwareFormat.MotorolaSRecord, "S-Record не распознан");

    var elf = Path.Combine(temporary, "firmware.elf");
    File.WriteAllBytes(elf, [0x7F, (byte)'E', (byte)'L', (byte)'F', 1, 1, 1, 0]);
    Assert(files.Inspect(elf).Format == FirmwareFormat.Elf, "ELF не распознан");

    var binary = Path.Combine(temporary, "firmware.bin");
    File.WriteAllBytes(binary, [0x00, 0xA5, 0xFF]);
    Assert(files.Inspect(binary).Format == FirmwareFormat.RawBinary, "Raw binary не распознан");
}
finally
{
    Directory.Delete(temporary, true);
}

var avrdude = new AvrdudeService();
var arguments = avrdude.BuildArguments(
    devices.Single(device => device.AvrdudeId == "usb647"),
    programmers.Single(programmer => programmer.Id == "usbasp"),
    "usb",
    ["-U", "flash:w:C:\\firmware.hex:i"]);
Assert(arguments.Contains("-u"), "Safemode с возможной скрытой записью не отключён");
Assert(arguments.Count(argument => argument.Contains("fuse", StringComparison.OrdinalIgnoreCase)) == 0,
    "Обычная Flash-команда неожиданно содержит fuse-операцию");
Assert(arguments.Contains("flash:w:C:\\firmware.hex:i"), "Путь Windows повреждён при формировании аргументов");

var streamParser = new AvrdudeStreamParser();
var progressLines = new List<string>();
progressLines.AddRange(streamParser.Append("\rWriting | ####", out var ttyPending1));
Assert(ttyPending1 == "Writing | ####", "Незавершённая TTY-строка прогресса должна отдаваться как pending");
progressLines.AddRange(streamParser.Append(" | 10% 0.10s\rWriting | ######## | 20% 0.20s\r\n", out var ttyPending2));
Assert(ttyPending2 is null, "После \\r/\\n строка должна считаться завершённой, а не pending");
Assert(progressLines.SequenceEqual([
    "Writing | #### | 10% 0.10s",
    "Writing | ######## | 20% 0.20s"
]), "Прогресс AVRDUDE с возвратами каретки разбирается неверно");

// AVRDUDE переключается на update_progress_no_tty(), когда stderr не консоль (всегда так при
// перенаправлении в pipe для .NET Process): бар растёт символами '#' без \r/\n между ними, и
// завершается только в самом конце операции. pending должен отражать этот рост, иначе прогресс
// не появится в интерфейсе до самого конца операции.
var noTtyParser = new AvrdudeStreamParser();
var noTtyLines = new List<string>();
noTtyLines.AddRange(noTtyParser.Append("Writing | ", out var noTtyPending1));
Assert(noTtyPending1 == "Writing | ", "Заголовок no-tty прогресса должен быть виден сразу");
noTtyLines.AddRange(noTtyParser.Append("##", out var noTtyPending2));
Assert(noTtyPending2 == "Writing | ##", "Растущий no-tty прогресс (без \\r/\\n) должен отражаться в pending");
noTtyLines.AddRange(noTtyParser.Append("### | 100% 0.42s\n", out var noTtyPendingFinal));
Assert(noTtyPendingFinal is null, "После завершающего \\n строка должна считаться завершённой");
Assert(noTtyLines.SequenceEqual(["Writing | ##### | 100% 0.42s"]),
    "Финальная no-tty строка прогресса разобрана неверно");

Console.WriteLine($"OK: {devices.Count} устройств, {programmers.Count} программаторов, форматы и fuse-защита проверены.");
var renderIndex = Array.IndexOf(args, "--render");
if (renderIndex >= 0 && renderIndex + 1 < args.Length)
{
    var tabIndex = Array.IndexOf(args, "--tab");
    var tab = tabIndex >= 0 && tabIndex + 1 < args.Length && int.TryParse(args[tabIndex + 1], out var selectedTab) ? selectedTab : 0;
    RenderWindow(Path.GetFullPath(args[renderIndex + 1]), tab);
}
return;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void RenderWindow(string path, int tab)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var application = new AVRDUDEPROG2.App();
            application.InitializeComponent();
            var window = new AVRDUDEPROG2.MainWindow
            {
                Width = 1060,
                Height = 700,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = -2000,
                Top = -2000
            };
            if (window.FindName("ContentTabs") is System.Windows.Controls.TabControl tabs)
                tabs.SelectedIndex = tab;
            window.Show();
            window.UpdateLayout();
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output = File.Create(path);
            encoder.Save(output);
            window.Close();
            application.Shutdown();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null)
        throw new InvalidOperationException("Не удалось отрисовать UI preview.", failure);
    Console.WriteLine("UI preview: " + path);
}
