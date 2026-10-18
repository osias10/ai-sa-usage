using System.Text.Json;
using System.Text.Json.Serialization;
using AIUsage.Core;
using Xunit;

namespace AIUsage.Tests;

public sealed class WidgetPlacementTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AIUsage-placement-" + Guid.NewGuid().ToString("N"));
    private static readonly MonitorLayout Primary = new("panel-primary", "DISPLAY1",
        new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 1, true);
    private static readonly MonitorLayout Left = new("panel-left", "DISPLAY2",
        new(-2560, 0, 2560, 1440), new(-2560, 0, 2560, 1400), 1.25, false);
    private static readonly DesktopRect LeftPosition = new(-400, 1200, 300, 150);

    private static PlacementResolution Resolve(WidgetPlacementState? state, params MonitorLayout[] monitors)
        => Assert.IsType<PlacementResolution>(WidgetPlacementPolicy.Resolve(state, monitors, 240, 100, 160, 50));

    [Fact]
    public async Task DisconnectSleepAndRestartKeepPreferredMonitorUntilItReconnects()
    {
        var primaryPosition = new DesktopRect(100, 200, 240, 100);
        var state = WidgetPlacementPolicy.Capture(null, Primary, primaryPosition);
        state = WidgetPlacementPolicy.Capture(state, Left, LeftPosition);
        var original = JsonSerializer.Serialize(state);
        for (var i = 0; i < 5; i++)
        {
            var fallback = Resolve(state, Primary);
            Assert.True(fallback.IsFallback);
            Assert.Equal(primaryPosition, fallback.Bounds);
            Assert.Equal(original, JsonSerializer.Serialize(state));
        }

        var store = new JsonSettingsStore(new(_directory));
        await store.SaveSettingsAsync(new() { WidgetPlacement = state });
        var restarted = (await new JsonSettingsStore(new(_directory)).LoadSettingsAsync()).WidgetPlacement;
        Assert.Equal(Left.Id, restarted!.PreferredMonitorId);
        Assert.True(Resolve(restarted, Primary).IsFallback);
        var restored = Resolve(restarted, Primary, Left);
        Assert.False(restored.IsFallback);
        Assert.Equal(Left.Id, restored.Monitor.Id);
        Assert.Equal(LeftPosition, restored.Bounds);
    }

    [Fact]
    public void UserDraggingFallbackExplicitlyAdoptsNewMonitorAndRetainsBothMemories()
    {
        var state = WidgetPlacementPolicy.Capture(null, Left, LeftPosition);
        var moved = new DesktopRect(25, 40, 240, 120);
        var adopted = WidgetPlacementPolicy.Capture(state, Primary, moved);
        Assert.Equal(Left.Id, state.PreferredMonitorId);
        Assert.Single(state.Monitors);
        Assert.Equal(Primary.Id, adopted.PreferredMonitorId);
        Assert.Equal(2, adopted.Monitors.Count);
        Assert.Equal(moved, Resolve(adopted, Left, Primary).Bounds);
        Assert.Equal(LeftPosition, adopted.Monitors[Left.Id].Bounds);
    }

    [Fact]
    public void StableMonitorIdentityWinsOverWindowsDisplayNamesAndEnumerationOrder()
    {
        var state = WidgetPlacementPolicy.Capture(null, Left, LeftPosition);
        var renamedLeft = Left with { DeviceName = Primary.DeviceName };
        var renamedPrimary = Primary with { DeviceName = Left.DeviceName };
        var restored = Resolve(state, renamedLeft, renamedPrimary);
        Assert.Equal(Left.Id, restored.Monitor.Id);
        Assert.Equal(LeftPosition, restored.Bounds);
        Assert.Equal(restored, Resolve(state, renamedPrimary, renamedLeft));
    }

    [Fact]
    public void ReusedDisplayNameDoesNotTransferPositionToDifferentHardware()
    {
        var state = WidgetPlacementPolicy.Capture(null, Left, LeftPosition);
        var replacement = Left with { Id = "replacement-panel" };
        var resolved = Resolve(state, replacement, Primary);
        Assert.True(resolved.IsFallback);
        Assert.Equal(Primary.Id, resolved.Monitor.Id);
        Assert.Equal(Left.Id, state.PreferredMonitorId);
    }

    [Fact]
    public void MonitorOriginChangesPreserveRelativePositionIncludingNegativeCoordinates()
    {
        var state = WidgetPlacementPolicy.Capture(null, Left, LeftPosition);
        var moved = Left with { Bounds = new(1920, -200, 2560, 1440), WorkArea = new(1920, -200, 2560, 1400) };
        var resolved = Resolve(state, Primary, moved);
        Assert.Equal(new DesktopRect(4080, 1000, 300, 150), resolved.Bounds);
        Assert.Equal(LeftPosition, Resolve(state, Left, Primary).Bounds);
    }

    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void DpiChangesKeepLogicalSizeAndNearestEdgeMargins(double scale)
    {
        var state = WidgetPlacementPolicy.Capture(null, Primary, new(1650, 920, 240, 100));
        var scaled = Primary with
        {
            Bounds = new(0, 0, 1920 * scale, 1080 * scale),
            WorkArea = new(0, 0, 1920 * scale, 1040 * scale), Scale = scale
        };
        var resolved = Resolve(state, scaled);
        Assert.Equal(new DesktopRect(1650 * scale, 920 * scale, 240 * scale, 100 * scale), resolved.Bounds);
        Assert.Equal(30, (scaled.WorkArea.Width - resolved.Bounds.Left - resolved.Bounds.Width) / scale, 8);
        Assert.Equal(20, (scaled.WorkArea.Height - resolved.Bounds.Top - resolved.Bounds.Height) / scale, 8);
    }

    [Fact]
    public void MixedDpiAndPhysicalOriginAreNotTreatedAsOneGlobalDipSpace()
    {
        var state = WidgetPlacementPolicy.Capture(null, Left, LeftPosition);
        var current = Left with { Bounds = new(-3840, -600, 3840, 2160), WorkArea = new(-3840, -600, 3840, 2100), Scale = 2 };
        var resolved = Resolve(state, Primary, current);
        Assert.Equal(480, resolved.Bounds.Width);
        Assert.Equal(240, resolved.Bounds.Height);
        Assert.Equal(-640, resolved.Bounds.Left);
        Assert.Equal(1180, resolved.Bounds.Top);
    }

    [Fact]
    public void ResolutionChangesKeepClosestRightAndBottomMargins()
    {
        var state = WidgetPlacementPolicy.Capture(null, Primary, new(1650, 920, 240, 100));
        var changed = Primary with { Bounds = new(0, 0, 2560, 1440), WorkArea = new(0, 0, 2560, 1400) };
        Assert.Equal(new DesktopRect(2290, 1280, 240, 100), Resolve(state, changed).Bounds);
    }

    [Fact]
    public void TaskbarMovingToLeftOrTopKeepsOffsetsFromWorkArea()
    {
        var state = WidgetPlacementPolicy.Capture(null, Primary, new(100, 100, 240, 100));
        var changed = Primary with { WorkArea = new(40, 60, 1880, 1020) };
        Assert.Equal(new DesktopRect(140, 160, 240, 100), Resolve(state, changed).Bounds);
    }

    [Fact]
    public void AutomaticContentHeightDoesNotReanchorOrOverwriteSavedTop()
    {
        var state = WidgetPlacementPolicy.Capture(null, Primary, new(1650, 800, 240, 100));
        var original = JsonSerializer.Serialize(state);
        foreach (var height in new[] { 50d, 150, 50, 200 })
        {
            var result = WidgetPlacementPolicy.Resolve(state, [Primary], 240, 100, 160, 50, height)!;
            Assert.Equal(800, result.Bounds.Top);
            Assert.Equal(height, result.Bounds.Height);
        }
        Assert.Equal(original, JsonSerializer.Serialize(state));
        Assert.Equal(new DesktopRect(1650, 800, 240, 100), Resolve(state, Primary).Bounds);
    }

    [Fact]
    public void OversizedContentClampsWithoutLosingOriginalPreferredPlacement()
    {
        var state = WidgetPlacementPolicy.Capture(null, Left, LeftPosition);
        var huge = WidgetPlacementPolicy.Resolve(state, [Left], 240, 100, 160, 50, 10_000)!;
        Assert.Equal(LeftPosition.Left, huge.Bounds.Left);
        Assert.Equal(0, huge.Bounds.Top);
        Assert.Equal(1400, huge.Bounds.Height);
        Assert.Equal(LeftPosition, Resolve(state, Left).Bounds);
    }

    [Fact]
    public void TinyMonitorAllowsWholeWindowToRemainAccessibleEvenBelowNormalMinimum()
    {
        var tiny = new MonitorLayout("tiny", "DISPLAY9", new(-100, -200, 120, 90), new(-100, -200, 120, 60), 2, true);
        var resolved = Resolve(null, tiny);
        Assert.Equal(tiny.WorkArea, resolved.Bounds);
    }

    [Fact]
    public void SavedWindowBelowCurrentMinimumIsExpandedWithinWorkArea()
    {
        var state = WidgetPlacementPolicy.Capture(null, Primary, new(10, 10, 20, 20));
        Assert.Equal(new DesktopRect(10, 10, 160, 50), Resolve(state, Primary).Bounds);
    }

    [Fact]
    public void TemporaryMissingInventoryDoesNotEraseState()
    {
        var state = WidgetPlacementPolicy.Capture(null, Left, LeftPosition);
        Assert.Null(WidgetPlacementPolicy.Resolve(state, [], 240, 100, 160, 50));
        Assert.Equal(LeftPosition, Resolve(state, Left).Bounds);
    }

    [Fact]
    public void InvalidMonitorsAndSavedGeometryAreIgnoredWithoutCrashing()
    {
        var invalid = new[]
        {
            Primary with { Id = "" }, Primary with { Scale = 0 }, Primary with { Scale = double.NaN },
            Primary with { Bounds = new(0, 0, 0, 1080) },
            Primary with { WorkArea = new(double.PositiveInfinity, 0, 1920, 1040) },
            Primary with { WorkArea = new(0, 0, 2000, 1040) }, null!
        };
        Assert.Null(WidgetPlacementPolicy.Resolve(null, invalid, 240, 100, 160, 50));
        var state = new WidgetPlacementState
        {
            PreferredMonitorId = Primary.Id,
            Monitors = new() { [Primary.Id] = new(Primary.Id, Primary.DeviceName, new(double.NaN, 0, 240, 100), Primary.WorkArea, 1) }
        };
        var resolved = Resolve(state, [.. invalid, Primary]);
        Assert.False(resolved.IsFallback);
        Assert.Equal(new DesktopRect(1670, 930, 240, 100), resolved.Bounds);
        Assert.Same(state, WidgetPlacementPolicy.Capture(state, Primary, new(0, 0, double.NaN, 100)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(0)]
    public void InvalidSizeOptionsProduceFiniteAccessibleDefaults(double invalid)
    {
        var resolved = WidgetPlacementPolicy.Resolve(null, [Primary], invalid, invalid, invalid, invalid, invalid)!;
        Assert.True(WidgetPlacementPolicy.IsValid(resolved.Bounds));
        Assert.Equal(new DesktopRect(1670, 930, 240, 100), resolved.Bounds);
    }

    [Fact]
    public void MalformedNullStateCollectionsAreSafe()
    {
        var state = JsonSerializer.Deserialize<WidgetPlacementState>("{\"PreferredMonitorId\":\"absent\",\"Monitors\":null}")!;
        Assert.True(Resolve(state, Primary).IsFallback);
        var captured = WidgetPlacementPolicy.Capture(state, Primary, new(100, 100, 240, 100));
        Assert.Single(captured.Monitors);
    }

    [Fact]
    public void MissingLegacyDeviceIsNotAdoptedAsThePrimaryAndMigratesAfterReconnect()
    {
        var legacy = new WindowPlacement(-400, 1200, 240, 120, 1.25);
        var state = WidgetPlacementPolicy.MigrateLegacy(legacy, Left.DeviceName, [Primary]);
        Assert.Null(state.PreferredMonitorId);
        Assert.NotNull(state.PendingLegacy);
        Assert.Empty(state.Monitors);
        Assert.True(Resolve(state, Primary).IsFallback);
        var adopted = WidgetPlacementPolicy.AdoptLegacy(state, [Primary, Left]);
        Assert.Null(adopted.PendingLegacy);
        Assert.Equal(Left.Id, adopted.PreferredMonitorId);
        Assert.Equal(LeftPosition, Resolve(adopted, Left, Primary).Bounds);
        Assert.NotNull(state.PendingLegacy);
    }

    [Fact]
    public void LegacyCoordinatesOutsideEveryConnectedMonitorRemainPending()
    {
        var state = WidgetPlacementPolicy.MigrateLegacy(new(-1000, -1000, 240, 100), null, [Primary]);
        Assert.NotNull(state.PendingLegacy);
        Assert.Null(state.PreferredMonitorId);
        Assert.True(Resolve(state, Primary).IsFallback);
    }

    [Fact]
    public void LegacyNegativeCoordinatesCanMigrateWithoutADeviceName()
    {
        var state = WidgetPlacementPolicy.MigrateLegacy(new(-400, 1200, 240, 120, 1.25), null, [Primary, Left]);
        Assert.Equal(Left.Id, state.PreferredMonitorId);
        Assert.Equal(LeftPosition, Resolve(state, Primary, Left).Bounds);
    }

    [Fact]
    public async Task PendingLegacyAndOriginalSchemaRoundTripWithoutChangingExistingSettings()
    {
        var legacy = new WindowPlacement(-400, 1200, 240, 120, 1.25);
        var settings = new AppSettings
        {
            ClickThrough = true, WidgetTransparencyPercent = 35,
            Placements = new() { ["compact-v2:last"] = legacy, ["compact-v2:DISPLAY2"] = legacy },
            WidgetPlacement = WidgetPlacementPolicy.MigrateLegacy(legacy, Left.DeviceName, [Primary])
        };
        var paths = new AppPaths(_directory);
        var store = new JsonSettingsStore(paths);
        await store.SaveSettingsAsync(settings);
        var restored = await store.LoadSettingsAsync();
        Assert.Equal(1, restored.SchemaVersion);
        Assert.Equal(legacy, restored.Placements["compact-v2:last"]);
        Assert.Equal(settings.WidgetPlacement!.PendingLegacy, restored.WidgetPlacement!.PendingLegacy);
        Assert.True(restored.ClickThrough);
        Assert.Equal(35, restored.WidgetTransparencyPercent);
        // Older releases preserve unknown additive settings through JsonExtensionData.
        var oldView = JsonSerializer.Deserialize<LegacySettings>(await File.ReadAllTextAsync(paths.SettingsFile))!;
        oldView.ClickThrough = false;
        await File.WriteAllTextAsync(paths.SettingsFile, JsonSerializer.Serialize(oldView));
        restored = await store.LoadSettingsAsync();
        Assert.False(restored.ClickThrough);
        Assert.Equal(legacy, restored.Placements["compact-v2:last"]);
        Assert.Equal(settings.WidgetPlacement.PendingLegacy, restored.WidgetPlacement!.PendingLegacy);
    }

    [Fact]
    public async Task ExistingFilesWithoutNewPlacementStateAreUnchanged()
    {
        var paths = new AppPaths(_directory);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(paths.SettingsFile,
            "{\"SchemaVersion\":1,\"Placements\":{\"compact-v2:last\":{\"Left\":-400,\"Top\":1200,\"Width\":240,\"Height\":120,\"Scale\":1.25}},\"FutureSetting\":42}");
        var store = new JsonSettingsStore(paths);
        var loaded = await store.LoadSettingsAsync();
        Assert.Null(loaded.WidgetPlacement);
        Assert.Equal(-400, loaded.Placements["compact-v2:last"].Left);
        await store.SaveSettingsAsync(loaded);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(paths.SettingsFile));
        Assert.Equal(42, document.RootElement.GetProperty("FutureSetting").GetInt32());
    }

    private sealed class LegacySettings
    {
        public bool ClickThrough { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
