using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using ExileCore2;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Nodes;

namespace FlaskCore;

/// <summary>
/// Flask automation + PanicMode for PoE2.
///
/// Core principle — efficiency over threshold:
///   Only press when HP/mana missing >= heal per use, so charges are never wasted.
///   Trigger condition: current% < (100% - healPct% + tolerancePct%)
///
/// NORMAL (regen, 3s buff):
///   Press when HP missing >= healPct AND buff not active.
///   Single press; buff guard prevents re-press during regen.
///
/// BUBBLING (~28% instant + ~72% regen, 3s buff):
///   Press when HP missing >= healPct AND buff not active.  [normal trigger]
///   Re-press when HP missing >= instantPct even if buff active. [burst re-use during heavy damage]
///   The instant portion is always worth using if HP missing >= instantPct.
///
/// SEETHING (100% instant, no buff, ~50% total recovery):
///   Press when HP missing >= healPct.  [first use]
///   Immediately re-press after cooldown if HP missing still >= healPct.  [second use]
///   Keep repeating until HP missing < healPct (next press would overflow → stop).
///   This naturally handles 50% HP situations: press 2× for ~100% recovery.
///   Handles 30% HP: press 3× if healPct=23%, etc.
///   No buff to track — cooldown is the only gate between presses.
///
/// PanicMode: ESC at 10% HP → exit to character select, avoid XP loss.
/// </summary>
public class FlaskCore : BaseSettingsPlugin<FlaskCoreSettings>
{
    public static FlaskCore Main;

    private enum FlaskType { Life, Mana }

    // Flask slot cache (~1s refresh)
    private ServerInventory.InventSlotItem _lifeFlaskSlot;
    private ServerInventory.InventSlotItem _manaFlaskSlot;
    private DateTime _flaskSlotRefreshedAt = DateTime.MinValue;
    private const double FlaskSlotRefreshMs = 1000;

    // Per-slot cooldowns
    private DateTime _lifeFlaskLastUsed = DateTime.MinValue;
    private DateTime _manaFlaskLastUsed = DateTime.MinValue;

    // Debug
    private string _debugLifeStatus = "-";
    private string _debugManaStatus = "-";

    // PanicMode
    private bool _panicTriggered = false;
    private readonly Queue<DateTime> _panicEscQueue = new();

    // Perf timing (exposed via PluginBridge for PerfWatchdog)
    private long _lastTickUs;
    private long _lastRenderUs;
    private readonly Stopwatch _swTick   = new();
    private readonly Stopwatch _swRender = new();

    // MapState bridge
    private Func<bool>  _msIsInTown;
    private Func<bool>  _msIsInHideout;
    private Func<float> _msHpPercent;
    private Func<float> _msManaPercent;
    private Func<bool>  _msPlayerAlive;
    private DateTime    _bridgeResolvedAt = DateTime.MinValue;
    private const double BridgeResolveMs = 5000;

    public FlaskCore() { Name = "FlaskCore"; }

    // ---- Lifecycle ----

    public override bool Initialise()
    {
        Main = this;

        Input.RegisterKey(Settings.LifeFlaskKey.Value);
        Input.RegisterKey(Settings.ManaFlaskKey.Value);
        Settings.LifeFlaskKey.OnValueChanged += () => Input.RegisterKey(Settings.LifeFlaskKey.Value);
        Settings.ManaFlaskKey.OnValueChanged += () => Input.RegisterKey(Settings.ManaFlaskKey.Value);

        GameController.PluginBridge.SaveMethod("FlaskCore.LifeFlaskReady", (Func<bool>)LifeFlaskReady);
        GameController.PluginBridge.SaveMethod("FlaskCore.ManaFlaskReady", (Func<bool>)ManaFlaskReady);
        GameController.PluginBridge.SaveMethod("FlaskCore.IsPanicArmed",   (Func<bool>)(() => !_panicTriggered));
        GameController.PluginBridge.SaveMethod("FlaskCore.GetTickUs",      (Func<long>)(() => _lastTickUs));
        GameController.PluginBridge.SaveMethod("FlaskCore.GetRenderUs",    (Func<long>)(() => _lastRenderUs));

        Settings.CalibrateLifeFlask.OnPressed += LogLifeCalibration;
        Settings.CalibrateManaFlask.OnPressed += LogManaCalibration;

        LogMessage("[FlaskCore] initialised.");
        return true;
    }

