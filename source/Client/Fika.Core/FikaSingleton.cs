namespace Fika.Core;

/// <summary>
/// Fika-owned services live in the managed domain. Comfort.Singleton's native
/// generic cannot be closed over managed interfaces or injected Fika classes.
/// Game-owned services continue to use Comfort.Common.Singleton.
/// </summary>
public static class FikaSingleton<T> where T : class
{
    public static T Instance { get; set; }
    public static bool Instantiated => Instance != null;

    public static void Create(T instance) => Instance = instance;

    // Preserve the original Comfort singleton lifecycle contract.
    public static void Release(T instance) => Instance = null;

    public static bool TryRelease(T instance)
    {
        if (instance == null || !ReferenceEquals(Instance, instance)) return false;
        Instance = null;
        return true;
    }
}
