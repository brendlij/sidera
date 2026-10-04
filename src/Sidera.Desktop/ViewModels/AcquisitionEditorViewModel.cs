using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sidera.Core.Devices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sidera.Desktop.ViewModels;

/// <summary>The camera an exposure takes its frames with, as far as the editor can see it: its id and, once it reports them, its capabilities.</summary>
public sealed record AcquisitionCameraContext(DeviceId? CameraId, DeviceCapabilities<CameraCapabilities> Capabilities)
{
    public static AcquisitionCameraContext None { get; } = new(null, DeviceCapabilities<CameraCapabilities>.Unknown);
}

/// <summary>
/// One gain or offset of an exposure: "Camera default" or a value. For a camera whose gain is a range the value is typed; for one
/// whose gain is a list of named modes it is picked by name. The input follows the capabilities of the camera and nothing else.
/// </summary>
public sealed partial class AcquisitionLevelEditor : ObservableObject
{
    public const string DefaultChoice = "Camera default";
    private static readonly string[] Modes = [DefaultChoice, "Set value"];

    private readonly Action<AcquisitionLevel?> _changed;
    private bool _loading;
    private IntegerControl? _control;

    public AcquisitionLevelEditor(string label, Action<AcquisitionLevel?> changed)
    {
        Label = label;
        _changed = changed;
        ModeChoices = Modes;
    }

    public string Label { get; }

    /// <summary>The camera has this setting: it is offered.</summary>
    [ObservableProperty]
    public partial bool IsAvailable { get; private set; }

    /// <summary>A range: the mode and a text box. Otherwise (a list) the names are offered.</summary>
    [ObservableProperty]
    public partial bool IsRange { get; private set; }

    [ObservableProperty]
    public partial bool IsList { get; private set; }

    public IReadOnlyList<string> ModeChoices { get; }

    [ObservableProperty]
    public partial int ModeIndex { get; set; }

    [ObservableProperty]
    public partial bool ShowValue { get; private set; }

    [ObservableProperty]
    public partial string ValueText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Hint { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<string> Choices { get; private set; } = [DefaultChoice];

    [ObservableProperty]
    public partial int ChoiceIndex { get; set; }

    partial void OnModeIndexChanged(int value)
    {
        ShowValue = value == 1;
        Push();
    }

    partial void OnValueTextChanged(string value) => Push();

    partial void OnChoiceIndexChanged(int value) => Push();

    private void Push()
    {
        if (!_loading)
        {
            _changed(Read());
        }
    }

    /// <summary>What is entered: <c>null</c> for the camera default.</summary>
    public AcquisitionLevel? Read()
    {
        if (!IsAvailable)
        {
            return null;
        }

        if (IsList)
        {
            // Index 0 is the default; the entries after it are the choices, and an entry the camera no longer has is kept as a name.
            return ChoiceIndex <= 0 || ChoiceIndex >= Choices.Count ? null : AcquisitionLevel.OfName(RawName(Choices[ChoiceIndex]));
        }

        if (ModeIndex != 1)
        {
            return null;
        }

        var text = ValueText?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            || int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out number)
            ? AcquisitionLevel.OfNumber(number)
            : AcquisitionLevel.OfName(text);
    }

    private const string Missing = " (not available)";

    private static string RawName(string choice) => choice.EndsWith(Missing, StringComparison.Ordinal) ? choice[..^Missing.Length] : choice;

    /// <summary>Shows the capabilities of the camera and the value of the exposure.</summary>
    public void Load(IntegerControl? control, AcquisitionLevel? level)
    {
        _loading = true;
        try
        {
            _control = control;
            IsAvailable = control is not null;
            IsList = control is { IsList: true };
            IsRange = control is { IsList: false };
            Hint = control is { IsList: false, Minimum: { } lo, Maximum: { } hi } ? string.Create(CultureInfo.InvariantCulture, $"{lo} to {hi}") : string.Empty;

            if (control is { IsList: true })
            {
                var choices = new List<string> { DefaultChoice };
                choices.AddRange(control.Choices);
                var index = 0;
                if (level is not null)
                {
                    var name = level.Name ?? level.Number!.Value.ToString(CultureInfo.InvariantCulture);
                    index = control.Choices.ToList().FindIndex(choice => string.Equals(choice, name, StringComparison.Ordinal)) + 1;
                    if (index == 0)
                    {
                        // The document asks for something this camera does not have: shown, never replaced by another choice.
                        choices.Add(name + Missing);
                        index = choices.Count - 1;
                    }
                }

                Choices = choices;
                ChoiceIndex = index;
            }
            else
            {
                Choices = [DefaultChoice];
                ChoiceIndex = 0;
            }

            ModeIndex = level is null ? 0 : 1;
            ShowValue = ModeIndex == 1;
            ValueText = level?.ToString() ?? string.Empty;
        }
        finally
        {
            _loading = false;
        }
    }
}