    public override void Tick()
    {
        _swTick.Restart();
        try
        {
            if (!Settings.Enable) return;

            ResolveBridgeDelegates();
            DrainPanicEscQueue();

            if (!IsInCombatZone()) { _panicTriggered = false; return; }
            if (!GetPlayerAlive()) return;

            RefreshFlaskSlotsIfNeeded();

            if (Settings.AutoFlaskEnabled) TickAutoFlask();
            if (Settings.PanicModeEnabled) TickPanicMode();
        }
        finally
        {
            _swTick.Stop();
            _lastTickUs = _swTick.Elapsed.Microseconds;
        }
    }

    public override void Render()
    {
        _swRender.Restart();
        try
        {
        if (!Settings.Enable || !Settings.DebugOverlay) return;

        float hp     = GetHpPercent();
        float mana   = GetManaPercent();
        bool  inZone = IsInCombatZone();

        // Absolute HP/mana for display + trigger thresholds
        var player = GameController?.Player;
        int maxHp = 0, curHp = 0, maxMana = 0, curMana = 0;
        if (player != null && player.TryGetComponent<Life>(out var lifeComp))
        {
            maxHp = lifeComp.MaxHP; curHp = lifeComp.CurHP;
            maxMana = lifeComp.MaxMana; curMana = lifeComp.CurMana;
        }

        var lifeStyle = ParseStyle(Settings.LifeFlaskStyle.Value);
        var manaStyle = ParseStyle(Settings.ManaFlaskStyle.Value);

        int lifeInstantHp = Settings.LifeFlaskHealHp.Value * Settings.LifeFlaskInstantSplitPct.Value / 100;
        int lifeRegenHp   = Settings.LifeFlaskHealHp.Value - lifeInstantHp;

        float lifeHealFrac = maxHp > 0 ? Settings.LifeFlaskHealHp.Value  / (float)maxHp : 0f;
        float lifeInstFrac = lifeHealFrac * Settings.LifeFlaskInstantSplitPct.Value / 100f;
        float manaHealFrac = maxMana > 0 ? Settings.ManaFlaskHealMana.Value / (float)maxMana : 0f;
        float manaInstFrac = manaHealFrac * Settings.ManaFlaskInstantSplitPct.Value / 100f;

        float lifeTrigger  = lifeStyle == FlaskStyle.Bubbling ? 1f - lifeInstFrac  : 1f - lifeHealFrac;
        float manaTrigger  = manaStyle == FlaskStyle.Bubbling ? 1f - manaInstFrac  : 1f - manaHealFrac;

        bool lifeBuff = HasFlaskBuff(Settings.LifeFlaskBuffId.Value);
        bool manaBuff = HasFlaskBuff(Settings.ManaFlaskBuffId.Value);

        int lifeTriggerHp  = maxHp   > 0 ? (int)(lifeTrigger  * maxHp)   : 0;
        int manaTriggerMana = maxMana > 0 ? (int)(manaTrigger * maxMana) : 0;

        var pos   = new Vector2(12, 290);
        int lineH = 18;
        int y     = 0;

        Graphics.DrawText(
            $"FlaskCore: autoflask={Settings.AutoFlaskEnabled.Value}  panic={Settings.PanicModeEnabled.Value}  zone={(inZone ? "map" : "town/HO")}",
            pos + new Vector2(0, y++ * lineH), Color.Cyan);

        var hpColor   = hp   < lifeTrigger ? Color.Yellow : Color.Green;
        var manaColor = mana < manaTrigger ? Color.Yellow : Color.Green;

        string lifeHealInfo = lifeStyle == FlaskStyle.Bubbling
            ? $"inst=+{lifeInstantHp}HP regen=+{lifeRegenHp}HP"
            : $"heal=+{Settings.LifeFlaskHealHp.Value}HP";
        Graphics.DrawText(
            $"  HP: {curHp}/{maxHp} [{hp:P1}]  [use<{lifeTriggerHp}HP  {lifeHealInfo}]  " +
            $"life({Settings.LifeFlaskStyle.Value ?? "?"}): {_debugLifeStatus}  buff={lifeBuff}  slot={(_lifeFlaskSlot != null ? "ok" : "MISSING")}",
            pos + new Vector2(0, y++ * lineH), hpColor);

        Graphics.DrawText(
            $"  Mana: {curMana}/{maxMana} [{mana:P1}]  [use<{manaTriggerMana}mana  heal=+{Settings.ManaFlaskHealMana.Value}mana]  " +
            $"mana({Settings.ManaFlaskStyle.Value ?? "?"}): {_debugManaStatus}  buff={manaBuff}  slot={(_manaFlaskSlot != null ? "ok" : "MISSING")}",
            pos + new Vector2(0, y++ * lineH), manaColor);

        var panicColor = _panicTriggered ? Color.Red : Color.LightGreen;
        Graphics.DrawText(
            $"  PanicMode: {(_panicTriggered ? "TRIGGERED" : "armed")}  threshold={Settings.PanicThreshold.Value}%  queue={_panicEscQueue.Count}",
            pos + new Vector2(0, y++ * lineH), panicColor);
        }
        finally
        {
            _swRender.Stop();
            _lastRenderUs = _swRender.Elapsed.Microseconds;
        }
    }

