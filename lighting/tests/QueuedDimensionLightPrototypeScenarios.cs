using System.Reflection;
using System.Threading;
using Atlas.XUnit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace RiftTraveler.AtlasTests;

public sealed class QueuedDimensionLightPrototypeScenarios : AtlasScenarioBase
{
    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task ScopedLookupCorrection_ShouldRestoreLightOnVanillaWorkerQueue()
    {
        // Test-only prototype; never staged as runtime mod code.
        World.Api.ModLoader.GetModSystem("RiftTraveler.FracturedDeepBlockLightAdapter").Dispose();
        var player = await World.JoinPlayer("QueuedLight");
        BlockPos overworld = player.Position.Copy().Add(0, 8, 0);
        Assert.True((await player.ExecuteCommand("/rt debug fractureddeep")).Ok);
        await World.Until(() => player.Position.dimension > 0, timeoutTicks: 1200);
        BlockPos pos = new(1075, 90, 1075, player.Position.dimension);
        await World.Until(() => World.Api.WorldManager.GetChunk(pos) != null, timeoutTicks: 1200);
        object server = World.Api.World;
        Assembly engine = server.GetType().Assembly;
        object map = server.GetType().GetField("WorldMap")!.GetValue(server)!;
        Type accessorType = engine.GetType("Vintagestory.Common.BlockAccessorRelaxed", true)!;
        IBlockAccessor fixture = (IBlockAccessor)Activator.CreateInstance(accessorType,
            new object[] { map, World.Api.World, false, false })!;
        var blocks = World.Api.World.BlockAccessor;
        int storage = World.Api.World.GetBlock(new AssetLocation("game:groundstorage"))!.Id;
        int granite = World.Api.World.GetBlock(new AssetLocation("game:rock-granite"))!.Id;
        Block small = World.Api.World.GetBlock(new AssetLocation("game:lantern-small-up"))!;
        Block torch = World.Api.World.GetBlock(new AssetLocation("game:torch-basic-lit-up"))!;
        BlockPos above = pos.Copy().Add(0, 1, 0);
        int Light(BlockPos at) => blocks.GetLightLevel(at, EnumLightLevelType.OnlyBlockLight);

        IInventory Install(BlockPos at)
        {
            for(int dx = -1; dx <= 1; dx++)
            for(int dz = -1; dz <= 1; dz++)
            for(int dy = 0; dy <= 2; dy++) fixture.SetBlock(0, at.Copy().Add(dx, dy, dz));
            fixture.SetBlock(granite, at.Copy().Add(0, -1, 0));
            fixture.SetBlock(storage, at);
            var be = blocks.GetBlockEntity(at);
            Assert.NotNull(be);
            var inventory = (IInventory)be.GetType().GetProperty("Inventory")!.GetValue(be)!;
            inventory[0]!.Itemstack = new ItemStack(small);
            inventory[0]!.Itemstack!.Attributes.SetString("material", "bismuth");
            inventory[0]!.Itemstack!.Attributes.SetString("lining", "plain");
            inventory[0]!.Itemstack!.Attributes.SetString("glass", "plain");
            return inventory;
        }

        // Submit tasks to the REAL engine queue. No private illuminator, direct
        // propagation calls, relights, timers or full-chunk resend repair.
        object queue = map.GetType().GetField("LightingTasks")!.GetValue(map)!;
        Type taskType = queue.GetType().GetGenericArguments().Single();
        object queueLock = map.GetType().GetField("LightingTasksLock")!.GetValue(map)!;
        MethodInfo enqueue = queue.GetType().GetMethod("Enqueue")!;
        void Submit(BlockPos at, int newId, byte[]? oldLight = null)
        {
            object task = Activator.CreateInstance(taskType)!;
            taskType.GetField("pos")!.SetValue(task, at.Copy());
            taskType.GetField("oldBlockId")!.SetValue(task, 0);
            taskType.GetField("newBlockId")!.SetValue(task, newId);
            if(oldLight != null) taskType.GetField("removeLightHsv")!.SetValue(task, oldLight.Clone());
            lock(queueLock) enqueue.Invoke(queue, new[] { task });
        }

        using var patch = new NativeLightTestBinding(map, pos.dimension);
        var inventory = Install(pos);
        BlockPos raw = new BlockPos(0).Set(pos.X, pos.InternalY, pos.Z);
        Assert.Null(fixture.GetBlockEntity(raw)); // Not a global accessor correction.
        // Exercise the actual ground-storage LightUpdate -> ExchangeBlock ->
        // engine queue path before relying on any explicitly submitted tasks.
        int beforeNative = patch.CorrectedLookups;
        blocks.GetBlockEntity(pos).GetType().GetMethod("LightUpdate")!
            .Invoke(blocks.GetBlockEntity(pos), new object[] { inventory[0]!.Itemstack! });
        await World.Until(() => Light(above) > 0, timeoutTicks: 120);
        Assert.True(patch.CorrectedLookups > beforeNative);
        // The separate baseline proved worker identity; this test now runs the
        // shipped runtime module on that same native queue path.
        Assert.Null(fixture.GetBlockEntity(raw)); // Worker scope was restored.
        await World.Ticks(10);
        int plainLight = Light(above);

        byte[] old = blocks.GetBlock(pos).GetLightHsv(blocks, pos);
        Assert.Equal(18, old[2]);
        inventory[0]!.Itemstack!.Attributes.SetString("lining", "silver");
        blocks.GetBlockEntity(pos).GetType().GetMethod("LightUpdate")!
            .Invoke(blocks.GetBlockEntity(pos), new object[] { inventory[0]!.Itemstack! });
        await World.Until(() => Light(above) > plainLight, timeoutTicks: 120);
        Assert.Equal(20, blocks.GetBlock(pos).GetLightHsv(blocks, pos)[2]);

        // Overlap remains when the dynamic source is removed.
        BlockPos torchPos = pos.Copy().Add(4, 0, 0);
        fixture.SetBlock(granite, torchPos.Copy().Add(0, -1, 0));
        fixture.SetBlock(0, torchPos.Copy().Add(0, 1, 0));
        fixture.SetBlock(torch.Id, torchPos);
        Submit(torchPos, torch.Id);
        await World.Until(() => Light(torchPos.Copy().Add(0, 1, 0)) > 0, timeoutTicks: 120);
        old = blocks.GetBlock(pos).GetLightHsv(blocks, pos);
        blocks.GetBlockEntity(pos).OnBlockBroken(player.Player);
        fixture.SetBlock(0, pos);
        await World.Until(() => Light(above) < plainLight, timeoutTicks: 120);
        Assert.True(Light(above) > 0, "Removing the lantern must preserve overlapping torch light.");
        fixture.SetBlock(0, torchPos);
        Submit(torchPos, 0, torch.GetLightHsv(blocks, null, new ItemStack(torch)));
        await World.Until(() => Light(above) == 0, timeoutTicks: 120);

        // Opted-in scope must leave an ordinary overworld lantern unchanged.
        await World.Until(() => World.Api.WorldManager.GetChunk(overworld) != null, timeoutTicks: 1200);
        Install(overworld);
        int beforeCorrections = patch.CorrectedLookups;
        Submit(overworld, storage);
        await World.Until(() => Light(overworld.Copy().Add(0, 1, 0)) > 0, timeoutTicks: 120);
        Assert.Equal(beforeCorrections, patch.CorrectedLookups);
        old = blocks.GetBlock(overworld).GetLightHsv(blocks, overworld);
        fixture.SetBlock(0, overworld);
        Submit(overworld, 0, old);
        await World.Until(() => Light(overworld.Copy().Add(0, 1, 0)) == 0, timeoutTicks: 120);
    }
}

