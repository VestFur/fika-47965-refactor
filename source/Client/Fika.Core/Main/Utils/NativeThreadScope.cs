using System;
using Il2CppInterop.Runtime;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Native serialization may run in a CLR HTTP continuation. Register that
/// thread with IL2CPP for the synchronous native operation and its GC roots.
/// This does not permit Unity scene APIs on worker threads or span awaits.
/// </summary>
internal readonly struct NativeThreadScope : IDisposable
{
    private readonly IntPtr _attached;

    public NativeThreadScope()
    {
        _attached = IntPtr.Zero;
        if (IL2CPP.il2cpp_thread_current() != IntPtr.Zero) return;
        _attached = IL2CPP.il2cpp_thread_attach(IL2CPP.il2cpp_domain_get());
        if (_attached == IntPtr.Zero)
            throw new InvalidOperationException("Could not attach the managed worker thread to IL2CPP.");
    }

    public void Dispose()
    {
        if (_attached != IntPtr.Zero) IL2CPP.il2cpp_thread_detach(_attached);
    }
}