    // ---- AutoFlask ----

    private void TickAutoFlask()
    {
        float hp   = GetHpPercent();
        float mana = GetManaPercent();

        var player = GameController?.Player;
        Life lifeComp = null;
        player?.TryGetComponent(out lifeComp);

        int maxHp   = lifeComp?.MaxHP   > 0 ? lifeComp.MaxHP   : 1;
        int maxMana = lifeComp?.MaxMana > 0 ? lifeComp.MaxMana : 1;

        float lifeHealFrac = Settings.LifeFlaskHealHp.Value    / (float)maxHp;
        float lifeInstFrac = lifeHealFrac * Settings.LifeFlaskInstantSplitPct.Value / 100f;
        float manaHealFrac = Settings.ManaFlaskHealMana.Value   / (float)maxMana;
        float manaInstFrac = manaHealFrac * Settings.ManaFlaskInstantSplitPct.Value / 100f;

        EvaluateFlask(FlaskType.Life, hp,
            ParseStyle(Settings.LifeFlaskStyle.Value),
            lifeHealFrac, lifeInstFrac,
            Settings.LifeFlaskBuffId.Value,
            Settings.LifeFlaskKey.Value,
            ref _lifeFlaskLastUsed, ref _debugLifeStatus);

        EvaluateFlask(FlaskType.Mana, mana,
            ParseStyle(Settings.ManaFlaskStyle.Value),
            manaHealFrac, manaInstFrac,
            Settings.ManaFlaskBuffId.Value,
            Settings.ManaFlaskKey.Value,
            ref _manaFlaskLastUsed, ref _debugManaStatus);
    }