/// <summary>Only in the test assembly; scope is thread-local and bound to this world/accessor.</summary>
internal sealed class ScopedLightLookupPrototype : IDisposable
{
    private const string Id = "rifttraveler.atlas.dimension-light-lookup-prototype";
    private readonly Harmony harmony = new(Id);
    private static ScopedLightLookupPrototype? active;
    private readonly object map;
    private readonly int dimension;
    private int corrections;
    private int workerThread;
    [ThreadStatic] private static IBlockAccessor? scopedAccessor;
    public int CorrectedLookups => Volatile.Read(ref corrections);
    public int WorkerThread => Volatile.Read(ref workerThread);

    public ScopedLightLookupPrototype(Assembly engine, object map, int dimension)
    {
        this.map = map;
        this.dimension = dimension;
        Assert.True(dimension > 0);
        active = this;
        try
        {
            Type light = engine.GetType("Vintagestory.Common.ChunkIlluminator", true)!;
            Type accessor = engine.GetType("Vintagestory.Common.BlockAccessorBase", true)!;
            harmony.Patch(AccessTools.Method(light, "UpdateLightAt"),
                prefix: new HarmonyMethod(typeof(ScopedLightLookupPrototype), nameof(Enter)),
                finalizer: new HarmonyMethod(typeof(ScopedLightLookupPrototype), nameof(Exit)));
            MethodInfo lookup = accessor.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Single(m => m.Name == "GetBlockEntity" && !m.IsGenericMethod && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(BlockPos));
            harmony.Patch(lookup,
                prefix: new HarmonyMethod(typeof(ScopedLightLookupPrototype), nameof(NormalizeLookup)));
        }
        catch { Dispose(); throw; }
    }

    private static void Enter(int __2, object ___chunkProvider, IBlockAccessor ___readBlockAccess,
        out IBlockAccessor? __state)
    {
        __state = scopedAccessor;
        var owner = active;
        scopedAccessor = owner != null && ReferenceEquals(owner.map, ___chunkProvider)
            && __2 / BlockPos.DimensionBoundary == owner.dimension ? ___readBlockAccess : null;
    }

    private static void Exit(IBlockAccessor? __state) => scopedAccessor = __state;

    private static void NormalizeLookup(object __instance, ref BlockPos __0)
    {
        var owner = active;
        if(owner == null || !ReferenceEquals(__instance, scopedAccessor) || __0 == null
            || __0.dimension != 0 || __0.Y / BlockPos.DimensionBoundary != owner.dimension) return;
        __0 = new BlockPos(__0.X, __0.Y, __0.Z);
        Interlocked.Increment(ref owner.corrections);
        Volatile.Write(ref owner.workerThread, Environment.CurrentManagedThreadId);
    }

    public void Dispose()
    {
        harmony.UnpatchAll(Id);
        if(ReferenceEquals(active, this)) active = null;
    }
}
