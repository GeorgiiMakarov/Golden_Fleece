// Dto.cs — data transfer objects for the emulator.
// Mirrors the Unity runtime contracts without any UnityEngine dependency:
//   * ManifestFile  mirrors Scripts/Manifest/LayerManifest.cs
//     (LayerManifest.cs uses System.Serializable precisely so both sides
//     share one DTO shape)
//   * GestureStream mirrors schemas/gesture-stream.schema.json
// JSON is camelCase on the wire; System.Text.Json maps it via naming policy.
using System.Text.Json.Serialization;

namespace XrLayerSwitcherEmulator;

public sealed class GestureStream
{
    public string StreamVersion { get; set; } = "1.0";
    public int Fps { get; set; } = 90;
    public List<JointFrame> Frames { get; set; } = new();
}

public sealed class JointFrame
{
    [JsonPropertyName("t_ms")] // wire format is snake_case (see gesture-stream.schema.json)
    public long TMs { get; set; }
    public bool Tracked { get; set; } = true;
    public string Label { get; set; } = "";
    public string ExpectedGesture { get; set; } = "None";
    // 26 joints, index 0 = palm (XRHandJointID.Palm). Each joint = [x, y, z] metres.
    // v1 classifiers read the palm only; the full hand is carried for v2.
    public List<List<float>> Joints { get; set; } = new();
}

public sealed class ManifestFile
{
    public string ManifestVersion { get; set; } = "1.0";
    public string GestureDictionaryVersion { get; set; } = "v1";
    public string VideoId { get; set; } = "";
    public List<LayerSpecDto> Layers { get; set; } = new();
}

public sealed class LayerSpecDto
{
    public string LayerId { get; set; } = "";
    public string BundleUrl { get; set; } = "";
    public long SpawnAtMs { get; set; }
    public string InitialState { get; set; } = "Appear";
    public PlacementDto Placement { get; set; } = new();
}

public sealed class PlacementDto
{
    public string Mode { get; set; } = "headLocked";
    public List<float> Offset { get; set; } = new() { 0f, 0.1f, 0.6f };
}
