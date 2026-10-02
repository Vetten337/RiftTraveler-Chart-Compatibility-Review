using System.Collections;
using System.Reflection;
using Atlas.XUnit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace RiftTraveler.AtlasTests;

public sealed class DimensionLightLifecycleScenarios : AtlasScenarioBase
{
    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task SecondManifoldDimension_ShouldRetainInventoryLightAcrossNativeUnloadAndDiskReload()
    {
        World.Api.ModLoader.GetModSystem("RiftTraveler.FracturedDeepBlockLightAdapter").Dispose();
        object deep = World.Api.ModLoader.GetModSystem("RiftTraveler.FracturedDeepSystem");
        // Borrow its owner-scoped facade, not FD's worldgen or numeric identity.
        // Registration and all disk writes exist only in this disposable Atlas save.
        object manifold = AccessTools.Field(deep.GetType(), "manifold").GetValue(deep)!;
        object registry = manifold.GetType().GetProperty("Registry")!.GetValue(manifold)!;
        Assembly manifoldAssembly = registry.GetType().Assembly;
        object strategy = Activator.CreateInstance(manifoldAssembly.GetType("Manifold.Api.Helpers.BasicVoidWorldgenStrategy", true)!)!;
        var code = new AssetLocation("rifttraveler", "atlas_light_lab");
        object builder = registry.GetType().GetMethod("Define")!.Invoke(registry, new object[] { code })!;
        builder.GetType().GetMethod("Persistent")!.Invoke(builder, null);
        builder.GetType().GetMethod("WithWorldgen")!.Invoke(builder, new[] { strategy });
        builder.GetType().GetMethod("WithGenerationRadius")!.Invoke(builder, new object[] { 1 });
        object dimension = builder.GetType().GetMethod("Create")!.Invoke(builder, null)!;
        int dim = (int)dimension.GetType().GetProperty("InternalId")!.GetValue(dimension)!;
        int fd = (int)AccessTools.Field(deep.GetType(), "dimensionId").GetValue(deep)!;
        Assert.True(dim > 0);
        Assert.NotEqual(fd, dim);
        var pos = new BlockPos(1055, 90, 1055, dim); // X/Z chunk corner.
        manifold.GetType().GetMethod("GenerateRegion")!.Invoke(manifold, new object[] { code, pos });
        await World.Until(() => World.Api.WorldManager.GetChunk(pos) != null, timeoutTicks: 1200);

        object server = World.Api.World;
        Assembly engine = server.GetType().Assembly;
        object map = AccessTools.Field(server.GetType(), "WorldMap").GetValue(server)!;
        IBlockAccessor fixture = (IBlockAccessor)Activator.CreateInstance(
            engine.GetType("Vintagestory.Common.BlockAccessorRelaxed", true)!, map, server, false, false)!;
        var blocks = World.Api.World.BlockAccessor;
        Block storage = World.Api.World.GetBlock(new AssetLocation("game:groundstorage"))!;
        Block lantern = World.Api.World.GetBlock(new AssetLocation("game:lantern-small-up"))!;
        Block granite = World.Api.World.GetBlock(new AssetLocation("game:rock-granite"))!;
        fixture.SetBlock(granite.Id, pos.Copy().Add(0, -1, 0));
        fixture.SetBlock(storage.Id, pos);
        BlockEntity original = blocks.GetBlockEntity(pos);
        IInventory Inventory(BlockEntity be) => (IInventory)be.GetType().GetProperty("Inventory")!.GetValue(be)!;
        var stack = new ItemStack(lantern);
        stack.Attributes.SetString("material", "bismuth");
        stack.Attributes.SetString("lining", "silver");
        stack.Attributes.SetString("glass", "plain");
        stack.Attributes.SetString("atlasLifecycleToken", "same-lantern");
        Inventory(original)[0]!.Itemstack = stack;
        void Update(BlockEntity be) => be.GetType().GetMethod("LightUpdate")!
            .Invoke(be, new object[] { Inventory(be)[0]!.Itemstack! });
        int Light(BlockPos at) => blocks.GetLightLevel(at, EnumLightLevelType.OnlyBlockLight);
        BlockPos east = pos.Copy().Add(1, 1, 0), south = pos.Copy().Add(0, 1, 1);

        // A void dimension has no overworld map chunk at this location. The
        // vanilla dispatcher silently skips its otherwise valid lighting update.
        IDictionary mapChunks = (IDictionary)AccessTools.Field(server.GetType(), "loadedMapChunks").GetValue(server)!;
        long mapKey = (long)map.GetType().GetMethod("MapChunkIndex2D", new[] { typeof(int), typeof(int) })!
            .Invoke(map, new object[] { pos.X / 32, pos.Z / 32 })!;
        Assert.False(mapChunks.Contains(mapKey));
        using(var lookupOnly = new NativeLightTestBinding(map, dim, dispatch: false, packets: false))
        {
            Update(original);
            await World.Ticks(10);
            Assert.Equal(0, Light(east));
            Assert.Equal(0, lookupOnly.CorrectedLookups);
        }
        using(var patch = new NativeLightTestBinding(map, dim))
        {
            Update(original);
            await World.Until(() => Light(east) > 0 && Light(south) > 0, timeoutTicks: 120);
            Assert.True(patch.CorrectedLookups > 0);
            Assert.True(patch.Enqueued > 0);
            int dispatched = patch.Enqueued;
            MethodInfo updateLighting = map.GetType().GetMethod("UpdateLighting")!;
            var missing = new BlockPos(4096, 90, 4096, dim);
            Assert.Null(World.Api.WorldManager.GetChunk(missing));
            updateLighting.Invoke(map, new object[] { 0, storage.Id, missing });
            updateLighting.Invoke(map, new object[] { 0, storage.Id, new BlockPos(pos.X, pos.Y, pos.Z) });
            Assert.Equal(dispatched, patch.Enqueued);
            Assert.Null(World.Api.WorldManager.GetChunk(missing));
            Assert.False(mapChunks.Contains(mapKey)); // No overworld map-chunk loading.
            Assert.Equal(20, storage.GetLightHsv(blocks, pos)[2]);
            await World.Ticks(10);
            IWorldChunk before = World.Api.WorldManager.GetChunk(pos);
            await SaveAndUnloadColumn(engine, server, map, pos);
            Assert.Null(World.Api.WorldManager.GetChunk(pos));
            Assert.Null(blocks.GetBlockEntity(pos));
            // Manifold's generated-column record loads from disk, not worldgen.
            manifold.GetType().GetMethod("GenerateRegion")!.Invoke(manifold, new object[] { code, pos });
            await World.Until(() => blocks.GetBlockEntity(pos) != null, timeoutTicks: 1200);
            IWorldChunk after = World.Api.WorldManager.GetChunk(pos);
            Assert.NotSame(before, after);
            BlockEntity restored = blocks.GetBlockEntity(pos);
            Assert.NotSame(original, restored);
            var saved = Inventory(restored)[0]!.Itemstack!;
            Assert.Equal(lantern.Code, saved.Block.Code);
            Assert.Equal(1, saved.StackSize);
            Assert.Equal("same-lantern", saved.Attributes.GetString("atlasLifecycleToken"));
            Assert.Equal("silver", saved.Attributes.GetString("lining"));
            Assert.Equal(pos, restored.Pos);
            Assert.Equal(20, storage.GetLightHsv(blocks, pos)[2]);
            Assert.True(Light(east) > 0 && Light(south) > 0, "Saved light must survive loading without a repair scan.");
            Update(restored);
            await World.Until(() => Light(east) > 0 && Light(south) > 0, timeoutTicks: 120);
            restored.OnBlockBroken(null);
            fixture.SetBlock(0, pos);
            await World.Until(() => Light(east) == 0 && Light(south) == 0, timeoutTicks: 120);
        }
        // Fresh registration of the removable prototype must not retain old scope.
        using(var reinstalled = new NativeLightTestBinding(map, dim))
        {
            Assert.Equal(0, reinstalled.CorrectedLookups);
            Assert.Null(fixture.GetBlockEntity(new BlockPos(0).Set(pos.X, pos.InternalY, pos.Z)));
            fixture.SetBlock(storage.Id, pos);
            var replacement = blocks.GetBlockEntity(pos);
            Inventory(replacement)[0]!.Itemstack = stack.Clone();
            Update(replacement);
            await World.Until(() => Light(east) > 0 && Light(south) > 0, timeoutTicks: 120);
            Assert.True(reinstalled.CorrectedLookups > 0);
            replacement.OnBlockBroken(null);
            fixture.SetBlock(0, pos);
            await World.Until(() => Light(east) == 0 && Light(south) == 0, timeoutTicks: 120);
        }
        MethodInfo lookup = engine.GetType("Vintagestory.Common.BlockAccessorBase", true)!
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(m => m.Name == "GetBlockEntity" && !m.IsGenericMethod && m.GetParameters().Length == 1);
        Assert.DoesNotContain("rifttraveler.dimension-light-native",
            Harmony.GetPatchInfo(lookup)?.Owners.ToArray() ?? Array.Empty<string>());
        Assert.DoesNotContain("rifttraveler.dimension-light-native",
            Harmony.GetPatchInfo(map.GetType().GetMethod("UpdateLighting"))?.Owners.ToArray() ?? Array.Empty<string>());
    }