    // healFrac and instFrac are pre-computed by TickAutoFlask from flat HP settings / MaxHP.
    private void EvaluateFlask(
        FlaskType type, float current,
        FlaskStyle style,
        float healFrac, float instFrac,
        string buffId, HotkeyNodeV2.HotkeyNodeValue key,
        ref DateTime lastUsed, ref string status)
    {
        var slot = type == FlaskType.Life ? _lifeFlaskSlot : _manaFlaskSlot;
        if (slot == null) { status = "no slot"; return; }

        float missing     = 1f - current;
        bool  buffActive  = HasFlaskBuff(buffId);
        int   cdMs        = style == FlaskStyle.Seething
                            ? Settings.SeethingCooldownMs.Value
                            : Settings.FlaskCooldownMs.Value;
        bool  cdElapsed   = (DateTime.UtcNow - lastUsed).TotalMilliseconds >= cdMs;

        // Normal/Seething: press when full heal fits (missing >= healFrac)
        bool belowTrigger  = missing >= healFrac;
        // Bubbling: press when instant portion fits — regardless of buff state
        // (buff only means regen is running; it never blocks the instant burst)
        bool instantFits   = missing >= instFrac;

        bool shouldPress = style switch
        {
            // NORMAL: full regen fits + buff not already running
            FlaskStyle.Normal =>
                cdElapsed && belowTrigger && !buffActive,

            // BUBBLING: always press when instant fits — chains until HP > (1 - instFrac)
            // buff state irrelevant: instant applies on every press, buff renews each time
            FlaskStyle.Bubbling =>
                cdElapsed && instantFits,

            // SEETHING: press while missing >= heal per use (each re-press still fits)
            FlaskStyle.Seething =>
                cdElapsed && belowTrigger,

            _ => false
        };

        if (!shouldPress)
        {
            if (style == FlaskStyle.Bubbling && !instantFits)         status = "full";
            else if (!belowTrigger)                                    status = "full";
            else if (buffActive && style == FlaskStyle.Normal)         status = "regen active";
            else                                                       status = "cooldown";
            return;
        }

        if (Settings.SkipIfUiOpen && IsAnyUiPanelOpen()) { status = "UI open"; return; }

        Input.KeyPressRelease(key.Key);
        lastUsed = DateTime.UtcNow;

        status = style switch
        {
            FlaskStyle.Seething  => $"burst (miss={missing:P0})",
            FlaskStyle.Bubbling  => $"inst+regen (miss={missing:P0})",
            _                    => "used"
        };

        if (Settings.VerboseLogging)
        {
            var player = GameController?.Player;
            if (player != null && player.TryGetComponent<Buffs>(out var buffs))
            {
                var list = buffs.BuffsList?.Select(b => b?.Name ?? "null") ?? Enumerable.Empty<string>();
                LogMessage($"[FlaskCore] {type}({style}) at {current:P0} missing={missing:P0} heal={healFrac:P0}. Buffs: {string.Join(", ", list)}");
            }
        }
    }

    // ---- PanicMode ----

    private void TickPanicMode()
    {
        float hp          = GetHpPercent();
        float panicPct    = Settings.PanicThreshold.Value / 100f;
        float recoveryPct = panicPct + Settings.PanicRecoveryBuffer.Value / 100f;

        if (!_panicTriggered)
        {
            if (hp < panicPct)
            {
                _panicTriggered = true;
                LogMessage($"[FlaskCore] PANIC at hp={hp:P1}", 5f);
                for (int i = 0; i < Settings.PanicEscPressCount.Value; i++)
                    _panicEscQueue.Enqueue(DateTime.UtcNow.AddMilliseconds(i * Settings.PanicEscDelayMs.Value));
            }
        }
        else
        {
            if (hp >= recoveryPct) _panicTriggered = false;
        }
    }

    private void DrainPanicEscQueue()
    {
        var now = DateTime.UtcNow;
        while (_panicEscQueue.Count > 0 && now >= _panicEscQueue.Peek())
        {
            _panicEscQueue.Dequeue();
            Input.KeyPressRelease(Keys.Escape);
            LogMessage("[FlaskCore] PANIC ESC.", 3f);
        }
    }

    // ---- Flask slot detection ----

