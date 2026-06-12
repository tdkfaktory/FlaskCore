using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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

    // File logging — everything the overlay shows (and more) goes to flaskcore_debug.log
    // so behaviour can be analysed after the session without watching the screen in combat.
    private string _logFilePath;
    private readonly List<string> _logBuffer = new();
    private DateTime _lastLogFlushAt = DateTime.MinValue;
    private readonly DateTime[] _lastSkipLogAt = new DateTime[2]; // indexed by FlaskType
    private DateTime _lastHpSourceCheckAt = DateTime.MinValue;
    private DateTime _lastStateLogAt = DateTime.MinValue;

    // Press verification: re-check buff state ~700ms after each press to detect no-op presses.
    // Charges are NOT reliable: in kill-heavy combat they refill faster than the verify window.
    // Buff appearance is the only reliable proof-of-use (seething flasks have no buff — fall back to charges).
    private sealed record PendingPress(FlaskType Type, DateTime At, float Before, int ChargesBefore, string BuffId);
    private readonly List<PendingPress> _pendingPresses = new();
    private const double PressVerifyDelayMs = 700;

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

        _logFilePath = Path.Combine(DirectoryFullName, "flaskcore_debug.log");
        try
        {
            if (File.Exists(_logFilePath) && new FileInfo(_logFilePath).Length > 2_000_000)
                File.Delete(_logFilePath);
        }
        catch { /* rotation is best-effort */ }
        FileLog($"=== session start | life={Settings.LifeFlaskStyle.Value} healHp={Settings.LifeFlaskHealHp.Value} " +
                $"instSplit={Settings.Advanced.LifeFlaskInstantSplitPct.Value}% " +
                $"bubblingCd={Settings.Advanced.BubblingCooldownMs.Value}ms normalCd={Settings.Advanced.FlaskCooldownMs.Value}ms ===");

        LogMessage($"[FlaskCore] initialised. Debug log: {_logFilePath}");
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
            if (!GetPlayerAlive())
            {
                // Dead character — game ignores all flask inputs; stop pressing and drain
                // any pending presses so the post-death verify loop doesn't spam the log.
                if (_pendingPresses.Count > 0)
                {
                    FileLog($"[DEAD] clearing {_pendingPresses.Count} pending press(es) — character is dead");
                    _pendingPresses.Clear();
                }
                return;
            }

            RefreshFlaskSlotsIfNeeded();

            if (Settings.AutoFlaskEnabled) TickAutoFlask();
            if (Settings.PanicModeEnabled) TickPanicMode();

            VerifyPendingPresses();
            LogHpSourceDivergence();
            LogStateHeartbeat();
        }
        finally
        {
            FlushFileLogIfNeeded();
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

        // Life trigger display
        float lifeHealFracR, lifeInstFracR;
        if (lifeStyle == FlaskStyle.Normal)
        {
            lifeHealFracR = Settings.LifeTriggerPct.Value / 100f;
            lifeInstFracR = lifeHealFracR;
        }
        else
        {
            lifeHealFracR = maxHp > 0 ? Settings.LifeFlaskHealHp.Value / (float)maxHp : 0f;
            lifeInstFracR = lifeHealFracR * Settings.Advanced.LifeFlaskInstantSplitPct.Value / 100f;
        }
        int lifeInstantHp = Settings.LifeFlaskHealHp.Value * Settings.Advanced.LifeFlaskInstantSplitPct.Value / 100;
        int lifeRegenHp   = Settings.LifeFlaskHealHp.Value - lifeInstantHp;
        float lifeTrigger = lifeStyle == FlaskStyle.Bubbling ? 1f - lifeInstFracR : 1f - lifeHealFracR;
        int lifeTriggerHp = maxHp > 0 ? (int)(lifeTrigger * maxHp) : 0;

        // Mana trigger display
        float manaHealFracR, manaInstFracR;
        if (manaStyle == FlaskStyle.Normal)
        {
            manaHealFracR = Settings.ManaTriggerPct.Value / 100f;
            manaInstFracR = manaHealFracR;
        }
        else
        {
            manaHealFracR = maxMana > 0 ? Settings.ManaFlaskHealMana.Value / (float)maxMana : 0f;
            manaInstFracR = manaHealFracR * Settings.Advanced.ManaFlaskInstantSplitPct.Value / 100f;
        }
        float manaTrigger   = manaStyle == FlaskStyle.Bubbling ? 1f - manaInstFracR : 1f - manaHealFracR;
        int manaTriggerMana = maxMana > 0 ? (int)(manaTrigger * maxMana) : 0;

        bool lifeBuff = HasFlaskBuff(Settings.Advanced.LifeFlaskBuffId.Value);
        bool manaBuff = HasFlaskBuff(Settings.Advanced.ManaFlaskBuffId.Value);

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

        var lifeStyle = ParseStyle(Settings.LifeFlaskStyle.Value);
        var manaStyle = ParseStyle(Settings.ManaFlaskStyle.Value);

        // Normal: user sets trigger % directly. Bubbling/Seething: derive from flat heal / MaxHP.
        float lifeHealFrac, lifeInstFrac;
        if (lifeStyle == FlaskStyle.Normal)
        {
            lifeHealFrac = Settings.LifeTriggerPct.Value / 100f;
            lifeInstFrac = lifeHealFrac;
        }
        else
        {
            lifeHealFrac = Settings.LifeFlaskHealHp.Value / (float)maxHp;
            lifeInstFrac = lifeHealFrac * Settings.Advanced.LifeFlaskInstantSplitPct.Value / 100f;
        }

        float manaHealFrac, manaInstFrac;
        if (manaStyle == FlaskStyle.Normal)
        {
            manaHealFrac = Settings.ManaTriggerPct.Value / 100f;
            manaInstFrac = manaHealFrac;
        }
        else
        {
            manaHealFrac = Settings.ManaFlaskHealMana.Value / (float)maxMana;
            manaInstFrac = manaHealFrac * Settings.Advanced.ManaFlaskInstantSplitPct.Value / 100f;
        }

        EvaluateFlask(FlaskType.Life, hp,
            lifeStyle,
            lifeHealFrac, lifeInstFrac,
            Settings.Advanced.LifeFlaskBuffId.Value,
            Settings.LifeFlaskKey.Value,
            ref _lifeFlaskLastUsed, ref _debugLifeStatus);

        EvaluateFlask(FlaskType.Mana, mana,
            manaStyle,
            manaHealFrac, manaInstFrac,
            Settings.Advanced.ManaFlaskBuffId.Value,
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
        var prevStatus = status;
        try
        {
        var slot = type == FlaskType.Life ? _lifeFlaskSlot : _manaFlaskSlot;
        if (slot == null) { status = "no slot"; return; }

        var (chgCur, chgMax, chgPerUse) = GetCharges(slot);

        float missing     = 1f - current;
        bool  buffActive  = HasFlaskBuff(buffId);
        int   cdMs = style switch
        {
            FlaskStyle.Seething  => Settings.Advanced.SeethingCooldownMs.Value,
            // Bubbling: use dedicated CD to match PoE2's internal per-slot cooldown.
            // Default 2000ms prevents spamming every 500ms while the game ignores presses during buff.
            FlaskStyle.Bubbling  => Settings.Advanced.BubblingCooldownMs.Value,
            _                    => Settings.Advanced.FlaskCooldownMs.Value,
        };
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

        // A flask "wants" to fire when the heal would fit — any skip in that state
        // is exactly what we need on record during burst damage.
        bool wantsPress = style == FlaskStyle.Bubbling ? instantFits : belowTrigger;

        if (!shouldPress)
        {
            if (style == FlaskStyle.Bubbling && !instantFits)         status = "full";
            else if (!belowTrigger)                                    status = "full";
            else if (buffActive && style == FlaskStyle.Normal)         status = "regen active";
            else                                                       status = "cooldown";

            if (wantsPress)
                LogSkipThrottled(type,
                    $"[{type}/{style}] SKIP {status} miss={missing:P0} need>={(style == FlaskStyle.Bubbling ? instFrac : healFrac):P0} " +
                    $"sinceLast={(DateTime.UtcNow - lastUsed).TotalMilliseconds:F0}ms chg={chgCur}/{chgMax}");
            return;
        }

        if (Settings.Advanced.SkipIfUiOpen && IsAnyUiPanelOpen())
        {
            status = "UI open";
            LogSkipThrottled(type, $"[{type}/{style}] SKIP UI open miss={missing:P0} chg={chgCur}/{chgMax}");
            return;
        }

        Input.KeyPressRelease(key.Key);
        lastUsed = DateTime.UtcNow;

        status = style switch
        {
            FlaskStyle.Seething  => $"burst (miss={missing:P0})",
            FlaskStyle.Bubbling  => $"inst+regen (miss={missing:P0})",
            _                    => "used"
        };

        FileLog($"[{type}/{style}] PRESS at={current:P1} miss={missing:P0} healFrac={healFrac:P0} instFrac={instFrac:P0} " +
                $"chg={chgCur}/{chgMax} perUse={chgPerUse}");
        _pendingPresses.Add(new PendingPress(type, DateTime.UtcNow, current, chgCur, buffId));

        if (Settings.Advanced.VerboseLogging)
        {
            var player = GameController?.Player;
            if (player != null && player.TryGetComponent<Buffs>(out var buffs))
            {
                var allBuffs = buffs.BuffsList?.Select(b => b?.Name ?? "null") ?? Enumerable.Empty<string>();
                var buffStr = string.Join(", ", allBuffs);
                LogMessage($"[FlaskCore] {type}({style}) at {current:P0} missing={missing:P0} heal={healFrac:P0}. Buffs: {buffStr}");
                // Also write to file — critical for finding the correct Bubbling buff ID offline.
                FileLog($"[VERBOSE/{type}] press at={current:P1} buffs=[{buffStr}]");
            }
        }
        }
        finally
        {
            if (status != prevStatus)
                FileLog($"[{type}] status: {prevStatus} -> {status}");
        }
    }

    // ---- PanicMode ----

    private void TickPanicMode()
    {
        float hp          = GetHpPercent();
        float panicPct    = Settings.PanicThreshold.Value / 100f;
        float recoveryPct = panicPct + Settings.Advanced.PanicRecoveryBuffer.Value / 100f;

        if (!_panicTriggered)
        {
            if (hp < panicPct)
            {
                _panicTriggered = true;
                LogMessage($"[FlaskCore] PANIC at hp={hp:P1}", 5f);
                FileLog($"[PANIC] triggered at hp={hp:P1} threshold={panicPct:P0}");
                for (int i = 0; i < Settings.Advanced.PanicEscPressCount.Value; i++)
                    _panicEscQueue.Enqueue(DateTime.UtcNow.AddMilliseconds(i * Settings.Advanced.PanicEscDelayMs.Value));
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

    // ---- File logging / diagnostics ----

    private void FileLog(string msg)
    {
        if (Settings == null || !Settings.Advanced.FileLogging) return;
        _logBuffer.Add($"{DateTime.Now:HH:mm:ss.fff} {msg}");
    }

    private void FlushFileLogIfNeeded()
    {
        if (_logBuffer.Count == 0 || _logFilePath == null) return;
        var now = DateTime.UtcNow;
        if (_logBuffer.Count < 50 && (now - _lastLogFlushAt).TotalMilliseconds < 1000) return;
        _lastLogFlushAt = now;
        try { File.AppendAllLines(_logFilePath, _logBuffer); } catch { /* never break Tick over I/O */ }
        _logBuffer.Clear();
    }

    private void LogSkipThrottled(FlaskType type, string msg)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastSkipLogAt[(int)type]).TotalMilliseconds < 500) return;
        _lastSkipLogAt[(int)type] = now;
        FileLog(msg);
    }

    private static (int cur, int max, int perUse) GetCharges(ServerInventory.InventSlotItem slot)
    {
        var item = slot?.Item;
        if (item == null) return (-1, -1, -1);
        try
        {
            if (!item.TryGetComponent<Charges>(out var ch) || ch == null) return (-1, -1, -1);
            return (ch.NumCharges, ch.ChargesMax, ch.ChargesPerUse);
        }
        catch { return (-1, -1, -1); }
    }

    // Log point 4: re-check buff state ~700ms after each press.
    // Primary check: did the buff appear? Reliable even when charges refill from kills in <700ms.
    // Fallback (Seething / empty buffId): check charges. Still imperfect with fast kill charge-gen,
    // but Seething is instant so 700ms is usually enough to see charge drop before refill.
    private void VerifyPendingPresses()
    {
        if (_pendingPresses.Count == 0) return;
        var now = DateTime.UtcNow;
        for (int i = _pendingPresses.Count - 1; i >= 0; i--)
        {
            var p = _pendingPresses[i];
            if ((now - p.At).TotalMilliseconds < PressVerifyDelayMs) continue;
            _pendingPresses.RemoveAt(i);

            var slot = p.Type == FlaskType.Life ? _lifeFlaskSlot : _manaFlaskSlot;
            var (chgCur, chgMax, _) = GetCharges(slot);
            float after = p.Type == FlaskType.Life ? GetHpPercent() : GetManaPercent();

            string verdict;
            bool hasBuff = !string.IsNullOrEmpty(p.BuffId) && HasFlaskBuff(p.BuffId);
            if (!string.IsNullOrEmpty(p.BuffId))
            {
                // Buff-based check: buff present = definitely worked; absent = NO-OP
                verdict = hasBuff ? "ok (buff active)" : "NO-OP? buff never appeared — key lost or already on cd";
            }
            else
            {
                // Seething / no buff: fall back to charge drop (imperfect but best available)
                verdict = p.ChargesBefore >= 0 && chgCur >= 0 && chgCur >= p.ChargesBefore
                    ? "NO-OP? charges did not drop (seething, no buff to check)"
                    : "ok";
            }

            FileLog($"[{p.Type}] POST-PRESS +{(now - p.At).TotalMilliseconds:F0}ms " +
                    $"{(p.Type == FlaskType.Life ? "hp" : "mana")} {p.Before:P1}->{after:P1} " +
                    $"chg {p.ChargesBefore}->{chgCur}/{chgMax} buff={hasBuff} {verdict}");
        }
    }

    // Log point 3: compare MapState bridge HP against a direct Life read.
    // A stale bridge value during burst damage makes the flask react late.
    private void LogHpSourceDivergence()
    {
        if (_msHpPercent == null) return;
        var now = DateTime.UtcNow;
        if ((now - _lastHpSourceCheckAt).TotalMilliseconds < 1000) return;
        _lastHpSourceCheckAt = now;

        var player = GameController?.Player;
        if (player == null || !player.TryGetComponent<Life>(out var life) || life.MaxHP <= 0) return;
        float direct = (float)life.CurHP / life.MaxHP;
        float bridge = _msHpPercent.Invoke();
        if (Math.Abs(bridge - direct) > 0.02f)
            FileLog($"[HP-SOURCE] bridge={bridge:P1} direct={direct:P1} diff={Math.Abs(bridge - direct):P1} — bridge stale, flask reacts late");
    }

    // Overlay mirror: periodic snapshot so the log alone tells the whole story.
    private void LogStateHeartbeat()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastStateLogAt).TotalSeconds < 5) return;
        _lastStateLogAt = now;

        var (lifeChg, lifeChgMax, _) = GetCharges(_lifeFlaskSlot);
        var (manaChg, manaChgMax, _) = GetCharges(_manaFlaskSlot);
        FileLog($"[STATE] hp={GetHpPercent():P1} mana={GetManaPercent():P1} " +
                $"lifeChg={lifeChg}/{lifeChgMax} manaChg={manaChg}/{manaChgMax} " +
                $"life={_debugLifeStatus} mana={_debugManaStatus} pending={_pendingPresses.Count}");
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

        int idxOverride = Settings.Advanced.FlaskInventoryIndex.Value;
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
        var style = ParseStyle(Settings.LifeFlaskStyle.Value);
        if (style == FlaskStyle.Normal)
        {
            LogMessage($"[FlaskCore] Life (Normal): MaxHP={life.MaxHP}  trigger=below {Settings.LifeTriggerPct.Value}% ({(int)(life.MaxHP * Settings.LifeTriggerPct.Value / 100f)}HP)");
            return;
        }
        float healFrac = Settings.LifeFlaskHealHp.Value / (float)life.MaxHP;
        float instFrac = healFrac * Settings.Advanced.LifeFlaskInstantSplitPct.Value / 100f;
        int   instantHp = Settings.LifeFlaskHealHp.Value * Settings.Advanced.LifeFlaskInstantSplitPct.Value / 100;
        int   regenHp   = Settings.LifeFlaskHealHp.Value - instantHp;
        int   triggerHp = life.MaxHP - instantHp;
        LogMessage($"[FlaskCore] Life ({style}): MaxHP={life.MaxHP}  HealHP={Settings.LifeFlaskHealHp.Value}" +
                   $"  healFrac={healFrac:P1}  instFrac={instFrac:P1}" +
                   $"  triggerHP={triggerHp}  instant=+{instantHp}HP regen=+{regenHp}HP");
    }

    private void LogManaCalibration()
    {
        var life = GameController?.Player?.GetComponent<Life>();
        if (life == null) { LogMessage("[FlaskCore] Calibrate: no Life component"); return; }
        int maxMana = life.MaxMana > 0 ? life.MaxMana : 1;
        var style = ParseStyle(Settings.ManaFlaskStyle.Value);
        if (style == FlaskStyle.Normal)
        {
            LogMessage($"[FlaskCore] Mana (Normal): MaxMana={maxMana}  trigger=below {Settings.ManaTriggerPct.Value}% ({(int)(maxMana * Settings.ManaTriggerPct.Value / 100f)}mana)");
            return;
        }
        float healFrac    = Settings.ManaFlaskHealMana.Value / (float)maxMana;
        float instFrac    = healFrac * Settings.Advanced.ManaFlaskInstantSplitPct.Value / 100f;
        int   instantMana = Settings.ManaFlaskHealMana.Value * Settings.Advanced.ManaFlaskInstantSplitPct.Value / 100;
        int   regenMana   = Settings.ManaFlaskHealMana.Value - instantMana;
        int   triggerMana = maxMana - instantMana;
        LogMessage($"[FlaskCore] Mana ({style}): MaxMana={maxMana}  HealMana={Settings.ManaFlaskHealMana.Value}" +
                   $"  healFrac={healFrac:P1}  instFrac={instFrac:P1}" +
                   $"  triggerMana={triggerMana}  instant=+{instantMana}mana regen=+{regenMana}mana");
    }

    private bool LifeFlaskReady()
    {
        if (_lifeFlaskSlot == null) return false;
        if (ParseStyle(Settings.LifeFlaskStyle.Value) == FlaskStyle.Seething) return true;
        return !HasFlaskBuff(Settings.Advanced.LifeFlaskBuffId.Value);
    }

    private bool ManaFlaskReady()
    {
        if (_manaFlaskSlot == null) return false;
        if (ParseStyle(Settings.ManaFlaskStyle.Value) == FlaskStyle.Seething) return true;
        return !HasFlaskBuff(Settings.Advanced.ManaFlaskBuffId.Value);
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
