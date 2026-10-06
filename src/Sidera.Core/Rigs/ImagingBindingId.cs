using Sidera.Core.Devices;

namespace Sidera.Core.Rigs;

/// <summary>
/// The identity of an imaging path as a workflow means it: "imaging with this camera and what sits on it". It is not the identity of a setup object (<see cref="RigId"/>, what the equipment file
/// stores): a setup can be made, renamed, replaced or not exist at all (a single camera needs none), and the path stays the same as long as it is the same camera. Workflow blocks, autofocus
/// policies, the dither policy and the pointing setup refer to an imaging path by this id, so that creating an explicit imaging setup for a camera that was used without one changes nothing about
/// what they mean.
/// <para>
/// The id is derived from the camera and nothing else, so it is deterministic and stable: <c>imaging:auto:camera.main</c>. Never from a name, never random, never from a position in a list. A
/// setup whose camera is replaced is another path: it has another id, and what referred to the old one is not carried over.
/// </para>
/// <para>
/// A value that is not a path id (it does not start with <see cref="Prefix"/>) is a reference by setup id, as every document before this existed wrote it; <see cref="IsPath"/> tells the two apart,
/// and <see cref="Sidera.Runtime.Rigs.ISetupSource.TryResolve"/> resolves both.
/// </para>
/// </summary>
public readonly record struct ImagingBindingId
{
    public const string Prefix = "imaging:auto:";

    public string Value { get; }

    public ImagingBindingId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("An imaging binding cannot be empty or whitespace.", nameof(value));
        }

        Value = value.Trim();
    }

    /// <summary>The path of imaging with this camera.</summary>
    public static ImagingBindingId For(DeviceId camera) => new(Prefix + camera.Value);

    /// <summary>The path of a setup: the one of its camera.</summary>
    public static ImagingBindingId Of(Rig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        return For(rig.CameraId);
    }

    /// <summary>A setup id as a reference: what a document that names a setup by its id holds.</summary>
    public static implicit operator ImagingBindingId(RigId setup) => new(setup.Value);

    /// <summary>The value is the id of a path (derived from a camera), not a reference to a setup by its id.</summary>
    public bool IsPath => Value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The camera of the path; <c>false</c> for a reference by setup id.</summary>
    public bool TryGetCamera(out DeviceId camera)
    {
        if (IsPath && Value.Length > Prefix.Length)
        {
            camera = new DeviceId(Value[Prefix.Length..]);
            return true;
        }

        camera = default;
        return false;
    }

    public override string ToString() => Value;
}
