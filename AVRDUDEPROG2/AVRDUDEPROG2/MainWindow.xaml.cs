using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using AVRDUDEPROG2.Models;
using AVRDUDEPROG2.Services;

namespace AVRDUDEPROG2;

public partial class MainWindow : Window
{
    private readonly LegacyIniService _iniService = new();
    private readonly SettingsService _settingsService = new();
    private readonly FirmwareFileService _firmwareFiles = new();
    private readonly AvrdudeService _avrdude = new();
    private readonly AppSettings _settings;
    private IReadOnlyList<DeviceDefinition> _devices = [];
    private IReadOnlyList<ProgrammerDefinition> _programmers = [];
    private bool _loading = true;
    private bool _isBusy;

    public ObservableCollection<FuseByteState> FuseStates { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _settings = _settingsService.Load();
        _avrdude.OutputReceived += Avrdude_OutputReceived;

        try
        {
            LoadDefinitions();
            RestoreSettings();
            AppendLog("AVRDUDE PRO запущен. Fuse-операции отделены от Flash/EEPROM.");
        }
        catch (Exception exception)
        {
            AppendLog($"ОШИБКА ИНИЦИАЛИЗАЦИИ: {exception.Message}");
            MessageBox.Show(this, exception.Message, "Не удалось загрузить конфигурацию", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadDefinitions()
    {
        var dataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
        _devices = _iniService.LoadDevices(
            Path.Combine(dataDirectory, "atmel.ini"),
            Path.Combine(AppContext.BaseDirectory, "Tools", "avrdude.conf"));
        _programmers = _iniService.LoadProgrammers(Path.Combine(dataDirectory, "programm.ini"));
        if (_devices.Count == 0 || _programmers.Count == 0)
            throw new InvalidDataException("Таблицы микроконтроллеров или программаторов пусты.");

        DeviceCombo.ItemsSource = _devices;
        ProgrammerCombo.ItemsSource = _programmers;
        ProfileCombo.ItemsSource = _settings.Profiles;

        var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "usb" };
        foreach (var port in ReadSerialPorts())
            ports.Add(port);
        PortCombo.ItemsSource = ports.OrderBy(port => port, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void RestoreSettings()
    {
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);
        DeviceCombo.SelectedItem = _devices.FirstOrDefault(device =>
            device.AvrdudeId.Equals(_settings.SelectedDeviceId, StringComparison.OrdinalIgnoreCase)) ?? _devices[0];
        ProgrammerCombo.SelectedItem = _programmers.FirstOrDefault(programmer =>
            programmer.Id.Equals(_settings.SelectedProgrammerId, StringComparison.OrdinalIgnoreCase)) ?? _programmers[0];
        PortCombo.Text = string.IsNullOrWhiteSpace(_settings.Port) ? "usb" : _settings.Port;
        FlashPathBox.Text = _settings.FlashFile;
        EepromPathBox.Text = _settings.EepromFile;
        RefreshFirmwareLabel("flash", FlashPathBox.Text);
        RefreshFirmwareLabel("eeprom", EepromPathBox.Text);
    }

    private static IReadOnlyList<string> ReadSerialPorts()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key is null)
                return [];
            return key.GetValueNames()
                .Select(name => key.GetValue(name))
                .OfType<string>()
                .Where(port => !string.IsNullOrWhiteSpace(port))
                .ToArray();
        }
        catch
        {
            // Registry access is optional; a port can always be entered manually.
            return [];
        }
    }

    private DeviceDefinition SelectedDevice =>
        DeviceCombo.SelectedItem as DeviceDefinition
        ?? throw new InvalidOperationException("Выберите микроконтроллер из списка.");

    private ProgrammerDefinition SelectedProgrammer =>
        ProgrammerCombo.SelectedItem as ProgrammerDefinition
        ?? throw new InvalidOperationException("Выберите программатор из списка.");