    private async Task SaveAndUnloadColumn(Assembly engine, object server, object map, BlockPos pos)
    {
        object queue = AccessTools.Field(map.GetType(), "LightingTasks").GetValue(map)!;
        object queueLock = AccessTools.Field(map.GetType(), "LightingTasksLock").GetValue(map)!;
        await World.Until(() => { lock(queueLock) return (int)queue.GetType().GetProperty("Count")!.GetValue(queue)! == 0; }, timeoutTicks: 120);
        await World.Ticks(10); // Let an already-dequeued worker task finish.
        Type dbChunk = engine.GetType("Vintagestory.Common.Database.DbChunk", true)!;
        Type coordType = engine.GetType("Vintagestory.Common.Database.ChunkPos", true)!;
        IList snapshots = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(dbChunk))!;
        var loaded = new List<(long Key, object Coord, object Chunk)>();
        for(int y = 0; y < World.Api.WorldManager.MapSizeY / 32; y++)
        {
            BlockPos origin = new(pos.X / 32 * 32, y * 32, pos.Z / 32 * 32, pos.dimension);
            object? chunk = World.Api.WorldManager.GetChunk(origin);
            if(chunk == null) continue;
            object coord = Activator.CreateInstance(coordType, pos.X / 32, y + pos.dimension * 1024, pos.Z / 32)!;
            byte[] bytes = (byte[])chunk.GetType().GetMethod("ToBytes", Type.EmptyTypes)!.Invoke(chunk, null)!;
            snapshots.Add(Activator.CreateInstance(dbChunk, coord, bytes)!);
            long key = (long)map.GetType().GetMethod("ChunkIndex3D", new[] { typeof(int), typeof(int), typeof(int) })!
                .Invoke(map, new object[] { pos.X / 32, y + pos.dimension * 1024, pos.Z / 32 })!;
            loaded.Add((key, coord, chunk));
        }
        object thread = AccessTools.Field(server.GetType(), "chunkThread").GetValue(server)!;
        object database = AccessTools.Field(thread.GetType(), "gameDatabase").GetValue(thread)!;
        database.GetType().GetMethod("SetChunks")!.Invoke(database, new object[] { snapshots });
        Type unloader = engine.GetType("Vintagestory.Server.ServerSystemUnloadChunks", true)!;
        MethodInfo unload = unloader.GetMethod("TryUnloadChunk")!;
        object dirty = Activator.CreateInstance(unload.GetParameters()[3].ParameterType)!;
        foreach(var item in loaded)
        {
            // Already persisted above; native unload may dispose safely without
            // putting a second copy on the asynchronous dirty-save queue.
            AccessTools.Field(item.Chunk.GetType(), "DirtyForSaving").SetValue(item.Chunk, false);
            unload.Invoke(null, new[] { (object)item.Key, item.Coord, item.Chunk, dirty, server });
        }
        Assert.Equal(0, (int)dirty.GetType().GetProperty("Count")!.GetValue(dirty)!);
    }
}

