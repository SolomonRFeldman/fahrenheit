// SPDX-License-Identifier: LGPL-3.0-or-later
//
// This file is part of Fahrenheit, © 2023-2026 The Fahrenheit contributors.
// It is licensed to you under the GNU Lesser General Public License, version 3.0 or later. See COPYING, COPYING.LESSER.

using System.Diagnostics;

namespace Fahrenheit;

/// <summary>
///     Bot: Temporary A/B profiler for FhCallRegistry's chain lock. Backed by an AutoResetEvent when
///     chain_lock_mode.txt next to fh.dll reads "event", otherwise by a Lock. Logs once per second to core.log.
/// </summary>
internal sealed class FhChainLockProfiler {
    private readonly bool           _use_event;
    private readonly AutoResetEvent _event = new AutoResetEvent(true);
    private readonly Lock           _lock  = new();

    private long _acquires;
    private long _ticks;
    private long _window_start = Stopwatch.GetTimestamp();

    public FhChainLockProfiler() {
        string mode_path = Path.Combine(Path.GetDirectoryName(typeof(FhChainLockProfiler).Assembly.Location)!, "chain_lock_mode.txt");
        _use_event = File.Exists(mode_path) && File.ReadAllText(mode_path).Trim() == "event";
    }

    private string mode_name => _use_event ? "event" : "lock";

    public void enter() {
        long start = Stopwatch.GetTimestamp();

        if (_use_event) _event.WaitOne();
        else            _lock.Enter();

        Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - start);
        Interlocked.Increment(ref _acquires);
    }

    public void exit() {
        long start = Stopwatch.GetTimestamp();

        if (_use_event) _event.Set();
        else            _lock.Exit();

        long end = Stopwatch.GetTimestamp();
        Interlocked.Add(ref _ticks, end - start);

        report_if_due(end);
    }

    private void report_if_due(long now) {
        long window_start = Volatile.Read(ref _window_start);
        if (now - window_start < Stopwatch.Frequency) return;
        if (Interlocked.CompareExchange(ref _window_start, now, window_start) != window_start) return;

        long   acquires = Interlocked.Exchange(ref _acquires, 0);
        double total_ms = Interlocked.Exchange(ref _ticks, 0) * 1000.0 / Stopwatch.Frequency;
        double seconds  = (now - window_start) / (double)Stopwatch.Frequency;

        FhInternal.Log.Info(
            $"[chain-lock] mode={mode_name} acquires/s={acquires / seconds:F0} total_ms/s={total_ms / seconds:F3} " +
            $"ns/acquire+release={(acquires == 0 ? 0 : total_ms * 1_000_000 / acquires):F0}");
    }
}
