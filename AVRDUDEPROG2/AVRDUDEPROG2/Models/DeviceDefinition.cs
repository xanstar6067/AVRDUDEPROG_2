using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AVRDUDEPROG2.Models;

public sealed class DeviceDefinition
{
    public required string DisplayName { get; init; }
    public required string AvrdudeId { get; init; }
    public IReadOnlyList<FuseByteDefinition> FuseBytes { get; init; } = [];

    public string SearchText => $"{DisplayName} {AvrdudeId}";
    public override string ToString() => DisplayName;
}

public sealed class FuseByteDefinition
{
    public required string MemoryName { get; init; }
    public required string DisplayName { get; init; }
    public IReadOnlyList<FuseBitDefinition> Bits { get; init; } = [];
}

public sealed class FuseBitDefinition
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required bool IsEditable { get; init; }
    public required bool DefaultRawValue { get; init; }
}

public sealed class FuseByteState : INotifyPropertyChanged
{
    private byte _value;
    private byte? _deviceValue;

    public FuseByteState(FuseByteDefinition definition)
    {
        Definition = definition;
        Bits = definition.Bits.OrderByDescending(bit => bit.Index)
            .Select(bit => new FuseBitState(this, bit))
            .ToArray();
        Value = BuildDefaultValue(definition);
    }

    public FuseByteDefinition Definition { get; }
    public IReadOnlyList<FuseBitState> Bits { get; }
    public string MemoryName => Definition.MemoryName;
    public string DisplayName => Definition.DisplayName;
    public bool HasDeviceValue => _deviceValue.HasValue;
    public byte? DeviceValue => _deviceValue;
    public bool IsChanged => _deviceValue.HasValue && _deviceValue.Value != Value;
    public string HexValue => $"0x{Value:X2}";
    public string BinaryValue => Convert.ToString(Value, 2).PadLeft(8, '0');
    public string DeviceHexValue => _deviceValue is byte value ? $"0x{value:X2}" : "—";
    public byte EditableMask => (byte)Definition.Bits
        .Where(bit => bit.IsEditable)
        .Aggregate(0, (mask, bit) => mask | (1 << bit.Index));
    public bool ReadOnlyBitsMatchDevice => !_deviceValue.HasValue ||
        (Value & ~EditableMask) == (_deviceValue.Value & ~EditableMask);

    public byte Value
    {
        get => _value;
        set
        {
            if (_value == value)
                return;

            _value = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HexValue));
            OnPropertyChanged(nameof(BinaryValue));
            OnPropertyChanged(nameof(IsChanged));
            foreach (var bit in Bits)
                bit.Refresh();
        }
    }

    public void SetDeviceValue(byte value)
    {
        _deviceValue = value;
        _value = value;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(HexValue));
        OnPropertyChanged(nameof(BinaryValue));
        OnPropertyChanged(nameof(DeviceValue));
        OnPropertyChanged(nameof(DeviceHexValue));
        OnPropertyChanged(nameof(HasDeviceValue));
        OnPropertyChanged(nameof(IsChanged));
        foreach (var bit in Bits)
            bit.Refresh();
    }

    public void ResetToDeviceValue()
    {
        if (_deviceValue.HasValue)
            Value = _deviceValue.Value;
    }

    public void ResetToDefaults() => ApplyEditableValue(BuildDefaultValue(Definition));

    public void ApplyEditableValue(byte desiredValue)
    {
        if (_deviceValue is byte deviceValue)
            Value = (byte)((desiredValue & EditableMask) | (deviceValue & ~EditableMask));
        else
            Value = desiredValue;
    }

    internal bool GetRawBit(int index) => (Value & (1 << index)) != 0;

    internal void SetRawBit(int index, bool rawValue)
    {
        Value = rawValue
            ? (byte)(Value | (1 << index))
            : (byte)(Value & ~(1 << index));
    }

    private static byte BuildDefaultValue(FuseByteDefinition definition)
    {
        var value = 0;
        foreach (var bit in definition.Bits)
            if (bit.DefaultRawValue)
                value |= 1 << bit.Index;
        return (byte)value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class FuseBitState : INotifyPropertyChanged
{
    private readonly FuseByteState _owner;

    public FuseBitState(FuseByteState owner, FuseBitDefinition definition)
    {
        _owner = owner;
        Definition = definition;
    }

    public FuseBitDefinition Definition { get; }
    public string Name => Definition.Name;
    public int Index => Definition.Index;
    public bool IsEditable => Definition.IsEditable;
    public string BitLabel => $"b{Index}";
    public string StateHint => IsRawOne ? "1 · не запрограммирован" : "0 · запрограммирован";
    public string RawValueText => IsRawOne ? "1" : "0";

    // AVR fuse bits are active-low. The UI exposes the raw datasheet value explicitly.
    public bool IsRawOne
    {
        get => _owner.GetRawBit(Index);
        set
        {
            if (IsEditable && value != IsRawOne)
                _owner.SetRawBit(Index, value);
        }
    }

    internal void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRawOne)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateHint)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RawValueText)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
