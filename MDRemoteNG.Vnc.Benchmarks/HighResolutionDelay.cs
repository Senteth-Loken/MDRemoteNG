using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MDRemoteNG.Vnc.Benchmarks;

/// <summary>Private per-wait timers; never changes Windows' global timer resolution.</summary>
internal static class HighResolutionDelay
{
    private const uint HighResolution = 0x00000002, TimerAccess = 0x00100002;
    private static int _usedNative, _usedFallback;
    public static string Mechanism => Volatile.Read(ref _usedFallback) != 0
        ? "Private Windows high-resolution one-shot waitable timer with thread-pool registration where supported; Task.Delay fallback was used. No global timer-resolution change or busy spin."
        : Volatile.Read(ref _usedNative) != 0
            ? "Private Windows high-resolution one-shot waitable timer with thread-pool registration. No global timer-resolution change or busy spin; no fallback used."
            : "Wait mechanism not used yet; private Windows high-resolution one-shot timer preferred, Task.Delay fallback where unsupported.";

    public static async Task Until(long deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (deadline > Stopwatch.GetTimestamp())
        {
            SafeWaitHandle? timer = CreateTimer();
            if (timer is null)
            {
                Volatile.Write(ref _usedFallback, 1);
                double remainingMs = (deadline - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency;
                if (remainingMs > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(remainingMs))), cancellationToken).ConfigureAwait(false);
                continue;
            }
            Volatile.Write(ref _usedNative, 1);
            using var wait = new TimerWaitHandle(timer);
            long remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0) return;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RegisteredWaitHandle registered = ThreadPool.RegisterWaitForSingleObject(wait,
                static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), completion, Timeout.Infinite, true);
            using CancellationTokenRegistration cancellation = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            try
            {
                // Negative due times are relative, in 100 ns units. Round upward so a
                // short fractional duration cannot deliberately fire before its deadline.
                long due = -Math.Max(1, (long)Math.Ceiling(remaining * 10_000_000d / Stopwatch.Frequency));
                if (!SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot arm the benchmark's private timer.");
                await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                // The registered callback only completes the TCS; it never touches
                // native handles, so an already-running callback may safely finish.
                registered.Unregister(null);
                _ = CancelWaitableTimer(timer);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static SafeWaitHandle? CreateTimer()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            SafeWaitHandle timer = CreateWaitableTimerExW(IntPtr.Zero, null, HighResolution, TimerAccess);
            if (!timer.IsInvalid) return timer;
            int error = Marshal.GetLastPInvokeError();
            timer.Dispose();
            if (error is 50 or 87 or 120) return null; // Older Windows without high-resolution timers.
            throw new Win32Exception(error, "Cannot create the benchmark's private timer.");
        }
        catch (EntryPointNotFoundException) { return null; }
    }

    private sealed class TimerWaitHandle : WaitHandle
    {
        public TimerWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period,
        IntPtr completionRoutine, IntPtr completionArgument, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
}
