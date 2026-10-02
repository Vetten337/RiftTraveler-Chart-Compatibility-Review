using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

namespace RiftTraveler;

// Experimental engine boundary module, not yet a stable external API. The
// binding has no FD codes, heights, source cache, extra illuminator or timers.
internal sealed class DimensionLightCompatibility : IDisposable
{
    private const string HarmonyId = "rifttraveler.dimension-light-native";
    private static readonly object Gate = new();
    private static DimensionLightCompatibility[] bindings = Array.Empty<DimensionLightCompatibility>();
    private static Harmony harmony;
    private static readonly FieldInfo LoadedMapChunks = AccessTools.Field(typeof(ServerMain), "loadedMapChunks");
    [ThreadStatic] private static IBlockAccessor scopedAccessor;
    [ThreadStatic] private static DimensionLightCompatibility scopedBinding;
    [ThreadStatic] private static RemovalScope removal;
    private readonly WorldMap map;
    private readonly int dimension;
    private readonly DimensionLightingConfig settings;
    private int correctedLookups;
    private int correctedPackets;
    private int queuedUpdates;
    private bool disposed;
    internal int CorrectedLookups => Volatile.Read(ref correctedLookups);
    internal int CorrectedPackets => Volatile.Read(ref correctedPackets);
    internal int QueuedUpdates => Volatile.Read(ref queuedUpdates);

    internal DimensionLightCompatibility(WorldMap map, int dimension, DimensionLightingConfig settings)
    {
        if(map == null) throw new ArgumentNullException(nameof(map));
        if(dimension <= 0 || dimension >= 1024) throw new ArgumentOutOfRangeException(nameof(dimension));
        this.map = map;
        this.dimension = dimension;
        // Keep a snapshot: changing settings requires a restart/rebind.
        this.settings = new DimensionLightingConfig {
            Enabled = true, CorrectInventoryLookups = settings.CorrectInventoryLookups,
            CorrectRemovalPackets = settings.CorrectRemovalPackets,
            CorrectMissingMapDispatch = settings.CorrectMissingMapDispatch
        };
        lock(Gate)
        {
            if(bindings.Any(b => ReferenceEquals(b.map, map) && b.dimension == dimension))
                throw new InvalidOperationException("A lighting binding already owns this world/dimension.");
            if(harmony == null) Install();
            Volatile.Write(ref bindings, bindings.Append(this).ToArray());
        }
    }

