using System.IO;
using System.Text.Json;

namespace PerformanceMonitorLite.Mcp;

internal sealed class McpSettings
{
    public bool Enabled { get; set; } = false;
    public int Port { get; set; } = 5151;

    public static McpSettings Load(string configDirectory)
    {
        var path = Path.Combine(configDirectory, "settings.json");
        if (!File.Exists(path))
        {
            return new McpSettings();
        }

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new McpSettings
            {
                Enabled = root.TryGetProperty("mcp_enabled", out var enabled) && enabled.GetBoolean(),
                Port = root.TryGetProperty("mcp_port", out var port) ? port.GetInt32() : 5151
            };
        }
        catch
        {
            return new McpSettings();
        }
    }
}
