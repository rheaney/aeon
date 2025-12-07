using System.Text.Json.Serialization;

namespace Aeon.Emulator.Launcher.Configuration;

public sealed class AeonDriveConfiguration
{
	[JsonPropertyName("type")]
	[JsonConverter(typeof(JsonStringEnumConverter))]
	public DriveType Type { get; set; }
	[JsonPropertyName("host-path")]
	public string HostPath { get; set; } = string.Empty;
	[JsonPropertyName("read-only")]
	public bool ReadOnly { get; set; }
	[JsonPropertyName("image-path")]
	public string ImagePath { get; set; } = string.Empty;
	[JsonPropertyName("free-space")]
	public long? FreeSpace { get; set; }
	[JsonPropertyName("label")]
	public string Label { get; set; } = string.Empty;
}