    private static void Install()
    {
        // Validate version-sensitive signatures before installing anything.
        MethodInfo update = AccessTools.DeclaredMethod(typeof(ChunkIlluminator), "UpdateLightAt",
            new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(Vintagestory.API.Datastructures.FastSetOfLongs) });
        MethodInfo lookup = typeof(BlockAccessorBase).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Single(m => m.Name == "GetBlockEntity" && !m.IsGenericMethod && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType == typeof(BlockPos));
        MethodInfo broadcast = AccessTools.DeclaredMethod(typeof(ServerMain), "BroadcastPacket",
            new[] { typeof(Packet_Server), typeof(IServerPlayer[]) });
        MethodInfo remove = AccessTools.DeclaredMethod(typeof(ServerWorldMap), "RemoveBlockLight", new[] { typeof(byte[]), typeof(BlockPos) });
        MethodInfo dispatch = AccessTools.DeclaredMethod(typeof(ServerWorldMap), "UpdateLighting", new[] { typeof(int), typeof(int), typeof(BlockPos) });
        if(update == null || broadcast == null || remove == null || dispatch == null
            || AccessTools.Field(typeof(ChunkIlluminator), "chunkProvider") == null
            || AccessTools.Field(typeof(ChunkIlluminator), "readBlockAccess") == null || LoadedMapChunks == null)
            throw new MissingMethodException("Unsupported engine lighting signatures.");
        var candidate = new Harmony(HarmonyId);
        try
        {
            // Callee first: prevent the rebuilt removal method from inlining it.
            candidate.Patch(broadcast, prefix: Hook(nameof(PrepareRemovalPacket)));
            candidate.Patch(remove, prefix: Hook(nameof(EnterRemoval)), finalizer: Hook(nameof(ExitRemoval)));
            candidate.Patch(dispatch, prefix: Hook(nameof(DispatchMissingMapChunk)));
            candidate.Patch(lookup, prefix: Hook(nameof(NormalizeLookup)));
            candidate.Patch(update, prefix: Hook(nameof(EnterLookup)), finalizer: Hook(nameof(ExitLookup)));
            harmony = candidate;
        }
        catch { candidate.UnpatchAll(HarmonyId); throw; }
    }

    private static HarmonyMethod Hook(string name) => new(typeof(DimensionLightCompatibility), name);
    private static DimensionLightCompatibility Find(object map, int dimension)
    {
        foreach(var binding in Volatile.Read(ref bindings))
            if(ReferenceEquals(binding.map, map) && binding.dimension == dimension) return binding;
        return null;
    }

    private static void EnterLookup(int __2, object ___chunkProvider, IBlockAccessor ___readBlockAccess,
        out LookupScope __state)
    {
        __state = new LookupScope(scopedAccessor, scopedBinding);
        scopedBinding = Find(___chunkProvider, __2 / BlockPos.DimensionBoundary);
        scopedAccessor = scopedBinding?.settings.CorrectInventoryLookups == true ? ___readBlockAccess : null;
    }
    private static void ExitLookup(LookupScope __state)
    {
        scopedAccessor = __state.Accessor;
        scopedBinding = __state.Binding;
    }
    private static void NormalizeLookup(object __instance, ref BlockPos __0)
    {
        var binding = scopedBinding;
        if(binding == null || !ReferenceEquals(__instance, scopedAccessor) || __0 == null
            || __0.dimension != 0 || __0.Y / BlockPos.DimensionBoundary != binding.dimension) return;
        __0 = new BlockPos(__0.X, __0.Y, __0.Z);
        Interlocked.Increment(ref binding.correctedLookups);
    }
    private readonly record struct LookupScope(IBlockAccessor Accessor, DimensionLightCompatibility Binding);
    private sealed record RemovalScope(DimensionLightCompatibility Binding, BlockPos Position);

    private static void EnterRemoval(object __instance, BlockPos __1, out RemovalScope __state)
    {
        __state = removal;
        var binding = __1 == null ? null : Find(__instance, __1.dimension);
        removal = binding?.settings.CorrectRemovalPackets == true ? new RemovalScope(binding, __1.Copy()) : null;
    }
    private static void ExitRemoval(RemovalScope __state) => removal = __state;
    private static void PrepareRemovalPacket(ServerMain __instance, Packet_Server __0)
    {
        var scope = removal;
        if(scope == null || !ReferenceEquals(scope.Binding.map.World, __instance) || __0?.Id != 72) return;
        var payload = __0.RemoveBlockLight;
        var pos = scope.Position;
        if(payload == null || payload.PosX != pos.X || payload.PosZ != pos.Z || payload.PosY != pos.Y) return;
        payload.PosY = pos.InternalY;
        Interlocked.Increment(ref scope.Binding.correctedPackets);
    }
    private static bool DispatchMissingMapChunk(ServerWorldMap __instance, int __0, int __1, BlockPos __2)
    {
        var binding = __2 == null ? null : Find(__instance, __2.dimension);
        if(binding?.settings.CorrectMissingMapDispatch != true) return true;
        if(__instance.World is not ServerMain server) return true;
        // The engine's existing map-chunk path stays authoritative if present.
        long key = __instance.MapChunkIndex2D(__2.X / 32, __2.Z / 32);
        if(((IDictionary)LoadedMapChunks.GetValue(server)).Contains(key) || __instance.GetChunk(__2) == null) return true;
        lock(__instance.LightingTasksLock)
            __instance.LightingTasks.Enqueue(new UpdateLightingTask { oldBlockId = __0, newBlockId = __1, pos = __2.Copy() });
        Interlocked.Increment(ref binding.queuedUpdates);
        return false;
    }

    public void Dispose()
    {
        lock(Gate)
        {
            if(disposed) return;
            disposed = true;
            Volatile.Write(ref bindings, bindings.Where(b => !ReferenceEquals(b, this)).ToArray());
            if(bindings.Length == 0)
            {
                harmony?.UnpatchAll(HarmonyId);
                harmony = null;
            }
        }
    }
}
