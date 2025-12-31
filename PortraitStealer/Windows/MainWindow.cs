using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Dalamud.Bindings.ImGui;
using PortraitStealer.Windows.Controls;
using PortraitStealer.Services;

namespace PortraitStealer.Windows;

public partial class MainWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private string? _copyStatusMessage;
    private bool _copyStatusIsError;
    private readonly System.Diagnostics.Stopwatch _copyStatusTimer = new();

    private const float DefaultSpacing = 5f;
    private const float MediumSpacing = 8f;
    private const int MaxDutySlots = 8;
    private const int CopyStatusDurationMs = 3000;
    private uint _selectedCacheObjectId = 0;
    private CachedPortraitData? _selectedCacheInfoForDisplay => GetSelectedCacheData();
    private string? _selectedPlatePlayerName = null;
    private bool _isAgentCharaCardValid = false;
    private readonly CachedPortraitDisplay _cachedPortraitDisplay;
    private CachedPortraitData?[] _currentCacheState = new CachedPortraitData?[MaxDutySlots];

    private List<CachedPortraitData> _populatedCache = [];
    private bool _cacheFilterDirty = true;

    private float _actionGroupHeightEstimate;
    private Vector2 _maxStatusSize;
    private bool _initialCalculationsDone = false;

    public MainWindow(Plugin plugin)
        : base("Portrait Stealer###PortraitStealerMainWindow")
    {
        Vector2 fixedSize = new Vector2(700, 750);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = fixedSize,
            MaximumSize = fixedSize,
        };
        Size = fixedSize;
        SizeCondition = ImGuiCond.FirstUseEver;

        Flags |= ImGuiWindowFlags.NoScrollbar;
        Flags |= ImGuiWindowFlags.NoScrollWithMouse;
        Flags |= ImGuiWindowFlags.NoResize;

        _plugin = plugin;
        _cachedPortraitDisplay = new CachedPortraitDisplay(Plugin.Log, Plugin.TextureProvider);
    }

    private void PerformInitialCalculations()
    {
        if (_initialCalculationsDone)
            return;

        _actionGroupHeightEstimate =
            (ImUtf8.FrameHeight * 1.5f)
            + (ImUtf8.TextHeightSpacing * 4.5f)
            + (ImUtf8.Style.WindowPadding.Y * 2)
            + (MediumSpacing * ImUtf8.GlobalScale * 3);

        var longestStatus = "Failed to generate string!";
        Vector2 iconSize = Vector2.Zero;
        if (UiBuilder.IconFont.IsLoaded())
        {
            using var font = ImRaii.PushFont(UiBuilder.IconFont);
            iconSize = ImUtf8.CalcTextSize(FontAwesomeIcon.TimesCircle.ToIconString());
        }
        else
        {
            Plugin.Log.Warning(
                "IconFont not ready during PerformInitialCalculations. Using fallback size for status message."
            );
            iconSize = ImUtf8.CalcTextSize("X");
        }

        var textSize = ImUtf8.CalcTextSize(longestStatus);
        _maxStatusSize = new Vector2(
            iconSize.X + ImUtf8.ItemInnerSpacing.X + textSize.X,
            Math.Max(iconSize.Y, textSize.Y)
        );

        _initialCalculationsDone = true;
        Plugin.Log.Debug("MainWindow initial ImGui-dependent calculations performed.");
    }

    public void Dispose()
    {
        _cachedPortraitDisplay.Dispose();
        GC.SuppressFinalize(this);
    }

    public override unsafe void PreDraw()
    {
        if (!_initialCalculationsDone)
        {
            PerformInitialCalculations();
        }

        CheckAgentValidity();
        var newState = _plugin.DutySlotCacheService.GetCurrentCacheState();

        bool changed = false;
        if (!ReferenceEquals(newState, _currentCacheState))
        {
            changed = true;
        }
        else
        {
            for (int i = 0; i < MaxDutySlots; ++i)
            {
                var oldHasValue = _currentCacheState[i].HasValue;
                var newHasValue = newState[i].HasValue;
                if (oldHasValue != newHasValue)
                {
                    changed = true;
                    break;
                }
                else if (oldHasValue && newHasValue)
                {
                    // Compare the actual content, not just the references
                    var oldEntry = _currentCacheState[i]!.Value;
                    var newEntry = newState[i]!.Value;
                    if (oldEntry.ClientObjectId != newEntry.ClientObjectId ||
                        oldEntry.Timestamp != newEntry.Timestamp ||
                        oldEntry.State != newEntry.State)
                    {
                        changed = true;
                        break;
                    }
                }
            }
        }

        if (changed || _cacheFilterDirty)
        {
            _currentCacheState = newState;
            UpdatePopulatedCache();
            _cacheFilterDirty = false;
        }

        if (
            _selectedCacheObjectId != 0
            && !_currentCacheState.Any(c =>
                c.HasValue && c.Value.ClientObjectId == _selectedCacheObjectId
            )
        )
        {
            _selectedCacheObjectId = 0;
            _cachedPortraitDisplay.SetData(null);
        }
    }

    private void UpdatePopulatedCache()
    {
        _populatedCache = _currentCacheState
            .Where(c => c.HasValue)
            .Select(c => c!.Value)
            .OrderBy(c => c.SlotIndex)
            .ToList();
    }

    public override void Draw()
    {
        if (!_initialCalculationsDone)
        {
            PerformInitialCalculations();
        }

        var selectedData = _selectedCacheInfoForDisplay;
        if (selectedData.HasValue)
        {
            _cachedPortraitDisplay.SetData(selectedData.Value);
        }
        else
        {
            _cachedPortraitDisplay.SetData(null);
        }

        using var style = ImRaii
            .PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(MediumSpacing, MediumSpacing))
            .Push(ImGuiStyleVar.FramePadding, new Vector2(4f, 3f) * ImUtf8.GlobalScale)
            .Push(
                ImGuiStyleVar.ItemSpacing,
                new Vector2(DefaultSpacing, DefaultSpacing) * ImUtf8.GlobalScale
            );

        using var tabBar = ImUtf8.TabBar("MainTabs");
        if (!tabBar)
            return;

        DrawTabs();
    }

    private unsafe void CheckAgentValidity()
    {
        _isAgentCharaCardValid = false;
        try
        {
            var agentInterface = Plugin.GameGui.GetAgentById((int)AgentId.CharaCard);
            if (!agentInterface.IsNull && agentInterface.IsAgentActive)
            {
                var agentCharaCard = (AgentCharaCard*)agentInterface.Address;
                _isAgentCharaCardValid = agentCharaCard->Data != null;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Exception during Agent CharaCard check.");
            _isAgentCharaCardValid = false;
        }
    }

    private CachedPortraitData? GetSelectedCacheData()
    {
        if (_selectedCacheObjectId == 0)
            return null;
        return _currentCacheState.FirstOrDefault(c =>
            c.HasValue && c.Value.ClientObjectId == _selectedCacheObjectId
        );
    }

    private void SetSelectedPlate(string? playerName)
    {
        _selectedPlatePlayerName = playerName;
    }

    private string? GetSelectedPlatePlayerName(List<CachedAdventurerPlateInfo> cachedPlates)
    {
        if (string.IsNullOrEmpty(_selectedPlatePlayerName))
            return null;
        
        if (cachedPlates.Any(p => p.PlayerName == _selectedPlatePlayerName))
            return _selectedPlatePlayerName;

        _selectedPlatePlayerName = null;
        return null;
    }

    private CachedAdventurerPlateInfo? GetSelectedPlateInfo(List<CachedAdventurerPlateInfo> cachedPlates)
    {
        var selectedName = GetSelectedPlatePlayerName(cachedPlates);
        if (string.IsNullOrEmpty(selectedName))
            return null;
        
        return cachedPlates.FirstOrDefault(p => p.PlayerName == selectedName);
    }
}
