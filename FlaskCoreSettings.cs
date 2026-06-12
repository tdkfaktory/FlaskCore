using System.Collections.Generic;
using System.Windows.Forms;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using System.Text.Json.Serialization;

namespace FlaskCore;

public enum FlaskStyle { Normal, Bubbling, Seething }

public class FlaskCoreSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(true);

    // ---- AutoFlask ----

    [Menu("AutoFlask Enabled")]
    public ToggleNode AutoFlaskEnabled { get; set; } = new(true);

    // --- Life Flask ---

    [Menu("Life Flask Key")]
    public HotkeyNodeV2 LifeFlaskKey { get; set; } = new(Keys.D1);

    [Menu("Life Flask Style", "Normal=regen over time / Bubbling=28% instant+regen / Seething=100% instant")]
    public ListNode LifeFlaskStyle { get; set; } = new() { Values = new List<string> { "Normal", "Bubbling", "Seething" }, Value = "Normal" };

    [Menu("Use when HP below %",
        "Normal only: press flask when HP drops below this %. Ex: 70 = use when below 70% HP.")]
    public RangeNode<int> LifeTriggerPct { get; set; } = new(70, 10, 99);

    [Menu("Life Flask Heals (HP)",
        "Bubbling/Seething: total HP recovered per use. Read from flask tooltip. Ex: 920. " +
        "Trigger is calculated automatically — press when missing HP >= instant heal amount.")]
    public RangeNode<int> LifeFlaskHealHp { get; set; } = new(920, 1, 5000);

    [JsonIgnore]
    [Menu("Calibrate Life Flask", "Logs MaxHP, trigger threshold, and heal breakdown to console.")]
    public ButtonNode CalibrateLifeFlask { get; set; } = new();

    // --- Mana Flask ---

    [Menu("Mana Flask Key")]
    public HotkeyNodeV2 ManaFlaskKey { get; set; } = new(Keys.D2);

    [Menu("Mana Flask Style", "Normal=regen over time / Bubbling=28% instant+regen / Seething=100% instant")]
    public ListNode ManaFlaskStyle { get; set; } = new() { Values = new List<string> { "Normal", "Bubbling", "Seething" }, Value = "Normal" };

    [Menu("Use when Mana below %",
        "Normal only: press flask when mana drops below this %. Ex: 50 = use when below 50% mana.")]
    public RangeNode<int> ManaTriggerPct { get; set; } = new(50, 10, 99);

    [Menu("Mana Flask Heals (Mana)",
        "Bubbling/Seething: total mana recovered per use. Read from flask tooltip. Ex: 500.")]
    public RangeNode<int> ManaFlaskHealMana { get; set; } = new(500, 1, 5000);

    [JsonIgnore]
    [Menu("Calibrate Mana Flask", "Logs MaxMana, trigger threshold, and heal breakdown to console.")]
    public ButtonNode CalibrateManaFlask { get; set; } = new();

    // ---- PanicMode ----

    [Menu("PanicMode Enabled", "Press ESC when HP < threshold to exit to character select and avoid XP loss.")]
    public ToggleNode PanicModeEnabled { get; set; } = new(true);

    [Menu("Panic HP Threshold %", "ESC fires when HP drops below this value.")]
    public RangeNode<int> PanicThreshold { get; set; } = new(10, 1, 50);

    // ---- Debug ----

    [Menu("Debug Overlay")]
    public ToggleNode DebugOverlay { get; set; } = new(true);

    // ---- Advanced (rarely changed) ----

    [Menu("Advanced", "Settings that rarely need to change.")]
    public AdvancedSettings Advanced { get; set; } = new();
}

[Submenu(CollapsedByDefault = true)]
public class AdvancedSettings
{
    [Menu("Life Flask Instant Split %",
        "Bubbling: % of total heal applied instantly per press. Default 28 (standard prefix).")]
    public RangeNode<int> LifeFlaskInstantSplitPct { get; set; } = new(28, 1, 100);

    [Menu("Mana Flask Instant Split %", "Bubbling: instant portion %. Default 28.")]
    public RangeNode<int> ManaFlaskInstantSplitPct { get; set; } = new(28, 1, 100);

    [Menu("Life Flask Buff ID", "Regen buff substring for buff-guard. Find via Verbose Logging.")]
    public TextNode LifeFlaskBuffId { get; set; } = new("flask_effect_life");

    [Menu("Mana Flask Buff ID", "Regen buff substring for buff-guard. Find via Verbose Logging.")]
    public TextNode ManaFlaskBuffId { get; set; } = new("flask_effect_mana");

    [Menu("Flask Cooldown (ms)", "Min ms between presses for Normal flasks.")]
    public RangeNode<int> FlaskCooldownMs { get; set; } = new(500, 100, 3000);

    [Menu("Bubbling Cooldown (ms)",
        "Min ms between Bubbling re-presses. Keep short (~300ms) so burst presses fire before the " +
        "server applies the regen buff — allows 3× instant heal in rapid succession.")]
    public RangeNode<int> BubblingCooldownMs { get; set; } = new(300, 50, 2000);

    [Menu("Seething Re-use Cooldown (ms)",
        "Min ms between Seething re-presses. Seething is 100% instant with no regen buff — " +
        "keep very short so 2-3 charges fire immediately when HP is critically low.")]
    public RangeNode<int> SeethingCooldownMs { get; set; } = new(150, 50, 1000);

    [Menu("Skip if UI Open")]
    public ToggleNode SkipIfUiOpen { get; set; } = new(true);

    [Menu("Panic Recovery Buffer %", "HP must reach threshold+buffer before panic re-arms.")]
    public RangeNode<int> PanicRecoveryBuffer { get; set; } = new(15, 5, 40);

    [Menu("Panic ESC Press Count")]
    public RangeNode<int> PanicEscPressCount { get; set; } = new(1, 1, 3);

    [Menu("Panic ESC Delay (ms)")]
    public RangeNode<int> PanicEscDelayMs { get; set; } = new(200, 50, 1000);

    [Menu("Flask Inventory Index Override", "-1 = auto-detect.")]
    public RangeNode<int> FlaskInventoryIndex { get; set; } = new(-1, -1, 30);

    [Menu("Verbose Logging", "Log buff IDs on flask press — use to find correct buff ID strings.")]
    public ToggleNode VerboseLogging { get; set; } = new(false);

    [Menu("File Logging", "Write press/skip/charges/HP diagnostics to flaskcore_debug.log for offline analysis.")]
    public ToggleNode FileLogging { get; set; } = new(true);
}
