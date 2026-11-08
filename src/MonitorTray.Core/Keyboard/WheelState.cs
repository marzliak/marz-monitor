// Portado do GPU by MarzOps (CmpInf, homolog 260a683f). Lógica mantida; só namespace/log/visibilidade mudaram.
// CmpInf local r3.3-wheel. Pure logic: no Windows input/audio/hardware calls.
// Licensed under the same GPL terms as the surrounding CmpInf adaptation.
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MonitorTray.Core.Keyboard
{
    public sealed class View
    {
        public int Page, Interval, Singles, Doubles, Revision;
        public bool Pinned, Pending;
    }
    public sealed class WheelState
    {
        private readonly object gate = new object();
        private int[] durations = new int[] { 5000 };
        private int page, singles, doubles, revision;
        private bool pinned, held;
        private long pending = -1, lastPress = -10000, switched, lastTick;
        public void Configure(int[] pageDurations, long now)
        {
            if (pageDurations == null || pageDurations.Length == 0) throw new ArgumentException("No pages");
            lock (gate)
            {
                durations = (int[])pageDurations.Clone();
                for (int i = 0; i < durations.Length; i++) durations[i] = Math.Max(1000, durations[i]);
                page = 0; pinned = held = false; pending = -1; switched = lastTick = now;
                singles = doubles = 0; revision++;
            }
        }
        // Returns true only on an accepted physical press, not on key-repeat/bounce.
        public bool Press(long now)
        {
            lock (gate)
            {
                if (held) return false;
                held = true;
                if (now - lastPress < 45) return false;
                lastPress = now;
                if (pending >= 0 && now - pending <= 350)
                {
                    pending = -1; pinned = !pinned; doubles++; switched = now; revision++;
                }
                else
                {
                    CommitSingle(now);
                    pending = now;
                }
                return true;
            }
        }
        public void Release() { lock (gate) held = false; }
        private void CommitSingle(long now)
        {
            if (pending >= 0 && now - pending >= 350)
            {
                pending = -1; page = (page + 1) % durations.Length;
                singles++; switched = now; revision++;
            }
        }
        public View Snapshot(long now)
        {
            lock (gate)
            {
                if (now - lastTick > 10000) { pending = -1; held = false; switched = now; }
                lastTick = now;
                CommitSingle(now);
                if (!pinned && pending < 0 && !held && now - switched >= durations[page])
                { page = (page + 1) % durations.Length; switched = now; revision++; }
                return new View { Page = page, Pinned = pinned, Pending = pending >= 0,
                    Interval = pinned ? 500 : 1000, Revision = revision, Singles = singles, Doubles = doubles };
            }
        }
        public void CancelInput(long now, bool returnToAuto)
        {
            lock (gate)
            {
                held = false; pending = -1; switched = now; lastPress = -10000;
                if (returnToAuto) pinned = false;
                revision++;
            }
        }
        public void Next(long now)
        { lock (gate) { pending = -1; page = (page + 1) % durations.Length; switched = now; revision++; } }
        public void TogglePin(long now)
        { lock (gate) { pending = -1; pinned = !pinned; switched = now; revision++; } }
    }
    public static class DeviceFilter
    {
        // The user associated this Consumer Control endpoint with the Apex wheel.
        // This is VID/PID/interface/collection matching, not a serial-number identity.
        public static bool Matches(string path)
        {
            string p = (path ?? "").ToUpperInvariant();
            return Has(p, "VID_1038") && Has(p, "PID_1640") && Has(p, "MI_03") && Has(p, "COL01");
        }
        private static bool Has(string p, string token)
        { return Regex.IsMatch(p, "(?:^|[&#\\\\])" + token + "(?:[&#\\\\]|$)", RegexOptions.CultureInvariant); }
    }
    public sealed class MuteFix
    {
        public bool Expected, Desired;
        public MuteFix(bool expected, bool desired) { Expected = expected; Desired = desired; }
    }
    public enum AudioCallResult { None, FirstFailure, Suppressed, Recovered }
    public sealed class AudioCallGate
    {
        private sealed class State { public bool Failed; public long Last; }
        private readonly Func<long> clock; private readonly long interval;
        private readonly Dictionary<string, State> states = new Dictionary<string, State>();
        public AudioCallGate(Func<long> clock, long interval) { this.clock = clock; this.interval = interval; }
        public AudioCallResult Failure(string key, Exception error)
        {
            if (!states.TryGetValue(key, out var state)) { state = new State(); states[key] = state; }
            long now = clock();
            if (!state.Failed) { state.Failed = true; state.Last = now; return AudioCallResult.FirstFailure; }
            if (now - state.Last >= interval) { state.Last = now; return AudioCallResult.Suppressed; }
            return AudioCallResult.Suppressed;
        }
        public AudioCallResult Success(string key)
        {
            if (!states.TryGetValue(key, out var state) || !state.Failed) return AudioCallResult.None;
            state.Failed = false; return AudioCallResult.Recovered;
        }
    }
    // Correlation is deliberately bounded. It is NOT HID suppression or exclusive attribution.
    // At most one external mute transition is compensated per observed Apex press.
    public sealed class MuteCorrelation
    {
        private bool known, ready, before, after, waitingDesired;
        private long transitionAt = -10000, waitingAt = -10000, blockedUntil;
        public void Reset(bool current)
        { known = current; ready = true; transitionAt = waitingAt = -10000; blockedUntil = 0; }
        public void Cancel(long now)
        { transitionAt = waitingAt = -10000; blockedUntil = now + 250; }
        public MuteFix? Click(long now)
        {
            if (!ready || now < blockedUntil) return null;
            if (transitionAt >= 0 && now - transitionAt <= 150)
            {
                MuteFix fix = new MuteFix(after, before);
                transitionAt = waitingAt = -10000;
                return fix;
            }
            waitingAt = now; waitingDesired = known; transitionAt = -10000;
            return null;
        }
        public MuteFix? Notification(long now, bool muted, bool own)
        {
            if (!ready) { Reset(muted); return null; }
            bool old = known; known = muted;
            if (own) { transitionAt = -10000; return null; }
            if (muted == old) return null;
            if (now < blockedUntil) { transitionAt = waitingAt = -10000; return null; }
            if (waitingAt >= 0 && now >= waitingAt && now - waitingAt <= 200)
            {
                bool desired = waitingDesired;
                waitingAt = transitionAt = -10000;
                return muted == desired ? null : new MuteFix(muted, desired);
            }
            waitingAt = -10000; transitionAt = now; before = old; after = muted;
            return null;
        }
        public void Corrected(bool muted) { known = muted; transitionAt = -10000; }
    }
}
