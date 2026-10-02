using System.Reflection;
using HarmonyLib;

namespace RiftTraveler.AtlasTests;

// Exercise the shipped runtime module, not a copied test implementation.
internal sealed class NativeLightTestBinding : IDisposable
{
    private readonly object binding;
    public int CorrectedLookups => Count("CorrectedLookups");
    public int CorrectedPackets => Count("CorrectedPackets");
    public int Enqueued => Count("QueuedUpdates");
    private int Count(string property) => (int)binding.GetType().GetProperty(property,
        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(binding)!;

    public NativeLightTestBinding(object map, int dimension, bool dispatch = true, bool packets = true, bool lookups = true)
    {
        Assembly runtime = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "RiftTraveler");
        object config = Activator.CreateInstance(runtime.GetType("RiftTraveler.DimensionLightingConfig", true)!)!;
        config.GetType().GetProperty("CorrectInventoryLookups")!.SetValue(config, lookups);
        config.GetType().GetProperty("CorrectMissingMapDispatch")!.SetValue(config, dispatch);
        config.GetType().GetProperty("CorrectRemovalPackets")!.SetValue(config, packets);
        binding = Activator.CreateInstance(runtime.GetType("RiftTraveler.DimensionLightCompatibility", true)!,
            BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { map, dimension, config }, null)!;
    }

    public void Dispose() => ((IDisposable)binding).Dispose();
}