/// <summary>Test-only fallback for an absent overworld map chunk. Loaded, opted-in chunks only.</summary>
internal sealed class ScopedLightDispatchPrototype : IDisposable
{
    private const string Id = "rifttraveler.atlas.dimension-light-dispatch-prototype";
    private readonly Harmony harmony = new(Id);
    private static ScopedLightDispatchPrototype? active;
    private readonly object map;
    private readonly object server;
    private readonly int dimension;
    public int Enqueued;

    public ScopedLightDispatchPrototype(object map, object server, int dimension)
    {
        this.map = map;
        this.server = server;
        this.dimension = dimension;
        active = this;
        try
        {
            harmony.Patch(map.GetType().GetMethod("UpdateLighting"),
                prefix: new HarmonyMethod(typeof(ScopedLightDispatchPrototype), nameof(SubmitMissingMapChunk)));
        }
        catch { Dispose(); throw; }
    }

    private static bool SubmitMissingMapChunk(object __instance, int __0, int __1, BlockPos __2)
    {
        var owner = active;
        if(owner == null || !ReferenceEquals(owner.map, __instance) || __2.dimension != owner.dimension) return true;
        long key = (long)__instance.GetType().GetMethod("MapChunkIndex2D", new[] { typeof(int), typeof(int) })!
            .Invoke(__instance, new object[] { __2.X / 32, __2.Z / 32 })!;
        IDictionary mapChunks = (IDictionary)AccessTools.Field(owner.server.GetType(), "loadedMapChunks").GetValue(owner.server)!;
        if(mapChunks.Contains(key)) return true; // Ordinary dispatch remains authoritative.
        if(__instance.GetType().GetMethod("GetChunk", new[] { typeof(BlockPos) })!.Invoke(__instance, new object[] { __2 }) == null)
            return true; // Never load or generate missing chunks.
        object queue = AccessTools.Field(__instance.GetType(), "LightingTasks").GetValue(__instance)!;
        Type taskType = queue.GetType().GetGenericArguments().Single();
        object task = Activator.CreateInstance(taskType)!;
        taskType.GetField("pos")!.SetValue(task, __2.Copy());
        taskType.GetField("oldBlockId")!.SetValue(task, __0);
        taskType.GetField("newBlockId")!.SetValue(task, __1);
        object queueLock = AccessTools.Field(__instance.GetType(), "LightingTasksLock").GetValue(__instance)!;
        lock(queueLock) queue.GetType().GetMethod("Enqueue")!.Invoke(queue, new[] { task });
        owner.Enqueued++;
        return false;
    }

    public void Dispose()
    {
        harmony.UnpatchAll(Id);
        if(ReferenceEquals(active, this)) active = null;
    }
}