    private void RefreshFlaskSlotsIfNeeded()
    {
        var now = DateTime.UtcNow;
        if ((now - _flaskSlotRefreshedAt).TotalMilliseconds < FlaskSlotRefreshMs) return;
        _flaskSlotRefreshedAt = now;

        _lifeFlaskSlot = null;
        _manaFlaskSlot = null;

        var inventories = GameController?.IngameState?.ServerData?.PlayerInventories;
        if (inventories == null) return;

        IEnumerable<ServerInventory.InventSlotItem> candidates = null;

        int idxOverride = Settings.FlaskInventoryIndex.Value;
        if (idxOverride >= 0 && idxOverride < inventories.Count)
            candidates = inventories[idxOverride]?.Inventory?.InventorySlotItems;

        if (candidates == null || !candidates.Any())
        {
            candidates = inventories
                .SelectMany(pi => pi?.Inventory?.InventorySlotItems ?? Enumerable.Empty<ServerInventory.InventSlotItem>())
                .Where(s => s?.Item?.Path?.Contains("Flask") == true);
        }

        foreach (var slot in candidates)
        {
            if (slot?.Item == null) continue;
            var path = slot.Item.Path ?? "";

            if (_lifeFlaskSlot == null &&
                (path.Contains("LifeFlask", StringComparison.OrdinalIgnoreCase) ||
                 path.Contains("life_flask", StringComparison.OrdinalIgnoreCase)))
            { _lifeFlaskSlot = slot; continue; }

            if (_manaFlaskSlot == null &&
                (path.Contains("ManaFlask", StringComparison.OrdinalIgnoreCase) ||
                 path.Contains("mana_flask", StringComparison.OrdinalIgnoreCase)))
            { _manaFlaskSlot = slot; continue; }

            if (_lifeFlaskSlot == null && slot.PosX == 0) { _lifeFlaskSlot = slot; continue; }
            if (_manaFlaskSlot == null && slot.PosX == 1) { _manaFlaskSlot = slot; continue; }
        }
    }

    // ---- Helpers ----

