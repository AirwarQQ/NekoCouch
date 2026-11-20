// Background loop — core logic: reacts to trigger devices connecting and to Big Picture.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NekoCouch
{

    internal sealed class MonitorService
    {
        private readonly AppSettings _s;
        private Thread _thread;
        private CancellationTokenSource _cts;
        private volatile bool _running;

        // Guards Activate()/Deactivate() against overlapping with each other, whether triggered
        // by the automatic loop or a manual Activate/Deactivate button.
        private readonly object _actionLock = new object();

        // Set from the UI thread on WM_DEVICECHANGE so the loop reacts immediately instead of on its next 2 s tick.
        private readonly AutoResetEvent _devicesChanged = new AutoResetEvent(false);

        // Bluetooth controllers drop for a moment now and then (sleep, interference); don't tear the
        // whole TV session down over a blip. How long counts as a blip is a setting.
        private TimeSpan RestoreGrace => TimeSpan.FromSeconds(_s.RestoreGraceSec);

        private DisplayConfigSnapshot _preActivateSnapshot;
        private int _sessionDisplay;
        private Adb _sessionAdb;
        private bool _sourceAttempted;
        private volatile bool _displayEnabled;

        // What this session changed, so deactivation puts exactly that back.
        private bool _tvWasAsleep;          // NekoCouch woke it → may put it back to sleep
        private string _tvPreviousApp;      // package/.Activity the TV showed before the switch
        private string[] _audioPrevious;    // original playback endpoints for each Windows audio role
        private bool? _hdrPrevious;         // TV display's HDR state before NekoCouch turned it on

        private Adb NewAdb() => new Adb(_s.AdbPath, _s.TvIp);   // TvIp can change (found again after DHCP)

        private void MinimizeSteam() { if (_s.MinimizeSteamAfter) SteamHelper.MinimizeSteamWindow(); }

        // What an activation or deactivation is doing right now, for the Home page: a Loc key
        // ("Phase.Wake", …) plus its argument, or null when idle.
        public string PhaseKey { get; private set; }
        public object PhaseArg { get; private set; }
        public event Action PhaseChanged;

        private void Phase(string key, object arg = null)
        {
            PhaseKey = key;
            PhaseArg = arg;
            PhaseChanged?.Invoke();
        }

        public event Action<string> Log;
        public event Action<bool> RunningChanged;
        // Raised (on whatever thread changed it) whenever IsDisplayActive flips — automatic
        // activation, Big Picture closing, restore-on-disconnect, rollback or the manual buttons.
        public event Action<bool> DisplayActiveChanged;
        // Fired for failures worth interrupting the user for (tray balloon), separate from the
        // full step-by-step Log so routine activity doesn't spam a notification.
        public event Action<string, string> Notify;
        // The TV turned up under a different IP (after a DHCP change); settings are already saved.
        public event Action<string> TvAddressChanged;

        public bool IsRunning => _running;
        public bool IsDisplayActive => _displayEnabled;
        public bool RestorePending { get; private set; }

        public MonitorService(AppSettings s) { _s = s; }

        public void NotifyDevicesChanged() => _devicesChanged.Set();

        private void L(string msg)
        {
            Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg);
            Log?.Invoke(msg);
        }

        private void N(string title, string msg) => Notify?.Invoke(title, msg);

        private void SetDisplayEnabled(bool value)
        {
            if (_displayEnabled == value) return;
            _displayEnabled = value;
            DisplayActiveChanged?.Invoke(value);
        }

        public void Start()
        {
            if (_running) return;
            _cts = new CancellationTokenSource();
            _running = true;
            _thread = new Thread(() => RunLoop(_cts.Token)) { IsBackground = true, Name = "NekoCouch-Monitor" };
            _thread.Start();
            RunningChanged?.Invoke(true);
            L("Monitor started. IP=" + _s.TvIp + ", display #" + _s.TargetDisplay +
              (_s.DisableOthers ? ", other monitors will be disabled." : "."));
            if (!_s.WatchedDevices.Any(d => d.Enabled))
                L("WARNING: no enabled trigger devices — add one (and tick it) on the Devices tab.");
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _cts.Cancel(); } catch { }
            try { _thread?.Join(3000); } catch { }
            RunningChanged?.Invoke(false);
            L("Monitor stopped.");
        }

        // Manual override — runs the same activation sequence as an automatic gamepad-connect
        // trigger, on demand (e.g. the TV was never/no longer correctly detected as active).
        // No-op if already active, since re-running it while DisableOthers is on would overwrite
        // _preActivateSnapshot with the already-modified layout, corrupting the real restore point.
        public void ActivateNow()
        {
            Task.Run(() =>
            {
                lock (_actionLock)
                {
                    if (_displayEnabled)
                    {
                        L("Manual activate: already active — ignoring.");
                        return;
                    }
                    L("Manual activate requested.");
                    Activate(CancellationToken.None, _s.EnableBigPicture);
                }
            });
        }

        // Manual override — restores displays/TV source regardless of whether the app thinks
        // it's currently "active", so it also works as a recovery button if state ever desyncs.
        // Closes Big Picture first, same as restore-on-disconnect — also when it was started by hand
        // and the TV was never "active".
        public Task DeactivateNow()
        {
            return Task.Run(() =>
            {
                lock (_actionLock)
                {
                    L("Manual deactivate requested.");
                    ManualReturn();
                }
            });
        }

        private void ManualReturn()
        {
            CloseBigPictureBeforeDeactivate(CancellationToken.None);
            Deactivate(CancellationToken.None);
            MinimizeSteam();
        }

        // Directional commands are idempotent. Drop input during a switch instead of queuing a
        // return (or a second activation) behind an action requested by another controller.
        public void GamepadCommand(bool activate)
        {
            Task.Run(() =>
            {
                if (!Monitor.TryEnter(_actionLock)) return;
                try
                {
                    if (!_s.GamepadShortcutsEnabled || activate == _displayEnabled) return;
                    if (_s.GamepadShortcutSound) System.Media.SystemSounds.Asterisk.Play();
                    L("Gamepad shortcut: " + (activate ? "Play on TV." : "Back to PC."));
                    if (activate) Activate(CancellationToken.None, _s.EnableBigPicture);
                    else ManualReturn();
                }
                finally { Monitor.Exit(_actionLock); }
            });
        }

        // Big Picture has to go before the TV display is switched off — otherwise it reopens full-screen
        // on the main monitor. A failure is reported but doesn't stop the deactivation.
        private void CloseBigPictureBeforeDeactivate(CancellationToken ct)
        {
            if (!SteamHelper.IsBigPictureRunning()) return;
            string how;
            if (SteamHelper.CloseBigPicture(ct, out how))
                L("Big Picture closed (" + how + ").");
            else
            {
                L("Big Picture " + how + " — deactivating anyway.");
                N("NekoCouch", Loc.T("Note.BpNotClosed"));
            }
        }

        private bool Sleep(CancellationToken ct, int ms) => ct.WaitHandle.WaitOne(ms);

        // A failed activation must never disable a display that was already on. The manual
        // recovery button can still disable the configured TV when there is no known session.
        private bool RestoreDisplayState(bool manualRecovery)
        {
            string err;
            if (_hdrPrevious != null)
            {
                if (DisplayManager.SetHdr(_sessionDisplay, _hdrPrevious.Value, out err)) L("TV HDR restored.");
                else L("Restore HDR: " + err);
                _hdrPrevious = null;
            }
            bool restored = true;
            if (_preActivateSnapshot != null)
            {
                if (!DisplayManager.RestoreAll(_preActivateSnapshot, out err))
                {
                    L("Restore monitors FAILED: " + err);
                    N("NekoCouch", Loc.T("Note.RestoreFailed", err));
                    restored = false;   // keep the restore point for the next Back to PC
                }
                else
                {
                    _preActivateSnapshot = null;
                    L(err ?? "Monitor layout restored.");
                    if (err != null) N("NekoCouch", Loc.T("Note.Extend"));
                }
            }
            else if (manualRecovery && !DisplayManager.DisableDisplay(_s.TargetDisplay, out err))
                L("Disable display: " + err);
            // The old endpoint may belong to a monitor that was offline during the TV session.
            if (_audioPrevious != null)
            {
                if (AudioSwitcher.RestoreDefaults(_audioPrevious, out err)) L("Sound back on the previous device.");
                else L("Restore sound device: " + err);
                if (restored) _audioPrevious = null;
            }
            return restored;
        }

        private void WaitDuringActivation(CancellationToken ct, int ms)
        {
            if (Sleep(ct, ms)) ct.ThrowIfCancellationRequested();
        }

        private void Activate(CancellationToken ct, bool launchBigPicture)
        {
            Phase("Phase.Start");
            bool committed = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                L("Activating TV.");
                _sessionDisplay = _s.TargetDisplay;
                bool exclusive = _s.DisableOthers;
                _sessionAdb = NewAdb();
                var originalLayout = DisplayManager.QueryAll(out var err);
                if (originalLayout == null)
                    throw new InvalidOperationException(Loc.T("Note.LayoutUnknown", err));
                var originalAudio = _s.SwitchAudio ? AudioSwitcher.CaptureDefaults() : null;
                void ArmRollback()
                {
                    _preActivateSnapshot = originalLayout;
                    _audioPrevious = originalAudio;
                }
                if (_s.EnableWakeTV || _s.EnableSourceSwitch)
                {
                    if (!EnsureTvReachable(_sessionAdb, ct, ArmRollback))
                    {
                        ct.ThrowIfCancellationRequested();
                        throw new InvalidOperationException(Loc.T("Note.Unreachable", _s.TvIp));
                    }
                    _sessionAdb = NewAdb();   // discovery may have updated the address
                }

                if (_s.EnableWakeTV)
                {
                    Phase("Phase.Wake");
                    ArmRollback();
                    L(_sessionAdb.WakeTv(out _tvWasAsleep));
                    if (_tvWasAsleep) WaitDuringActivation(ct, 1500);
                }
                if (_s.EnableSourceSwitch && _s.RestoreTvApp && !_tvWasAsleep)
                {
                    _tvPreviousApp = _sessionAdb.GetForegroundComponent();
                    if (_tvPreviousApp != null && (_tvPreviousApp.StartsWith("com.xiaomi.mitv.tvplayer/", StringComparison.Ordinal) ||
                                                   _tvPreviousApp.StartsWith("com.liskovsoft.tvexternalsource", StringComparison.Ordinal)))
                        _tvPreviousApp = null;
                    if (_tvPreviousApp != null) L("TV was showing " + _tvPreviousApp + ".");
                }

                Phase("Phase.Display");
                ct.ThrowIfCancellationRequested();
                var target = DisplayManager.ResolvePhysicalId(_sessionDisplay, out err);
                if (target == null)
                    throw new InvalidOperationException(Loc.T("Note.DisplayFailed", _sessionDisplay, err));
                ArmRollback();
                if (!DisplayManager.EnableDisplay(_sessionDisplay, out err))
                    throw new InvalidOperationException(Loc.T("Note.DisplayFailed", _sessionDisplay, err));
                if (exclusive)
                {
                    WaitDuringActivation(ct, 1500);
                    if (!DisplayManager.DisableAllExceptPhysical(target.Value, out err, L))
                        throw new InvalidOperationException(Loc.T("Note.DisplayFailed", _sessionDisplay, err));
                }

                WaitDuringActivation(ct, 500);
                var activeNumber = DisplayManager.ActiveNumber(target.Value, out err);
                if (activeNumber == null)
                    throw new InvalidOperationException(Loc.T("Note.DisplayFailed", _sessionDisplay, err));
                _sessionDisplay = activeNumber.Value;
                if (!DisplayManager.SetHighestRefreshRate(_sessionDisplay, out err, L)) L("Set refresh rate: " + err);
                if (_s.EnableSourceSwitch) SwitchSource(_sessionAdb, ct);
                ApplyHdrAndAudio(ct);
                ct.ThrowIfCancellationRequested();
                if (launchBigPicture)
                {
                    Phase("Phase.BigPicture");
                    L("Starting Big Picture.");
                    if (!SteamHelper.StartBigPicture(_s.SteamPath, out err))
                        throw new InvalidOperationException(Loc.T("Note.BpFailed", err));
                }
                committed = true;
                SetDisplayEnabled(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { L("Activation cancelled."); }
            catch (Exception ex)
            {
                L("Activation failed: " + ex.Message);
                N("NekoCouch", ex.Message);
            }
            finally
            {
                try
                {
                    if (!committed)
                    {
                        Phase("Phase.Back");
                        RestoreSession(CancellationToken.None, manualRecovery: false);
                    }
                }
                finally { Phase(null); }
            }
        }

        private void SwitchSource(Adb adb, CancellationToken ct)
        {
            int port = _s.HdmiPort;
            Phase("Phase.Hdmi", port);
            _sourceAttempted = true;
            string pattern, method;
            try { pattern = adb.SwitchToHdmi(port, out method, ct); }
            catch (InvalidOperationException ex) { throw new InvalidOperationException(Loc.T("Note.SwitchFailed", ex.Message), ex); }
            ct.ThrowIfCancellationRequested();
            L("Switching to HDMI " + port + " via " + method + ".");
            if (!string.IsNullOrWhiteSpace(_s.HdmiActivityPattern)) pattern = _s.HdmiActivityPattern;
            if (pattern == null) WaitDuringActivation(ct, 2000);   // legacy vendor activity has no known name
            else
            {
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (!adb.IsHdmiSourceActive(pattern))
                {
                    if (wait.ElapsedMilliseconds >= 10000) throw new InvalidOperationException(Loc.T("Note.SwitchTimeout"));
                    WaitDuringActivation(ct, 200);
                }
            }
            WaitDuringActivation(ct, 1000);
            L("HDMI source active.");
        }

        // ADB answers → true. If not: wake the TV over the network (needs its MAC, learned the first
        // time it was reachable) and wait for it, then look for it under a new address in case DHCP
        // moved it.
        private bool EnsureTvReachable(Adb adb, CancellationToken ct, Action beforeNetworkWake)
        {
            if (adb.IsAdbReachable(3000)) { LearnTvMac(); return true; }
            if (string.IsNullOrEmpty(_s.TvMac)) return false;

            L("TV not answering — sending Wake-on-LAN to " + _s.TvMac + ".");
            beforeNetworkWake();
            TvDiscovery.SendWakeOnLan(_s.TvMac);
            for (int i = 0; i < 8; i++)
            {
                if (Sleep(ct, 2500)) return false;
                if (adb.IsAdbReachable(2500)) { L("TV woke up."); return true; }
            }

            string ip = TvDiscovery.FindByMacAsync(_s.TvMac).GetAwaiter().GetResult();
            if (ip == null || ip == _s.TvIp) return false;
            L("TV found at a new address: " + ip + " (was " + _s.TvIp + ").");
            _s.TvIp = ip;
            _s.Save();
            TvAddressChanged?.Invoke(ip);
            return NewAdb().IsAdbReachable(3000);
        }

        private void LearnTvMac()
        {
            string mac = TvDiscovery.MacOf(_s.TvIp);
            if (mac == null || mac == _s.TvMac) return;
            _s.TvMac = mac;
            _s.Save();
        }

        // TV-side extras once the picture is on the TV: HDR on, sound to the TV. Both are undone in
        // RestoreDisplayState().
        private void ApplyHdrAndAudio(CancellationToken ct)
        {
            string err;
            if (_s.EnableHdr)
            {
                bool? hdr = DisplayManager.GetHdr(_sessionDisplay);
                if (hdr == null) L("HDR: the TV display doesn't report HDR support.");
                else if (hdr == true) L("HDR already on.");
                else
                {
                    if (DisplayManager.SetHdr(_sessionDisplay, true, out err)) { _hdrPrevious = false; L("HDR on."); }
                    else L("HDR: " + err);
                }
            }

            if (_s.SwitchAudio)
            {
                Phase("Phase.Sound");
                string hint = string.IsNullOrWhiteSpace(_s.TvAudioDevice) ? DisplayManager.GetFriendlyName(_sessionDisplay) : _s.TvAudioDevice;
                var tv = AudioSwitcher.WaitForDevice(hint, 8000, ct);
                string current = AudioSwitcher.GetDefaultId();
                if (tv == null) L("Sound: no playback device matching \"" + hint + "\" — left as is.");
                else if (_audioPrevious == null) L("Sound: cannot save the original playback device.");
                else if (tv.Id == current) L("Sound already on " + tv.Name + ".");
                else if (AudioSwitcher.SetDefault(tv.Id, out err)) L("Sound → " + tv.Name + ".");
                else L("Sound: " + err);
            }
        }

        // Full deactivation sequence: restore displays, send TV back to its home screen,
        // disconnect adb. Used both by the automatic Big-Picture-closed trigger and
        // DeactivateNow() (which also minimizes Steam itself — the automatic path does that
        // separately since it wants it right after this, not as part of the shared sequence).
        private void Deactivate(CancellationToken ct)
        {
            Phase("Phase.Back");
            try
            {
                L("Deactivating TV.");
                RestoreSession(ct, manualRecovery: _sessionAdb == null);
            }
            finally { Phase(null); }
        }

        private void RestoreSession(CancellationToken ct, bool manualRecovery)
        {
            RestorePending = !RestoreDisplayState(manualRecovery);
            SetDisplayEnabled(RestorePending);
            if (RestorePending) return;  // keep the TV usable until Windows accepts the PC layout
            if (manualRecovery && System.Net.IPAddress.TryParse(_s.TvIp, out _))
            {
                _sessionAdb = NewAdb();
                _sourceAttempted = _s.EnableSourceSwitch;
            }
            if (_sessionAdb == null) return;
            if (_sourceAttempted)
                L("TV: " + _sessionAdb.ReturnTv(_tvPreviousApp, _s.TvHomeComponent, _s.SleepTvAfter && _tvWasAsleep, ct) + ".");
            else
            {
                if (_s.SleepTvAfter && _tvWasAsleep) _sessionAdb.SleepTv();
                _sessionAdb.Disconnect();
            }
            _sourceAttempted = false;
            _tvWasAsleep = false;
            _tvPreviousApp = null;
            _sessionAdb = null;
        }

        private void RunLoop(CancellationToken ct)
        {
            // Empty at start, so an enabled device that's already plugged in when monitoring starts
            // triggers right away — same as the old single-gamepad behaviour.
            var prevConnected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Resume Big Picture after an app restart, and preserve a manual TV-only session when
            // the user turns monitoring on halfway through it.
            bool prevBp;
            lock (_actionLock)
            {
                prevBp = SteamHelper.IsBigPictureRunning();
                if (prevBp)
                {
                    SetDisplayEnabled(true);
                    L("Big Picture already running — resuming as active.");
                }
            }

            // "Restore on disconnect" devices seen connected during the current active session, and
            // when the last of them went away (null while at least one is still around).
            var armed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DateTime? goneSince = null;

            // Enumerating every devnode is the expensive part of a tick. Windows announces plug-ins
            // (WM_DEVICECHANGE wakes this loop), so scan right after one — and for a few seconds
            // more, since Bluetooth flips "connected" a moment later — and otherwise only every 15 s
            // as a safety net. The cheap Big Picture check still runs every 2 s.
            var curConnected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DateTime lastScan = DateTime.MinValue, scanBurstUntil = DateTime.UtcNow.AddSeconds(6);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var watched = _s.WatchedDevices;   // copy-on-write list: read the reference once per tick
                    if (DateTime.UtcNow < scanBurstUntil || DateTime.UtcNow - lastScan > TimeSpan.FromSeconds(15)
                        || (_displayEnabled && goneSince != null))   // a restore countdown needs fresh state
                    {
                        curConnected = new HashSet<string>(
                            DeviceScanner.Scan().Where(d => d.IsConnected).Select(d => d.Key),
                            StringComparer.OrdinalIgnoreCase);
                        lastScan = DateTime.UtcNow;
                    }
                    bool curBp = SteamHelper.IsBigPictureRunning();

                    // Per-device edge, not "any watched device connected": otherwise an always-plugged
                    // keyboard on the list would keep that flag true forever and nothing could trigger.
                    var trigger = watched.FirstOrDefault(d =>
                        d.Enabled && curConnected.Contains(d.Key) && !prevConnected.Contains(d.Key));

                    // --- ACTIVATION: enabled trigger device just connected, TV not already active ---
                    // _displayEnabled matters now that "TV only" triggers don't start Big Picture: a second
                    // device connecting mid-session must not re-run Activate, which would re-snapshot the
                    // already-modified monitor layout and lose the real one to restore.
                    if (trigger != null && !curBp && !_displayEnabled)
                    {
                        bool tvOnly = trigger.Action == TriggerAction.TvOnly;
                        lock (_actionLock)
                        {
                            // Re-checked under the lock: a manual Activate (button / Home Assistant) may
                            // have just run, and a second pass would snapshot the already-changed layout.
                            if (!_displayEnabled)
                            {
                                L(trigger.DisplayName + " connected -> activating TV" + (tvOnly ? " (TV only)." : "."));
                                Activate(ct, launchBigPicture: !tvOnly && _s.EnableBigPicture);
                            }
                        }
                    }

                    // --- RESTORE ON DISCONNECT: every flagged device from this session gone for a while ---
                    if (_displayEnabled)
                    {
                        foreach (var d in watched)
                            if (d.Enabled && d.RestoreOnDisconnect && curConnected.Contains(d.Key))
                                armed.Add(d.Key);

                        if (armed.Count > 0 && !armed.Any(curConnected.Contains))
                        {
                            if (goneSince == null)
                            {
                                goneSince = DateTime.UtcNow;
                                L(NamesOf(watched, armed) + " disconnected — restoring in " + RestoreGrace.TotalSeconds + " s unless it comes back.");
                            }
                            else if (DateTime.UtcNow - goneSince.Value >= RestoreGrace)
                            {
                                lock (_actionLock)
                                {
                                    // Re-checked under the lock: a manual Deactivate may have finished meanwhile.
                                    if (_displayEnabled)
                                    {
                                        L(NamesOf(watched, armed) + " still gone -> deactivating TV.");
                                        CloseBigPictureBeforeDeactivate(ct);
                                        Deactivate(ct);
                                        MinimizeSteam();
                                    }
                                }
                                curBp = SteamHelper.IsBigPictureRunning();
                                armed.Clear();
                                goneSince = null;
                            }
                        }
                        else if (goneSince != null)
                        {
                            L("Device back — restore cancelled.");
                            goneSince = null;
                        }
                    }
                    else
                    {
                        armed.Clear();
                        goneSince = null;
                    }

                    // --- DEACTIVATION: Big Picture was running, now closed ---
                    // The check above runs outside the lock, so it also fires right after DeactivateNow()
                    // closed Big Picture itself while still restoring — re-check under the lock, or the
                    // restore would run twice (and the second pass, with no snapshot left, switches the
                    // TV display off even if it was on before activation).
                    if (_displayEnabled && prevBp && !curBp)
                    {
                        lock (_actionLock)
                        {
                            if (_displayEnabled)
                            {
                                L("Big Picture closed -> deactivating TV.");
                                Deactivate(ct);
                                MinimizeSteam();
                            }
                        }
                    }

                    prevConnected = curConnected;
                    prevBp = curBp;
                }
                catch (Exception ex)
                {
                    L("Loop error: " + ex.Message);
                }

                // Poll every 2 s, or right away when Windows reports a device change.
                int woke = WaitHandle.WaitAny(new[] { ct.WaitHandle, _devicesChanged }, 2000);
                if (woke == 0) return;
                // A plug-in is a burst of devnode changes (and Bluetooth flips IsConnected a moment
                // later) — give the rest of it time to land so one scan sees the finished state.
                if (woke == 1)
                {
                    scanBurstUntil = DateTime.UtcNow.AddSeconds(6);
                    if (Sleep(ct, 400)) return;
                }
            }
        }

        private static string NamesOf(List<WatchedDevice> watched, HashSet<string> keys) =>
            string.Join(", ", watched.Where(d => keys.Contains(d.Key)).Select(d => d.DisplayName));
    }

}
