using System.Windows;
using AVRDUDEPROG2.Models;

namespace AVRDUDEPROG2;

public sealed record FuseChangeView(
    string DisplayName,
    string MemoryName,
    byte OldValue,
    byte NewValue,
    string ChangedBits,
    bool IsDangerous)
{
    public string OldHex => $"0x{OldValue:X2}";
    public string NewHex => $"0x{NewValue:X2}";
    public Visibility DangerVisibility => IsDangerous ? Visibility.Visible : Visibility.Collapsed;
}

public partial class FuseWriteConfirmationWindow : Window
{
    public FuseWriteConfirmationWindow(DeviceDefinition device, IReadOnlyList<FuseChangeView> changes)
    {
        InitializeComponent();
        DeviceText.Text = $"Устройство: {device.DisplayName} ({device.AvrdudeId})";
        ChangeList.ItemsSource = changes;
        RiskText.Text = changes.Any(change => change.IsDangerous)
            ? "Обнаружены изменения тактирования или интерфейса программирования. Ошибка может сделать устройство недоступным для текущего программатора. Сверьте значения с datasheet и схемой."
            : "Fuse-биты будут записаны отдельно от Flash/EEPROM. После записи приложение немедленно прочитает их обратно и сверит значения.";
    }

    private void ConfirmationBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ConfirmButton.IsEnabled = string.Equals(ConfirmationBox.Text.Trim(), "ЗАПИСАТЬ", StringComparison.OrdinalIgnoreCase);

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