    private bool HasFlaskBuff(string buffId)
    {
        if (string.IsNullOrEmpty(buffId)) return false;
        var player = GameController?.Player;
        if (player == null || !player.TryGetComponent<Buffs>(out var buffs)) return false;
        var list = buffs.BuffsList;
        if (list == null) return false;
        for (int i = 0; i < list.Count; i++)
        {
            var name = list[i]?.Name;
            if (name != null && name.IndexOf(buffId, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    private void LogLifeCalibration()
    {
        var life = GameController?.Player?.GetComponent<Life>();
        if (life == null) { LogMessage("[FlaskCore] Calibrate: no Life component"); return; }
        float healFrac = Settings.LifeFlaskHealHp.Value / (float)life.MaxHP;
        float instFrac = healFrac * Settings.LifeFlaskInstantSplitPct.Value / 100f;
        int   triggerHp = life.MaxHP - (int)(Settings.LifeFlaskHealHp.Value * Settings.LifeFlaskInstantSplitPct.Value / 100f);
        int   instantHp = Settings.LifeFlaskHealHp.Value * Settings.LifeFlaskInstantSplitPct.Value / 100;
        int   regenHp   = Settings.LifeFlaskHealHp.Value - instantHp;
        LogMessage($"[FlaskCore] Life calibration: MaxHP={life.MaxHP}  HealHP={Settings.LifeFlaskHealHp.Value}" +
                   $"  healFrac={healFrac:P1}  instFrac={instFrac:P1}" +
                   $"  triggerHP={triggerHp}  instant=+{instantHp}HP regen=+{regenHp}HP");
    }

    private void LogManaCalibration()
    {
        var life = GameController?.Player?.GetComponent<Life>();
        if (life == null) { LogMessage("[FlaskCore] Calibrate: no Life component"); return; }
        int maxMana = life.MaxMana > 0 ? life.MaxMana : 1;
        float healFrac = Settings.ManaFlaskHealMana.Value / (float)maxMana;
        float instFrac = healFrac * Settings.ManaFlaskInstantSplitPct.Value / 100f;
        int   triggerMana = maxMana - (int)(Settings.ManaFlaskHealMana.Value * Settings.ManaFlaskInstantSplitPct.Value / 100f);
        int   instantMana = Settings.ManaFlaskHealMana.Value * Settings.ManaFlaskInstantSplitPct.Value / 100;
        int   regenMana   = Settings.ManaFlaskHealMana.Value - instantMana;
        LogMessage($"[FlaskCore] Mana calibration: MaxMana={maxMana}  HealMana={Settings.ManaFlaskHealMana.Value}" +
                   $"  healFrac={healFrac:P1}  instFrac={instFrac:P1}" +
                   $"  triggerMana={triggerMana}  instant=+{instantMana}mana regen=+{regenMana}mana");
    }

    private bool LifeFlaskReady()
    {
        if (_lifeFlaskSlot == null) return false;
        if (ParseStyle(Settings.LifeFlaskStyle.Value) == FlaskStyle.Seething) return true;
        return !HasFlaskBuff(Settings.LifeFlaskBuffId.Value);
    }

    private bool ManaFlaskReady()
    {
        if (_manaFlaskSlot == null) return false;
        if (ParseStyle(Settings.ManaFlaskStyle.Value) == FlaskStyle.Seething) return true;
        return !HasFlaskBuff(Settings.ManaFlaskBuffId.Value);
    }

    private static FlaskStyle ParseStyle(string v) =>
        v switch { "Bubbling" => FlaskStyle.Bubbling, "Seething" => FlaskStyle.Seething, _ => FlaskStyle.Normal };

    private bool IsInCombatZone()
    {
        bool inTown    = _msIsInTown?.Invoke()    ?? GameController?.Area?.CurrentArea?.IsTown    ?? true;
        bool inHideout = _msIsInHideout?.Invoke() ?? GameController?.Area?.CurrentArea?.IsHideout ?? true;
        return !inTown && !inHideout;
    }

    private float GetHpPercent()
    {
        if (_msHpPercent != null) return _msHpPercent.Invoke();
        var player = GameController?.Player;
        if (player == null || !player.TryGetComponent<Life>(out var life) || life.MaxHP <= 0) return 1f;
        return (float)life.CurHP / life.MaxHP;
    }

    private float GetManaPercent()
    {
        if (_msManaPercent != null) return _msManaPercent.Invoke();
        var player = GameController?.Player;
        if (player == null || !player.TryGetComponent<Life>(out var life)) return 1f;
        // Use UNRESERVED max mana, not total MaxMana. On heavy-reservation builds (e.g. this Deadeye:
        // Herald + Ghost Dance + Wind Dancer + Lingering Mirage reserve ~50%), CurMana/MaxMana can never
        // exceed ~0.5, so the trigger (mana < 54%) was permanently true and the mana flask key (D2) fired
        // every cooldown forever — even at full usable mana. Mana.Unreserved matches the in-game globe.
        double max = life.Mana.Unreserved;
        if (max <= 0) return 1f;
        return (float)(life.Mana.Current / max);
    }

    private bool GetPlayerAlive()
    {
        if (_msPlayerAlive != null) return _msPlayerAlive.Invoke();
        var player = GameController?.Player;
        if (player == null || !player.TryGetComponent<Life>(out var life)) return false;
        return life.CurHP > 0;
    }

    private bool IsAnyUiPanelOpen()
    {
        var ui = GameController?.IngameState?.IngameUi;
        if (ui == null) return false;
        return ui.FullscreenPanels?.Any(x => x.IsVisible) == true ||
               ui.OpenLeftPanel?.IsVisible  == true ||
               ui.OpenRightPanel?.IsVisible == true;
    }

    private void ResolveBridgeDelegates()
    {
        var now = DateTime.UtcNow;
        if ((now - _bridgeResolvedAt).TotalMilliseconds < BridgeResolveMs) return;

        _msIsInTown    = GameController.PluginBridge.GetMethod<Func<bool>>("MapState.IsInTown");
        _msIsInHideout = GameController.PluginBridge.GetMethod<Func<bool>>("MapState.IsInHideout");
        _msHpPercent   = GameController.PluginBridge.GetMethod<Func<float>>("MapState.HpPercent");
        _msManaPercent = GameController.PluginBridge.GetMethod<Func<float>>("MapState.ManaPercent");
        _msPlayerAlive = GameController.PluginBridge.GetMethod<Func<bool>>("MapState.PlayerAlive");
        _bridgeResolvedAt = now;
    }
}
