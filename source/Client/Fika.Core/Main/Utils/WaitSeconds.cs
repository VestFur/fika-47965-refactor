using System;

namespace Fika.Core.Main.Utils;

/// <summary>
/// A plain managed replacement for a Unity <c>WaitForSeconds</c>.
///
/// Why it exists: the routine pump drives managed iterators, so a port routine that yields the engine
/// object makes the pump read <c>WaitForSeconds.m_Seconds</c> through the interop wrapper. That read is
/// what ended the 2026-10-10 host raids: the diagnostics wrote "read WaitForSeconds.m_Seconds" and the
/// next thing coreclr did was raise its uncatchable 0x80131506 behind JIT_ReversePInvokeEnterRare.
/// Keeping the duration in managed state removes the engine object from the path entirely.
/// </summary>
internal sealed class WaitSeconds
{
    internal readonly float Seconds;

    internal WaitSeconds(float seconds)
    {
        Seconds = seconds;
    }
}
