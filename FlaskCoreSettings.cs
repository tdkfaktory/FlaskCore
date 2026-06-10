using System.Collections.Generic;
using System.Windows.Forms;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using System.Text.Json.Serialization;

namespace FlaskCore;

/// <summary>
/// User configures how much each flask heals (flat HP/mana values from tooltip).
/// Plugin reads MaxHP/MaxMana from the player each Tick and derives trigger thresholds automatically.
///
/// NORMAL (regen over ~3s, has buff):
///   healFrac = HealHp / MaxHP. Trigger = missing >= healFrac AND no buff.
///
/// BUBBLING (~28% instant + ~72% regen, has buff):
///   instFrac = healFrac * InstantSplitPct / 100. Trigger = missing >= instFrac (regardless of buff).
///   Buff state is irrelevant — instant portion is always available.
///
/// SEETHING (100% instant, no buff):
///   healFrac = HealHp / MaxHP. Re-presses while missing >= healFrac.
/// </summary>
public enum FlaskStyle { Normal, Bubbling, Seething }

public class FlaskCoreSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(true);

    // ---- AutoFlask ----

    [Menu("AutoFlask Enabled")]
    public ToggleNode AutoFlaskEnabled { get; set; } = new(true);

    // Life flask
    [Menu("Life Flask Key")]
    public HotkeyNodeV2 LifeFlaskKey { get; set; } = new(Keys.D1);

    [Menu("Life Flask Style", "Normal=regen / Bubbling=partial instant+regen / Seething=full instant.")]
    public ListNode LifeFlaskStyle { get; set; } = new() { Values = new List<string> { "Normal", "Bubbling", "Seething" }, Value = "Normal" };

    [Menu("Life Flask Heal HP",
        "Total HP recovered per use (flat). Read from flask tooltip. Ex: 920. " +
        "Plugin divides by MaxHP each Tick to derive the trigger threshold automatically.")]
    public RangeNode<int> LifeFlaskHealHp { get; set; } = new(920, 1, 5000);

    [Menu("Life Flask Instant Split %",
        "Bubbling only: % of total heal applied instantly per press. Default 28 (standard Bubbling prefix). " +
        "Ex: flask heals 920HP, split=28 → 258HP instant + 662HP regen. Adjust if prefix differs.")]
    public RangeNode<int> LifeFlaskInstantSplitPct { get; set; } = new(28, 1, 100);

    [JsonIgnore]
    [Menu("Calibrate Life Flask", "Logs MaxHP, computed heal%, instant%, and trigger HP to the console.")]
    public ButtonNode CalibrateLifeFlask { get; set; } = new();

    [Menu("Life Flask Buff ID", "Regen buff substring. Find via Verbose Logging. Unused for Seething.")]
    public TextNode LifeFlaskBuffId { get; set; } = new("flask_effect_life");

    // Mana flask
    [Menu("Mana Flask Key")]
    public HotkeyNodeV2 ManaFlaskKey { get; set; } = new(Keys.D2);

    [Menu("Mana Flask Style")]
    public ListNode ManaFlaskStyle { get; set; } = new() { Values = new List<string> { "Normal", "Bubbling", "Seething" }, Value = "Normal" };

    [Menu("Mana Flask Heal Mana",
        "Total mana recovered per use (flat). Read from flask tooltip. Ex: 500.")]
    public RangeNode<int> ManaFlaskHealMana { get; set; } = new(500, 1, 5000);

    [Menu("Mana Flask Instant Split %", "Bubbling only: instant portion %. Default 28.")]
    public RangeNode<int> ManaFlaskInstantSplitPct { get; set; } = new(28, 1, 100);

    [JsonIgnore]
    [Menu("Calibrate Mana Flask", "Logs MaxMana, computed heal%, instant%, and trigger mana to the console.")]
    public ButtonNode CalibrateManaFlask { get; set; } = new();

    [Menu("Mana Flask Buff ID", "Regen buff substring. Find via Verbose Logging. Unused for Seething.")]
    public TextNode ManaFlaskBuffId { get; set; } = new("flask_effect_mana");

    // Shared
    [Menu("Normal/Bubbling Cooldown (ms)", "Min ms between presses. Guards window before buff registers.")]
    public RangeNode<int> FlaskCooldownMs { get; set; } = new(500, 100, 3000);

    [Menu("Seething Re-use Cooldown (ms)", "Min ms between Seething re-presses during burst.")]
    public RangeNode<int> SeethingCooldownMs { get; set; } = new(200, 100, 2000);

    [Menu("Flask Inventory Index Override", "-1 = auto-detect.")]
    public RangeNode<int> FlaskInventoryIndex { get; set; } = new(-1, -1, 30);

    [Menu("Skip if UI Open")]
    public ToggleNode SkipIfUiOpen { get; set; } = new(true);

    // ---- PanicMode ----

    [Menu("PanicMode Enabled", "ESC when HP < threshold → exit to char select, avoid XP loss.")]
    public ToggleNode PanicModeEnabled { get; set; } = new(true);

    [Menu("Panic HP Threshold %")]
    public RangeNode<int> PanicThreshold { get; set; } = new(10, 1, 50);

    [Menu("Panic Recovery Buffer %", "HP must recover above threshold+buffer before panic re-arms.")]
    public RangeNode<int> PanicRecoveryBuffer { get; set; } = new(15, 5, 40);

    [Menu("Panic ESC Press Count")]
    public RangeNode<int> PanicEscPressCount { get; set; } = new(1, 1, 3);

    [Menu("Panic ESC Delay (ms)")]
    public RangeNode<int> PanicEscDelayMs { get; set; } = new(200, 50, 1000);

    // ---- Debug ----

    [Menu("Debug Overlay")]
    public ToggleNode DebugOverlay { get; set; } = new(true);

    [Menu("Verbose Logging", "Log buff IDs on flask press — use to find correct buff ID strings.")]
    public ToggleNode VerboseLogging { get; set; } = new(false);
}
