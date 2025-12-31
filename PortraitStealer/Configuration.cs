using System;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace PortraitStealer;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool ShowCaptureButtonOnAdventurerPlate { get; set; } = false;

    [NonSerialized]
    private IDalamudPluginInterface? _pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface;
    }

    public void Save()
    {
        _pluginInterface?.SavePluginConfig(this);
    }
}

