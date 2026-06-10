using System.Collections.Generic;
using System.Windows.Forms;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;

namespace FlaskCore;

/// <summary>
/// The user configures only how much each flask heals. Trigger thresholds are derived automatically
/// to guarantee maximum HP recovery without wasting charges.
///
/// NORMAL (regen over ~3s, has buff):
///   Trigger = HP < (100 - HealPct). Heal fits exactly in the missing HP.
///   Guard: don't press while buff active (regen already running).
///   Example: HealPct=46 → press when HP < 54%.
///
/// BUBBLING (~28% instant + ~72% regen, has buff):
///   Main trigger = HP < (100 - HealPct).  [same as Normal — full heal fits]
///   Emergency re-press = HP < (100 - InstantHealPct) even with buff active.
///   Example: HealPct=46, InstantHealPct=13 → main at HP<54%, re-press at HP<87%.
///
/// SEETHING (100% instant, no buff, ~50% recovery per use):
///   Trigger = HP < (100 - HealPct).  [one use fits without overflow]
///   Re-press after cooldown while HP still missing >= HealPct.
///   Naturally chains: at 30% HP with HealPct=23 → presses 3×.
///   Example: HealPct=23 → press at HP<77%; re-press if still missing>=23%.
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

    [Menu("Life Flask Heal % per use",
        "How much HP one use recovers (% of MaxHP). " +
        "Plugin auto-calculates when to press so no charges are wasted. " +
        "Normal/Bubbling: full heal e.g. 46%. Seething: ~23% (half the total).")]
    public RangeNode<int> LifeFlaskHealPct { get; set; } = new(46, 1, 99);

    [Menu("Life Flask Instant Heal % per use",
        "Bubbling only: the instant portion heals this % of MaxHP per press. " +
        "This is ALSO the trigger threshold — flask presses whenever HP missing >= this value. " +
        "Formula: flask_heal_hp * 0.28 / MaxHP. " +
        "Ex: flask=920HP, MaxHP=1648 → 920*0.28/1648 = 15.6% → set 16.")]
    public RangeNode<int> LifeFlaskInstantHealPct { get; set; } = new(16, 1, 50);

    [Menu("Life Flask Buff ID", "Regen buff substring. Find via Verbose Logging. Unused for Seething.")]
    public TextNode LifeFlaskBuffId { get; set; } = new("flask_effect_life");

    // Mana flask
    [Menu("Mana Flask Key")]
    public HotkeyNodeV2 ManaFlaskKey { get; set; } = new(Keys.D2);

    [Menu("Mana Flask Style")]
    public ListNode ManaFlaskStyle { get; set; } = new() { Values = new List<string> { "Normal", "Bubbling", "Seething" }, Value = "Normal" };

    [Menu("Mana Flask Heal % per use", "How much mana one use recovers (% of MaxMana).")]
    public RangeNode<int> ManaFlaskHealPct { get; set; } = new(46, 1, 99);

    [Menu("Mana Flask Instant Heal % per use", "Bubbling only: instant burst portion (% of MaxMana).")]
    public RangeNode<int> ManaFlaskInstantHealPct { get; set; } = new(13, 1, 50);

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