/// <summary>
/// The Acquisition section of an exposure: what the exposure sets itself besides its duration. Everything that is not set is
/// inherited from the defaults of the camera, and the first entry of every choice says so. Only the settings that the camera of
/// the exposure supports are offered, so there is nothing disabled to look at; while the camera is not connected nothing can be
/// offered, what the exposure already sets is shown as text and kept as it is, and it is checked once the camera reports what
/// it supports. The one source of truth is the intent: the inputs are views of it.
/// </summary>
public sealed partial class AcquisitionEditorViewModel : ObservableObject
{
    public const string DefaultChoice = AcquisitionLevelEditor.DefaultChoice;

    private static readonly IReadOnlyList<FrameType> FrameTypes = [FrameType.Light, FrameType.Dark, FrameType.Flat, FrameType.Bias];
    private static readonly string[] RegionModes = [DefaultChoice, "Full frame", "Region"];
    private static readonly string[] FastModes = [DefaultChoice, "On", "Off"];

    private readonly Func<AcquisitionCameraContext> _camera;
    private AcquisitionIntent _intent;
    private bool _loading;
    private string _signature = string.Empty;

    public AcquisitionEditorViewModel(AcquisitionIntent intent, Func<AcquisitionCameraContext> camera)
    {
        _intent = intent;
        _camera = camera;
        Gain = new AcquisitionLevelEditor("Gain", level => Change(i => i with { Gain = level }));
        Offset = new AcquisitionLevelEditor("Offset", level => Change(i => i with { Offset = level }));
        FrameTypeChoices = [.. FrameTypes.Select(t => t.ToString())];
        RegionChoices = RegionModes;
        FastChoices = FastModes;
        Refresh(force: true);
    }

    /// <summary>Raised when the user changed something that is part of the intent.</summary>
    public event EventHandler? Edited;

    public AcquisitionIntent Intent => _intent;

    public AcquisitionLevelEditor Gain { get; }
    public AcquisitionLevelEditor Offset { get; }

    public IReadOnlyList<string> FrameTypeChoices { get; }

    [ObservableProperty]
    public partial int FrameTypeIndex { get; set; }

    // Binning
    [ObservableProperty]
    public partial bool ShowBinning { get; private set; }

