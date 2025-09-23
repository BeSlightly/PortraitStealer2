using System;
using System.IO;
using Dalamud.Plugin.Services;

namespace PortraitStealer.Services;

public static class FileHelpers
{
    public static void SafeDeleteFile(string? path, IPluginLog log, string context)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        try
        {
            File.Delete(path);
            log.Debug($"[{context}] Deleted file: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[{context}] Failed to delete file: {Path.GetFileName(path)}");
        }
    }

    public static bool SafeCreateDirectory(string directoryPath, IPluginLog log, string context)
    {
        try
        {
            Directory.CreateDirectory(directoryPath);
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"Failed to create {context} directory: {directoryPath}");
            return false;
        }
    }
}
