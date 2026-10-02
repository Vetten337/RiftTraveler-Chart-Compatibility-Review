using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Atlas.XUnit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace RiftTraveler.AtlasTests;

public sealed class ClientDimensionLightScenarios : AtlasScenarioBase
{
    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task NativeClientQueue_ShouldPropagateAndRemoveInventoryLight()
    {
        World.Api.ModLoader.GetModSystem("RiftTraveler.FracturedDeepBlockLightAdapter").Dispose();
        var player = await World.JoinPlayer("ClientLight");
        Assert.True((await player.ExecuteCommand("/rt debug fractureddeep")).Ok);
        await World.Until(() => player.Position.dimension > 0, timeoutTicks: 1200);
        var pos = new BlockPos(1075, 90, 1075, player.Position.dimension);
        await World.Until(() => World.Api.WorldManager.GetChunk(pos) != null, timeoutTicks: 1200);
        Assembly engine = World.Api.World.GetType().Assembly;
        Type Type(string name) => engine.GetType(name, true)!;
        void Set(object target, string field, object value) => AccessTools.Field(target.GetType(), field).SetValue(target, value);
        object Get(object target, string field) => AccessTools.Field(target.GetType(), field).GetValue(target)!;

        object client = RuntimeHelpers.GetUninitializedObject(Type("Vintagestory.Client.NoObf.ClientMain"));
        object map = RuntimeHelpers.GetUninitializedObject(Type("Vintagestory.Client.NoObf.ClientWorldMap"));
        Set(client, "WorldMap", map);
        Set(map, "game", client);
        Set(map, "ClientChunkSize", 32);
        Set(map, "ServerChunkSize", 32);
        Set(map, "chunksLock", new object());
        AccessTools.Method(map.GetType(), "OnMapSizeReceived").Invoke(map,
            new object[] { new Vec3i(1024000, 256, 1024000), new Vec3i(2000, 1, 2000) });
        // Reuse loaded block definitions, but never server chunks/light buffers.
        object server = World.Api.World;
        Set(client, "Blocks", Get(server, "Blocks"));
        foreach(string name in new[] { "dirtyChunks", "dirtyChunksPriority" })
        {
            FieldInfo field = AccessTools.Field(client.GetType(), name);
            field.SetValue(client, Activator.CreateInstance(field.FieldType));
            Set(client, name + "Lock", new object());
        }
        FieldInfo queueField = AccessTools.Field(map.GetType(), "LightingTasks");
        queueField.SetValue(map, Activator.CreateInstance(queueField.FieldType));
        Set(map, "LightingTasksLock", new object());
        object pool = Activator.CreateInstance(Type("Vintagestory.Client.NoObf.ClientChunkDataPool"), 32, client)!;
        IDictionary chunks = (IDictionary)Get(map, "chunks");
        Type chunkType = Type("Vintagestory.Client.NoObf.ClientChunk");
        // Neighbor chunks cover propagation at the nearby vertical chunk edge.
        foreach(int dim in new[] { 0, pos.dimension })
        for(int dx = -1; dx <= 1; dx++)
        for(int dy = -1; dy <= 1; dy++)
        for(int dz = -1; dz <= 1; dz++)
        {
            int cx = pos.X / 32 + dx, cy = pos.Y / 32 + dim * 1024 + dy, cz = pos.Z / 32 + dz;
            object chunk = chunkType.GetMethod("CreateNew")!.Invoke(null, new[] { pool })!;
            ((IWorldChunk)chunk).LightPositions = new HashSet<int>();
            long key = (long)map.GetType().GetMethod("ChunkIndex3D", new[] { typeof(int), typeof(int), typeof(int) })!
                .Invoke(map, new object[] { cx, cy, cz })!;
            chunks.Add(key, chunk);
        }
        IBlockAccessor read = (IBlockAccessor)Activator.CreateInstance(Type("Vintagestory.Common.BlockAccessorRelaxed"),
            new object[] { map, client, false, false })!;
        Set(map, "RelaxedBlockAccess", read);
        object relight = Activator.CreateInstance(Type("Vintagestory.Client.NoObf.ClientSystemRelight"), client)!;
        object illuminator = Get(relight, "chunkIlluminator");
        AccessTools.Method(illuminator.GetType(), "InitForWorld").Invoke(illuminator,
            new object[] { Get(client, "Blocks"), (ushort)0, 1024000, 256, 1024000 });

        Block storage = World.Api.World.GetBlock(new AssetLocation("game:groundstorage"))!;
        Block small = World.Api.World.GetBlock(new AssetLocation("game:lantern-small-up"))!;
        // Inventory fixture uses a real, initialized BE; only emission reads occur
        // on this shared fixture. Interactions/client BE initialization are not tested.
        object serverMap = Get(server, "WorldMap");
        IBlockAccessor fixture = (IBlockAccessor)Activator.CreateInstance(Type("Vintagestory.Common.BlockAccessorRelaxed"),
            new object[] { serverMap, server, false, false })!;
        fixture.SetBlock(storage.Id, pos);
        BlockEntity be = fixture.GetBlockEntity(pos);
        IInventory inventory = (IInventory)be.GetType().GetProperty("Inventory")!.GetValue(be)!;
        inventory[0]!.Itemstack = new ItemStack(small);
        inventory[0]!.Itemstack!.Attributes.SetString("material", "bismuth");
        inventory[0]!.Itemstack!.Attributes.SetString("lining", "plain");
        inventory[0]!.Itemstack!.Attributes.SetString("glass", "plain");
        var clientChunk = (IWorldChunk)map.GetType().GetMethod("GetChunk", new[] { typeof(BlockPos) })!.Invoke(map, new object[] { pos })!;
        clientChunk.Data[(pos.Y % 32 * 32 + pos.Z % 32) * 32 + pos.X % 32] = storage.Id;
        AccessTools.Method(chunkType, "AddBlockEntity").Invoke(clientChunk, new object[] { be });
        BlockPos above = pos.Copy().Add(0, 1, 0);
        int Light(BlockPos at) => read.GetLightLevel(at, EnumLightLevelType.OnlyBlockLight);
        void Drain() => AccessTools.Method(relight.GetType(), "ProcessLightingQueue").Invoke(relight, null);
        void SetClientBlock(BlockPos at, int id)
        {
            var chunk = (IWorldChunk)map.GetType().GetMethod("GetChunk", new[] { typeof(BlockPos) })!.Invoke(map, new object[] { at })!;
            chunk.Data[(at.Y % 32 * 32 + at.Z % 32) * 32 + at.X % 32] = id;
        }
        void Add(BlockPos? at = null, int? id = null) => map.GetType().GetMethod("UpdateLighting")!
            .Invoke(map, new object[] { 0, id ?? storage.Id, (at ?? pos).Copy() });
        void Remove(byte[] hsv, BlockPos? at = null)
        {
            at ??= pos;
            SetClientBlock(at, 0);
            // Deliver a dimension-encoded removal through the real client
            // handler into its real task queue, then drain normally.
            object payload = Activator.CreateInstance(Type("Packet_RemoveBlockLight"))!;
            Set(payload, "PosX", at.X);
            Set(payload, "PosY", at.InternalY);
            Set(payload, "PosZ", at.Z);
            Set(payload, "LightH", (int)hsv[0]);
            Set(payload, "LightS", (int)hsv[1]);
            Set(payload, "LightV", (int)hsv[2]);
            object packet = Activator.CreateInstance(Type("Packet_Server"))!;
            Set(packet, "Id", 72);
            Set(packet, "RemoveBlockLight", payload);
            object handler = RuntimeHelpers.GetUninitializedObject(Type("Vintagestory.Client.NoObf.GeneralPacketHandler"));
            Set(handler, "game", client);
            AccessTools.Method(handler.GetType(), "RemoveBlockLight").Invoke(handler, new[] { packet });
            Drain();
        }

        Assert.Equal(18, storage.GetLightHsv(read, pos)[2]);
        Add();
        Drain();
        Assert.Equal(0, Light(above)); // Native client failure without correction.
        using var serverBinding = new NativeLightTestBinding(serverMap, pos.dimension);
        using(var patch = new NativeLightTestBinding(map, pos.dimension))
        {
            Add();
            Drain();
            Assert.True(patch.CorrectedLookups > 0);
            Assert.True(Light(above) > 0);
            int plain = Light(above);
            inventory[0]!.Itemstack!.Attributes.SetString("lining", "silver");
            Add();
            Drain();
            Assert.Equal(20, storage.GetLightHsv(read, pos)[2]);
            Assert.True(Light(above) > plain);
            Block torch = World.Api.World.GetBlock(new AssetLocation("game:torch-basic-lit-up"))!;
            BlockPos torchPos = pos.Copy().Add(4, 0, 0);
            SetClientBlock(torchPos, torch.Id);
            Add(torchPos, torch.Id);
            Drain();
            Remove(storage.GetLightHsv(read, pos));
            Assert.True(Light(above) > 0); // Overlapping torch must survive removal.
            Remove(torch.GetLightHsv(read, torchPos), torchPos);
            Assert.Equal(0, Light(above));
            Assert.Null(read.GetBlockEntity(new BlockPos(0).Set(pos.X, pos.InternalY, pos.Z)));
            Assert.NotEmpty((IEnumerable<long>)Get(client, "dirtyChunks"));

            // An overworld inventory-emission control uses the same read-only
            // fixture under a different key, never mutating the initialized BE.
            BlockPos overworld = new(pos.X, pos.Y, pos.Z);
            var overworldChunk = (IWorldChunk)map.GetType().GetMethod("GetChunk", new[] { typeof(BlockPos) })!
                .Invoke(map, new object[] { overworld })!;
            overworldChunk.BlockEntities[overworld.Copy()] = be;
            SetClientBlock(overworld, storage.Id);
            int before = patch.CorrectedLookups;
            Add(overworld);
            Drain();
            Assert.True(Light(overworld.Copy().Add(0, 1, 0)) > 0);
            Assert.Equal(before, patch.CorrectedLookups);
            Remove(storage.GetLightHsv(read, overworld), overworld);
            Assert.Equal(0, Light(overworld.Copy().Add(0, 1, 0)));
        }
        MethodInfo lookup = Type("Vintagestory.Common.BlockAccessorBase")
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Single(m => m.Name == "GetBlockEntity" && !m.IsGenericMethod && m.GetParameters().Length == 1);
        Assert.Contains("rifttraveler.dimension-light-native", Harmony.GetPatchInfo(lookup)!.Owners);
        serverBinding.Dispose();
        Assert.DoesNotContain("rifttraveler.dimension-light-native", Harmony.GetPatchInfo(lookup)?.Owners.ToArray() ?? Array.Empty<string>());
    }
}