    [ObservableProperty]
    public partial bool ShowAsymmetricBinning { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<string> BinChoices { get; private set; } = [DefaultChoice];

    [ObservableProperty]
    public partial int BinXIndex { get; set; }

    [ObservableProperty]
    public partial int BinYIndex { get; set; }

    // Region
    [ObservableProperty]
    public partial bool ShowRegion { get; private set; }

    public IReadOnlyList<string> RegionChoices { get; }

    [ObservableProperty]
    public partial int RegionIndex { get; set; }

    [ObservableProperty]
    public partial bool ShowRegionFields { get; private set; }

    [ObservableProperty]
    public partial string RegionXText { get; set; } = "0";

    [ObservableProperty]
    public partial string RegionYText { get; set; } = "0";

    [ObservableProperty]
    public partial string RegionWidthText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RegionHeightText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RegionHint { get; private set; } = string.Empty;

    // Readout
    [ObservableProperty]
    public partial bool ShowReadout { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<string> ReadoutChoices { get; private set; } = [DefaultChoice];

    [ObservableProperty]
    public partial int ReadoutIndex { get; set; }

    [ObservableProperty]
    public partial bool ShowFastReadout { get; private set; }

    public IReadOnlyList<string> FastChoices { get; }

    [ObservableProperty]
    public partial int FastIndex { get; set; }

    /// <summary>What the exposure sets itself, as the row shows it; for a camera that cannot be asked yet this is all there is to see.</summary>
    [ObservableProperty]
    public partial string ExplicitSummary { get; private set; } = string.Empty;

    /// <summary>The camera is not connected and the exposure sets something that can only be checked against it.</summary>
    [ObservableProperty]
    public partial bool IsNotVerifiable { get; private set; }

    /// <summary>The camera reports capabilities: the settings it supports are offered.</summary>
    [ObservableProperty]
    public partial bool IsVerifiable { get; private set; }

    /// <summary>The exposure has a camera to take it with.</summary>
    [ObservableProperty]
    public partial bool HasCamera { get; private set; }

    /// <summary>At least one setting of the camera can be chosen here.</summary>
    [ObservableProperty]
    public partial bool HasChoices { get; private set; }

    // ---- The inputs write the intent

    partial void OnFrameTypeIndexChanged(int value)
    {
        if (value >= 0 && value < FrameTypes.Count)
        {
            Change(i => i with { FrameType = FrameTypes[value] });
        }
    }

    partial void OnBinXIndexChanged(int value)
    {
        if (_loading)
        {
            return;
        }

        var x = BinValue(value);
        if (ShowAsymmetricBinning)
        {
            Change(i => i with { BinX = x });
        }
        else
        {
            // One selector for both axes of a camera that only bins the same horizontally and vertically.
            Change(i => i with { BinX = x, BinY = x });
        }
    }

    partial void OnBinYIndexChanged(int value)
    {
        if (!_loading && ShowAsymmetricBinning)
        {
            Change(i => i with { BinY = BinValue(value) });
        }
    }

    partial void OnRegionIndexChanged(int value)
    {
        ShowRegionFields = value == 2;
        if (!_loading)
        {
            ChangeRegion();
        }
    }

    partial void OnRegionXTextChanged(string value) => ChangeRegionFields();

    partial void OnRegionYTextChanged(string value) => ChangeRegionFields();

    partial void OnRegionWidthTextChanged(string value) => ChangeRegionFields();

    partial void OnRegionHeightTextChanged(string value) => ChangeRegionFields();

    partial void OnReadoutIndexChanged(int value)
    {
        if (!_loading)
        {
            Change(i => i with { ReadoutMode = value <= 0 || value >= ReadoutChoices.Count ? null : RawReadout(ReadoutChoices[value]) });
        }
    }

    partial void OnFastIndexChanged(int value)
    {
        if (!_loading)
        {
            Change(i => i with { FastReadout = value switch { 1 => true, 2 => false, _ => null } });
        }
    }

    private void ChangeRegionFields()
    {
        if (!_loading && RegionIndex == 2)
        {
            ChangeRegion();
        }
    }

    private void ChangeRegion()
    {
        AcquisitionRegion? region = RegionIndex switch
        {
            1 => AcquisitionRegion.Full,
            2 => ReadRegion(),
            _ => null,
        };

        // A rectangle that is not complete yet is not an intent yet: the old region stays until it is, and the editor says what is missing
        // (the draft reads the fields again and reports them).
        if (RegionIndex != 2 || region is not null)
        {
            Change(i => i with { Region = region });
        }
        else
        {
            Edited?.Invoke(this, EventArgs.Empty);
        }
    }

    private AcquisitionRegion? ReadRegion()
    {
        static bool Whole(string text, out int value) =>
            int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            || int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value);

        if (Whole(RegionXText, out var x) && Whole(RegionYText, out var y) && Whole(RegionWidthText, out var w) && Whole(RegionHeightText, out var h)
            && x >= 0 && y >= 0 && w > 0 && h > 0)
        {
            return AcquisitionRegion.Of(x, y, w, h);
        }

        return null;
    }

    private const string Missing = " (not available)";

    // The value of the entry of the binning choices: null for "Camera default".
    private List<int?> _binValues = [null];

    private int? BinValue(int index) => index >= 0 && index < _binValues.Count ? _binValues[index] : null;

    private static string RawReadout(string choice) => choice.EndsWith(Missing, StringComparison.Ordinal) ? choice[..^Missing.Length] : choice;

    private void Change(Func<AcquisitionIntent, AcquisitionIntent> edit)
    {
        if (_loading)
        {
            return;
        }

        var next = edit(_intent);
        if (next == _intent)
        {
            return;
        }

        _intent = next;
        ExplicitSummary = Describe(next);
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private static string Describe(AcquisitionIntent intent) =>
        intent.IsDefault ? string.Empty : SequenceDraftBuilder.AcquisitionSummary(intent).TrimStart(' ', '·');

    /// <summary>The problems with the fields as typed: a region that is not a rectangle yet.</summary>
    internal AcquisitionIntent Read(List<string> parseErrors)
    {
        if (RegionIndex == 2 && ReadRegion() is null)
        {
            parseErrors.Add("The region needs a start at 0 or more and a width and height of at least 1 pixel, as whole numbers.");
        }

        return _intent;
    }

    // ---- The capabilities of the camera decide what is offered

    /// <summary>
    /// Reads the capabilities of the camera again. The lists of choices are only made new when what the camera supports changed
    /// (or the camera did), so that editing is not disturbed; the intent itself is never changed by this.
    /// </summary>
    public void Refresh(bool force = false)
    {
        var context = _camera();
        var capabilities = context.Capabilities.Value;
        var signature = Signature(context, capabilities);
        if (!force && signature == _signature)
        {
            return;
        }

        _signature = signature;
        _loading = true;
        try
        {
            HasCamera = context.CameraId is not null;
            IsVerifiable = capabilities is not null;
            IsNotVerifiable = capabilities is null && HasCamera && !_intent.IsDefault;
            FrameTypeIndex = Math.Max(0, FrameTypes.ToList().IndexOf(_intent.FrameType));
            ExplicitSummary = Describe(_intent);

            if (capabilities is null)
            {
                ShowBinning = ShowAsymmetricBinning = ShowRegion = ShowReadout = ShowFastReadout = false;
                Gain.Load(null, _intent.Gain);
                Offset.Load(null, _intent.Offset);
                HasChoices = false;
                return;
            }

            Gain.Load(capabilities.Gain, _intent.Gain);
            Offset.Load(capabilities.Offset, _intent.Offset);

            ShowBinning = capabilities.SupportsBinning;
            ShowAsymmetricBinning = capabilities.SupportsBinning && capabilities.CanAsymmetricBin;
            var maxBin = Math.Max(capabilities.MaxBinX, capabilities.MaxBinY);
            var bins = new List<string> { DefaultChoice };
            var values = new List<int?> { null };
            string Label(int bin) => ShowAsymmetricBinning
                ? bin.ToString(CultureInfo.InvariantCulture)
                : string.Create(CultureInfo.InvariantCulture, $"{bin}×{bin}");
            for (var bin = 1; bin <= maxBin; bin++)
            {
                bins.Add(Label(bin));
                values.Add(bin);
            }

            // What the exposure asks for but the camera cannot do is listed and selected as missing, never replaced by another binning.
            foreach (var asked in new[] { _intent.BinX, _intent.BinY }.OfType<int>().Where(b => b > maxBin || b < 1).Distinct())
            {
                bins.Add(Label(asked) + Missing);
                values.Add(asked);
            }

            _binValues = values;
            BinChoices = bins;
            int IndexOf(int? bin) => bin is null ? 0 : Math.Max(0, values.IndexOf(bin));
            BinXIndex = ShowAsymmetricBinning ? IndexOf(_intent.BinX) : IndexOf(_intent.BinX ?? _intent.BinY);
            BinYIndex = IndexOf(_intent.BinY);

            ShowRegion = capabilities.SupportsSubframe;
            RegionHint = string.Create(
                CultureInfo.InvariantCulture,
                $"In binned pixels; inside {capabilities.SensorWidth} x {capabilities.SensorHeight} at 1x1 binning");
            switch (_intent.Region)
            {
                case null:
                    RegionIndex = 0;
                    break;
                case { IsFullFrame: true }:
                    RegionIndex = 1;
                    break;
                case var region:
                    RegionIndex = 2;
                    (RegionXText, RegionYText) = (region.X.ToString(CultureInfo.InvariantCulture), region.Y.ToString(CultureInfo.InvariantCulture));
                    (RegionWidthText, RegionHeightText) = (region.Width.ToString(CultureInfo.InvariantCulture), region.Height.ToString(CultureInfo.InvariantCulture));
                    break;
            }

            ShowRegionFields = RegionIndex == 2;

            ShowReadout = capabilities.ReadoutModes.Count > 0;
            var readout = new List<string> { DefaultChoice };
            readout.AddRange(capabilities.ReadoutModes);
            var readoutIndex = 0;
            if (_intent.ReadoutMode is { } wanted)
            {
                readoutIndex = capabilities.ReadoutModes.ToList().FindIndex(m => string.Equals(m, wanted, StringComparison.Ordinal)) + 1;
                if (readoutIndex == 0)
                {
                    // A mode the camera no longer has: shown as missing, never replaced by another one.
                    readout.Add(wanted + Missing);
                    readoutIndex = readout.Count - 1;
                }
            }

            ReadoutChoices = readout;
            ReadoutIndex = ShowReadout || _intent.ReadoutMode is not null ? readoutIndex : 0;
            ShowReadout |= _intent.ReadoutMode is not null;

            ShowFastReadout = capabilities.CanFastReadout;
            FastIndex = _intent.FastReadout switch { true => 1, false => 2, _ => 0 };

            HasChoices = Gain.IsAvailable || Offset.IsAvailable || ShowBinning || ShowRegion || ShowReadout || ShowFastReadout;
        }
        finally
        {
            _loading = false;
        }
    }

    // What decides the lists: the camera, and what it supports.
    private static string Signature(AcquisitionCameraContext context, CameraCapabilities? c) => c is null
        ? $"{context.CameraId}|unknown"
        : string.Join(
            '|',
            context.CameraId, c.Gain?.ToString(), c.Gain?.Choices.Count, string.Join(",", c.Gain?.Choices ?? []),
            c.Offset?.ToString(), string.Join(",", c.Offset?.Choices ?? []), c.MaxBinX, c.MaxBinY, c.CanAsymmetricBin, c.SupportsSubframe,
            c.SensorWidth, c.SensorHeight, string.Join(",", c.ReadoutModes), c.CanFastReadout);
}