    private string SelectedPort => string.IsNullOrWhiteSpace(PortCombo.Text)
        ? SelectedProgrammer.DefaultPort
        : PortCombo.Text.Trim();

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is not DeviceDefinition device)
            return;

        FuseStates.Clear();
        foreach (var definition in device.FuseBytes)
        {
            var state = new FuseByteState(definition);
            state.PropertyChanged += FuseState_PropertyChanged;
            FuseStates.Add(state);
        }
        UpdateFuseWriteAvailability();
        if (!_loading)
        {
            StatusText.Text = $"Выбран {device.DisplayName} ({device.AvrdudeId})";
            SaveSettings();
        }
    }

    private void ProgrammerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProgrammerCombo.SelectedItem is not ProgrammerDefinition programmer)
            return;

        PortCombo.IsEnabled = programmer.PortEnabled;
        if (!_loading)
        {
            PortCombo.Text = programmer.DefaultPort;
            SaveSettings();
        }
    }

    private void FuseState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FuseByteState.Value) or nameof(FuseByteState.IsChanged))
            UpdateFuseWriteAvailability();
    }

    private void UpdateFuseWriteAvailability() =>
        WriteFusesButton.IsEnabled = !_isBusy && FuseStates.Count > 0 &&
                                     FuseStates.All(state => state.HasDeviceValue) &&
                                     FuseStates.Any(state => state.IsChanged);

    private void BrowseFirmware_Click(object sender, RoutedEventArgs e)
    {
        var memory = (sender as FrameworkElement)?.Tag?.ToString() ?? "flash";
        var dialog = new OpenFileDialog { Filter = FirmwareFileService.OpenFileFilter, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
            SetFirmwarePath(memory, dialog.FileName);
    }

    private void FirmwareDrop_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void FirmwareDrop_Drop(object sender, DragEventArgs e)
    {
        var memory = (sender as FrameworkElement)?.Tag?.ToString() ?? "flash";
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length != 1)
        {
            MessageBox.Show(this, "Перетащите ровно один файл прошивки.", "Drag & Drop", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SetFirmwarePath(memory, files[0]);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void SetFirmwarePath(string memory, string path)
    {
        var info = _firmwareFiles.Inspect(path);
        if (memory.Equals("eeprom", StringComparison.OrdinalIgnoreCase) && info.Format == FirmwareFormat.Elf)
            AppendLog("Примечание: для EEPROM из ELF будет использован адресный диапазон, определяемый AVRDUDE.");

        if (memory.Equals("flash", StringComparison.OrdinalIgnoreCase))
            FlashPathBox.Text = path;
        else
            EepromPathBox.Text = path;
        RefreshFirmwareLabel(memory, path);
        StatusText.Text = $"Файл распознан: {info.FormatName}";
    }

    private void FirmwarePathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || sender is not TextBox box)
            return;
        RefreshFirmwareLabel(box.Tag?.ToString() ?? "flash", box.Text);
    }

    private void RefreshFirmwareLabel(string memory, string path)
    {
        var label = memory.Equals("flash", StringComparison.OrdinalIgnoreCase) ? FlashFormatText : EepromFormatText;
        if (string.IsNullOrWhiteSpace(path))
        {
            label.Text = "Перетащите файл сюда или выберите его";
            label.Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush");
            return;
        }

        try
        {
            var info = _firmwareFiles.Inspect(path);
            var size = new FileInfo(path).Length;
            label.Text = $"{info.FormatName} · {FormatSize(size)}";
            label.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
        }
        catch (Exception exception)
        {
            label.Text = exception.Message;
            label.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }
    }

    private async void MemoryAction_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag?.ToString()?.Split(':');
        if (tag is not { Length: 2 })
            return;
        await ExecuteUiOperationAsync("Выполняется операция с памятью…", () => RunMemoryOperationAsync(tag[0], tag[1][0]));
    }

    private async Task RunMemoryOperationAsync(string memory, char operation, bool flashEraseConfirmed = false)
    {
        string path;
        char format;
        if (operation == 'r')
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Intel HEX (*.hex)|*.hex|Motorola S-Record (*.srec)|*.srec|Двоичный файл (*.bin)|*.bin|Все файлы (*.*)|*.*",
                FileName = memory.Equals("flash", StringComparison.OrdinalIgnoreCase) ? "flash.hex" : "eeprom.eep",
                AddExtension = true
            };
            if (dialog.ShowDialog(this) != true)
                return;
            path = dialog.FileName;
            format = OutputFormatForPath(path);
        }
        else
        {
            path = memory.Equals("flash", StringComparison.OrdinalIgnoreCase) ? FlashPathBox.Text.Trim() : EepromPathBox.Text.Trim();
            format = _firmwareFiles.Inspect(path).AvrdudeSpecifier;
        }

        if (operation == 'w' && memory.Equals("flash", StringComparison.OrdinalIgnoreCase) && !flashEraseConfirmed)
        {
            var choice = MessageBox.Show(this,
                "Перед записью Flash AVRDUDE выполнит chip erase. Fuse-биты не изменяются, но lock-биты будут сброшены; EEPROM может стереться, если EESAVE = 1 (не запрограммирован). Продолжить?",
                "Запись Flash", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (choice != MessageBoxResult.Yes)
                return;
        }

        IReadOnlyDictionary<string, byte>? beforeFuses = null;
        if (operation == 'w')
        {
            AppendLog("Защита: читаю fuse-байты перед записью (без lock)…");
            beforeFuses = await _avrdude.ReadFuseBytesAsync(SelectedDevice, SelectedProgrammer, SelectedPort, includeLock: false);
        }

        var result = await _avrdude.RunAsync(
            SelectedDevice,
            SelectedProgrammer,
            SelectedPort,
            ["-U", $"{memory}:{operation}:{path}:{format}"]);
        if (!result.Success)
            throw new InvalidOperationException($"Операция {memory}:{operation} завершилась ошибкой. См. журнал AVRDUDE.");

        if (beforeFuses is not null)
        {
            AppendLog("Защита: повторно читаю fuse-байты и сравниваю…");
            var afterFuses = await _avrdude.ReadFuseBytesAsync(SelectedDevice, SelectedProgrammer, SelectedPort, includeLock: false);
            var changed = beforeFuses
                .Where(pair => !afterFuses.TryGetValue(pair.Key, out var after) || after != pair.Value)
                .Select(pair => $"{pair.Key}: 0x{pair.Value:X2} → {(afterFuses.TryGetValue(pair.Key, out var value) ? $"0x{value:X2}" : "?")}")
                .ToArray();
            if (changed.Length > 0)
            {
                var message = "КРИТИЧЕСКОЕ ПРЕДУПРЕЖДЕНИЕ: fuse-байты изменились после обычной операции:\n" + string.Join("\n", changed) +
                              "\nПриложение ничего не восстанавливало автоматически.";
                AppendLog(message);
                MessageBox.Show(this, message, "Изменение fuse обнаружено", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                AppendLog("Защита: fuse-байты не изменились.");
            }
        }

        StatusText.Text = operation switch
        {
            'w' => $"{memory.ToUpperInvariant()} записана и проверена",
            'v' => $"{memory.ToUpperInvariant()} совпадает с файлом",
            _ => $"{memory.ToUpperInvariant()} прочитана в {path}"
        };
    }

    private async void EraseChip_Click(object sender, RoutedEventArgs e)
    {
        var choice = MessageBox.Show(this,
            "Chip erase стирает Flash, снимает lock-биты и может стереть EEPROM (зависит от EESAVE). Fuse-биты не записываются. Продолжить?",
            "Стереть кристалл", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes)
            return;

        await ExecuteUiOperationAsync("Стирание кристалла…", async () =>
        {
            var before = await _avrdude.ReadFuseBytesAsync(SelectedDevice, SelectedProgrammer, SelectedPort, includeLock: false);
            var result = await _avrdude.RunAsync(SelectedDevice, SelectedProgrammer, SelectedPort, ["-e"]);
            if (!result.Success)
                throw new InvalidOperationException("Chip erase завершился ошибкой. См. журнал AVRDUDE.");
            var after = await _avrdude.ReadFuseBytesAsync(SelectedDevice, SelectedProgrammer, SelectedPort, includeLock: false);
            EnsureFuseSnapshotsMatch(before, after, "после chip erase");
            StatusText.Text = "Кристалл стёрт; fuse-байты не изменились";
        });
    }

    private async void ReadCalibration_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Двоичный файл (*.bin)|*.bin|Все файлы (*.*)|*.*", FileName = "calibration.bin" };
        if (dialog.ShowDialog(this) != true)
            return;

        await ExecuteUiOperationAsync("Чтение калибровочной ячейки…", async () =>
        {
            var result = await _avrdude.RunAsync(SelectedDevice, SelectedProgrammer, SelectedPort,
                ["-U", $"calibration:r:{dialog.FileName}:r"]);
            if (!result.Success)
                throw new InvalidOperationException("Калибровочную ячейку прочитать не удалось. Эта память поддерживается не всеми МК.");
            StatusText.Text = $"Калибровочная ячейка сохранена: {dialog.FileName}";
        });
    }

    private async void ReadFuses_Click(object sender, RoutedEventArgs e) =>
        await ExecuteUiOperationAsync("Чтение fuse и lock…", ReadFusesCoreAsync);

    private async Task ReadFusesCoreAsync()
    {
        var values = await _avrdude.ReadFuseBytesAsync(SelectedDevice, SelectedProgrammer, SelectedPort, includeLock: true);
        foreach (var state in FuseStates)
            if (values.TryGetValue(state.MemoryName, out var value))
                state.SetDeviceValue(value);
        UpdateFuseWriteAvailability();
        StatusText.Text = "Fuse и lock прочитаны с устройства";
        AppendLog("Fuse snapshot: " + string.Join(", ", FuseStates.Select(state => $"{state.MemoryName}={state.HexValue}")));
    }

    private void ResetFuses_Click(object sender, RoutedEventArgs e)
    {
        foreach (var state in FuseStates)
            state.ResetToDeviceValue();
        StatusText.Text = "Изменения отменены";
    }

    private void DefaultFuses_Click(object sender, RoutedEventArgs e)
    {
        var choice = MessageBox.Show(this,
            "Будут подставлены значения по умолчанию из таблицы AVRDUDE_PROG 3.3. Это не запись на устройство. Перед записью обязательно сверьте их с datasheet.",
            "Значения по умолчанию", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (choice != MessageBoxResult.OK)
            return;
        foreach (var state in FuseStates)
            state.ResetToDefaults();
        StatusText.Text = "Подставлены табличные значения по умолчанию (ещё не записаны)";
    }

    private async void VerifyFuses_Click(object sender, RoutedEventArgs e) =>
        await ExecuteUiOperationAsync("Проверка fuse без записи…", VerifyFusesCoreAsync);

    private async Task VerifyFusesCoreAsync()
    {
        if (!FuseStates.All(state => state.HasDeviceValue))
            throw new InvalidOperationException("Сначала прочитайте fuse-биты с устройства.");
        var live = await _avrdude.ReadFuseBytesAsync(SelectedDevice, SelectedProgrammer, SelectedPort, includeLock: true);
        var mismatches = FuseStates
            .Where(state => !live.TryGetValue(state.MemoryName, out var value) || value != state.Value)
            .Select(state => $"{state.MemoryName}: устройство {(live.TryGetValue(state.MemoryName, out var value) ? $"0x{value:X2}" : "?")}, экран {state.HexValue}")
            .ToArray();
        if (mismatches.Length == 0)
        {
            MessageBox.Show(this, "Все fuse и lock совпадают с выбранными значениями. Запись не выполнялась.", "Проверка fuse", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = "Fuse и lock совпадают; запись не выполнялась";
        }
        else
        {
            MessageBox.Show(this, "Есть различия (запись не выполнялась):\n" + string.Join("\n", mismatches), "Проверка fuse", MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "Найдены различия fuse; запись не выполнялась";
        }
    }

    private async void WriteFuses_Click(object sender, RoutedEventArgs e) =>
        await ExecuteUiOperationAsync("Подготовка записи fuse…", WriteFusesCoreAsync);

    private async Task WriteFusesCoreAsync()
    {
        if (!FuseStates.All(state => state.HasDeviceValue))
            throw new InvalidOperationException("Сначала прочитайте fuse и lock с устройства.");
        if (FuseStates.Any(state => !state.ReadOnlyBitsMatchDevice))
            throw new InvalidOperationException("Обнаружена попытка изменить зарезервированный или заблокированный fuse-бит. Запись отменена.");

        var changes = FuseStates.Where(state => state.IsChanged).ToArray();
        if (changes.Length == 0)
            throw new InvalidOperationException("Нет изменений для записи.");

        AppendLog("Защита fuse: повторное чтение непосредственно перед подтверждением…");
        var live = await _avrdude.ReadFuseBytesAsync(SelectedDevice, SelectedProgrammer, SelectedPort, includeLock: true);
        var stale = FuseStates
            .Where(state => state.DeviceValue is byte oldValue && (!live.TryGetValue(state.MemoryName, out var current) || current != oldValue))
            .Select(state => state.MemoryName)
            .ToArray();
        if (stale.Length > 0)
            throw new InvalidOperationException("Fuse на устройстве изменились после последнего чтения (" + string.Join(", ", stale) + "). Запись отменена; прочитайте их заново.");

        var changeViews = changes.Select(BuildFuseChangeView).ToArray();
        var confirmation = new FuseWriteConfirmationWindow(SelectedDevice, changeViews) { Owner = this };
        if (confirmation.ShowDialog() != true)
        {
            StatusText.Text = "Запись fuse отменена пользователем";
            return;
        }

        var operationArguments = new List<string>();
        foreach (var change in changes.OrderBy(state => state.MemoryName.Equals("lock", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
        {
            operationArguments.Add("-U");
            operationArguments.Add($"{change.MemoryName}:w:0x{change.Value:X2}:m");
        }

        AppendLog("ВНИМАНИЕ: начинается явно подтверждённая запись fuse/lock.");
        var result = await _avrdude.RunAsync(SelectedDevice, SelectedProgrammer, SelectedPort, operationArguments);
        if (!result.Success)
            throw new InvalidOperationException("Запись fuse завершилась ошибкой. Некоторые байты могли успеть измениться; проверьте подключение и выполните чтение.");

        var readback = await _avrdude.ReadFuseBytesAsync(SelectedDevice, SelectedProgrammer, SelectedPort, includeLock: true);
        var failed = changes
            .Where(state => !readback.TryGetValue(state.MemoryName, out var value) || value != state.Value)
            .Select(state => $"{state.MemoryName}: ожидалось {state.HexValue}, прочитано {(readback.TryGetValue(state.MemoryName, out var value) ? $"0x{value:X2}" : "?")}")
            .ToArray();
        if (failed.Length > 0)
            throw new InvalidOperationException("Контрольное чтение не совпало:\n" + string.Join("\n", failed));

        foreach (var state in FuseStates)
            if (readback.TryGetValue(state.MemoryName, out var value))
                state.SetDeviceValue(value);
        StatusText.Text = "Fuse/lock записаны и подтверждены контрольным чтением";
        MessageBox.Show(this, "Fuse и lock записаны. Контрольное чтение полностью совпало.", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static FuseChangeView BuildFuseChangeView(FuseByteState state)
    {
        var oldValue = state.DeviceValue!.Value;
        var changedBits = state.Definition.Bits
            .Where(bit => ((oldValue >> bit.Index) & 1) != ((state.Value >> bit.Index) & 1))
            .Select(bit => $"{bit.Name}: {((oldValue >> bit.Index) & 1)}→{((state.Value >> bit.Index) & 1)}")
            .ToArray();
        var dangerousNames = new[] { "CKSEL", "SUT", "CKDIV", "RSTDISBL", "SPIEN", "DWEN", "JTAGEN", "OCDEN", "BOOTRST", "LOCK" };
        var dangerous = state.MemoryName.Equals("lock", StringComparison.OrdinalIgnoreCase) ||
                        changedBits.Any(text => dangerousNames.Any(name => text.Contains(name, StringComparison.OrdinalIgnoreCase)));
        return new FuseChangeView(state.DisplayName, state.MemoryName, oldValue, state.Value,
            changedBits.Length == 0 ? "Изменено значение байта" : string.Join("; ", changedBits), dangerous);
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = ProfileNameBox.Text.Trim();
            if (name.Length == 0)
                throw new InvalidOperationException("Введите название профиля.");
            if (ProfileFusesCheck.IsChecked == true && !FuseStates.All(state => state.HasDeviceValue))
                throw new InvalidOperationException("Чтобы сохранить fuse в профиль, сначала прочитайте их с устройства.");

            var existing = _settings.Profiles.FirstOrDefault(profile => profile.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
            if (existing is not null && MessageBox.Show(this, "Профиль с таким именем уже есть. Заменить его?", "Сохранить профиль",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            var profile = existing ?? new ProgrammingProfile();
            profile.Name = name;
            profile.Description = ProfileDescriptionBox.Text.Trim();
            profile.DeviceId = SelectedDevice.AvrdudeId;
            profile.ProgrammerId = SelectedProgrammer.Id;
            profile.Port = SelectedPort;
            profile.FlashFile = FlashPathBox.Text.Trim();
            profile.EepromFile = EepromPathBox.Text.Trim();
            profile.WriteFlash = ProfileFlashCheck.IsChecked == true;
            profile.WriteEeprom = ProfileEepromCheck.IsChecked == true;
            profile.WriteFuses = ProfileFusesCheck.IsChecked == true;
            profile.FuseValues = profile.WriteFuses
                ? FuseStates.ToDictionary(state => state.MemoryName, state => state.Value, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

            if (existing is null)
                _settings.Profiles.Add(profile);
            ProfileCombo.Items.Refresh();
            ProfileCombo.SelectedItem = profile;
            SaveSettings();
            StatusText.Text = $"Профиль «{name}» сохранён";
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not ProgrammingProfile profile)
            return;
        ApplyProfile(profile);
    }

    private void ApplyProfile(ProgrammingProfile profile)
    {
        _loading = true;
        try
        {
            DeviceCombo.SelectedItem = _devices.FirstOrDefault(device => device.AvrdudeId.Equals(profile.DeviceId, StringComparison.OrdinalIgnoreCase));
            ProgrammerCombo.SelectedItem = _programmers.FirstOrDefault(programmer => programmer.Id.Equals(profile.ProgrammerId, StringComparison.OrdinalIgnoreCase));
            PortCombo.Text = profile.Port;
            FlashPathBox.Text = profile.FlashFile;
            EepromPathBox.Text = profile.EepromFile;
            ProfileNameBox.Text = profile.Name;
            ProfileDescriptionBox.Text = profile.Description;
            ProfileFlashCheck.IsChecked = profile.WriteFlash;
            ProfileEepromCheck.IsChecked = profile.WriteEeprom;
            ProfileFusesCheck.IsChecked = profile.WriteFuses;
        }
        finally
        {
            _loading = false;
        }
        RefreshFirmwareLabel("flash", FlashPathBox.Text);
        RefreshFirmwareLabel("eeprom", EepromPathBox.Text);
        StatusText.Text = $"Загружен профиль «{profile.Name}»";
    }

    private async void ProgramProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not ProgrammingProfile profile)
        {
            ShowError(new InvalidOperationException("Выберите профиль."));
            return;
        }
        var choice = MessageBox.Show(this,
            $"Запустить профиль «{profile.Name}»?\n\nFlash: {(profile.WriteFlash ? "да" : "нет")}\nEEPROM: {(profile.WriteEeprom ? "да" : "нет")}\nFuse/lock: {(profile.WriteFuses ? "да, с отдельным подтверждением" : "нет")}",
            "Автоматическое программирование", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes)
            return;

        await ExecuteUiOperationAsync($"Выполняется профиль «{profile.Name}»…", async () =>
        {
            ApplyProfile(profile);
            if (!profile.WriteFlash && !profile.WriteEeprom && !profile.WriteFuses)
                throw new InvalidOperationException("В профиле не выбрано ни одной операции.");
            if (profile.WriteFlash)
                await RunMemoryOperationAsync("flash", 'w', flashEraseConfirmed: true);
            if (profile.WriteEeprom)
                await RunMemoryOperationAsync("eeprom", 'w');
            if (profile.WriteFuses)
            {
                await ReadFusesCoreAsync();
                foreach (var state in FuseStates)
                    if (profile.FuseValues.TryGetValue(state.MemoryName, out var desired))
                        state.ApplyEditableValue(desired);
                await WriteFusesCoreAsync();
            }
            StatusText.Text = $"Профиль «{profile.Name}» выполнен";
        });
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not ProgrammingProfile profile)
            return;
        if (MessageBox.Show(this, $"Удалить профиль «{profile.Name}»?", "Удалить профиль", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _settings.Profiles.Remove(profile);
        ProfileCombo.SelectedItem = null;
        ProfileCombo.Items.Refresh();
        SaveSettings();
        StatusText.Text = "Профиль удалён";
    }

    private void OpenDrivers_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Drivers_USBasp");
        if (!Directory.Exists(directory))
        {
            ShowError(new DirectoryNotFoundException("Папка драйвера не найдена в комплекте приложения."));
            return;
        }
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private async Task ExecuteUiOperationAsync(string status, Func<Task> operation)
    {
        if (_isBusy)
            return;
        SetBusy(true, status);
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            SetBusy(false, StatusText.Text);
            SaveSettings();
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _isBusy = busy;
        ContentTabs.IsEnabled = !busy;
        DeviceCombo.IsEnabled = !busy;
        ProgrammerCombo.IsEnabled = !busy;
        PortCombo.IsEnabled = !busy && (SelectedProgrammer?.PortEnabled ?? false);
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = status;
        UpdateFuseWriteAvailability();
        Mouse.OverrideCursor = busy ? Cursors.Wait : null;
    }

    private void Avrdude_OutputReceived(object? sender, string line) => Dispatcher.BeginInvoke(() => AppendLog(line));

    private void AppendLog(string line)
    {
        if (LogBox.Text.Length > 250_000)
            LogBox.Text = LogBox.Text[^150_000..];
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void ShowError(Exception exception)
    {
        AppendLog("ОШИБКА: " + exception.Message);
        StatusText.Text = "Ошибка: " + exception.Message.Replace(Environment.NewLine, " ");
        MessageBox.Show(this, exception.Message, "AVRDUDE PRO", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void EnsureFuseSnapshotsMatch(
        IReadOnlyDictionary<string, byte> before,
        IReadOnlyDictionary<string, byte> after,
        string context)
    {
        var changed = before.Where(pair => !after.TryGetValue(pair.Key, out var value) || value != pair.Value).ToArray();
        if (changed.Length > 0)
            throw new InvalidOperationException($"Fuse-байты неожиданно изменились {context}. Автоматическое восстановление не выполнялось.");
    }

    private static char OutputFormatForPath(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".hex" or ".ihex" or ".ihx" or ".a90" or ".eep" or ".eeprom")
            return 'i';
        if (extension is ".srec" or ".s19" or ".s28" or ".s37" or ".mot")
            return 's';
        return 'r';
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:F1} МБ",
        >= 1024 => $"{bytes / 1024d:F1} КБ",
        _ => $"{bytes} байт"
    };

    private void SaveSettings()
    {
        if (_loading)
            return;
        if (DeviceCombo.SelectedItem is DeviceDefinition device)
            _settings.SelectedDeviceId = device.AvrdudeId;
        if (ProgrammerCombo.SelectedItem is ProgrammerDefinition programmer)
            _settings.SelectedProgrammerId = programmer.Id;
        _settings.Port = PortCombo.Text;
        _settings.FlashFile = FlashPathBox.Text;
        _settings.EepromFile = EepromPathBox.Text;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        try
        {
            _settingsService.Save(_settings);
        }
        catch (Exception exception)
        {
            AppendLog("Не удалось сохранить настройки: " + exception.Message);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e) => SaveSettings();
}
