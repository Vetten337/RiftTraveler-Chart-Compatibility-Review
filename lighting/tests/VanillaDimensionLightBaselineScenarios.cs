using System.Reflection;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace RiftTraveler.AtlasTests;

/// <summary>
/// Characterization of the installed 1.22.7 engine, not an assertion that the bug
/// is desirable. Expected results must change when upstream or a prototype fixes it.
/// </summary>
public sealed class VanillaDimensionLightBaselineScenarios : AtlasScenarioBase
{
    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task VanillaPropagator_ShouldExposeDimensionLostInGroundStorageLookup()
    {
        // Disable repairs only in this class's disposable Atlas world. Never touch
        // the deployed mod/config, and don't mutate an illuminator used by its worker.
        ModSystem adapter = World.Api.ModLoader.GetModSystem("RiftTraveler.FracturedDeepBlockLightAdapter");
        adapter.Dispose();
        foreach(string name in new[] { "listener", "priorityListener" })
            Assert.Equal(0L, adapter.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter));
        Assert.Null(adapter.GetType().GetField("illuminator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter));
        Assert.Null(adapter.GetType().GetField("harmony", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(adapter));

        ITestPlayer player = await World.JoinPlayer("VanillaLight");
        BlockPos overworld = player.Position.Copy().Add(0, 8, 0);
        Assert.Equal(0, overworld.dimension);
        var blocks = World.Api.World.BlockAccessor;
        await World.Until(() => World.Api.WorldManager.GetChunk(overworld) != null, timeoutTicks: 1200);

        object server = World.Api.World;
        Assembly engine = server.GetType().Assembly;
        object map = server.GetType().GetField("WorldMap")!.GetValue(server)!;
        Type accessorType = engine.GetType("Vintagestory.Common.BlockAccessorRelaxed", throwOnError: true)!;
        IBlockAccessor vanillaAccessor = (IBlockAccessor)Activator.CreateInstance(accessorType,
            new object[] { map, World.Api.World, false, false })!;
        Type illuminatorType = engine.GetType("Vintagestory.Common.ChunkIlluminator", throwOnError: true)!;
        object illuminator = Activator.CreateInstance(illuminatorType,
            new object[] { map, vanillaAccessor, 32 })!;
        illuminatorType.GetMethod("InitForWorld")!.Invoke(illuminator, new object[] {
            World.Api.World.Blocks, (ushort)World.Api.World.SunBrightness,
            World.Api.WorldManager.MapSizeX, World.Api.WorldManager.MapSizeY, World.Api.WorldManager.MapSizeZ });
        MethodInfo place = illuminatorType.GetMethod("PlaceBlockLight")!;
        MethodInfo remove = illuminatorType.GetMethod("RemoveBlockLight")!;

        Block small = World.Api.World.GetBlock(new AssetLocation("game:lantern-small-up"))!;
        Assert.NotNull(small);
        int groundStorage = World.Api.World.GetBlock(new AssetLocation("game:groundstorage"))!.Id;
        int granite = World.Api.World.GetBlock(new AssetLocation("game:rock-granite"))!.Id;
        var lantern = new ItemStack(small);
        lantern.Attributes.SetString("material", "bismuth");
        lantern.Attributes.SetString("lining", "plain");
        lantern.Attributes.SetString("glass", "plain");

        BlockEntity InstallLantern(BlockPos pos)
        {
            // Keep the inventory fixture supported; no relights or repair calls.
            for(int dx = -1; dx <= 1; dx++)
            for(int dz = -1; dz <= 1; dz++)
            for(int dy = 0; dy <= 2; dy++) vanillaAccessor.SetBlock(0, pos.Copy().Add(dx, dy, dz));
            vanillaAccessor.SetBlock(granite, pos.Copy().Add(0, -1, 0));
            vanillaAccessor.SetBlock(groundStorage, pos);
            BlockEntity be = blocks.GetBlockEntity(pos);
            Assert.NotNull(be);
            IInventory inventory = (IInventory)be.GetType().GetProperty("Inventory")!.GetValue(be)!;
            inventory[0]!.Itemstack = lantern.Clone();
            return be;
        }

        int AboveLight(BlockPos pos) => blocks.GetLightLevel(pos.Copy().Add(0, 1, 0), EnumLightLevelType.OnlyBlockLight);
        void Propagate(byte[] hsv, BlockPos pos) => place.Invoke(illuminator, new object[] { hsv, pos.X, pos.InternalY, pos.Z });
        void Remove(byte[] hsv, BlockPos pos) => remove.Invoke(illuminator, new object[] { hsv, pos.X, pos.InternalY, pos.Z });

        BlockEntity overworldBe = InstallLantern(overworld);
        // This reproduces the exact coordinate construction in CollectLightValuesForLightSource.
        BlockPos raw = new BlockPos(0).Set(overworld.X, overworld.InternalY, overworld.Z);
        Assert.Same(overworldBe, vanillaAccessor.GetBlockEntity(raw));
        byte[] hsv = blocks.GetBlock(overworld).GetLightHsv(vanillaAccessor, raw);
        Assert.Equal(18, hsv[2]);
        Propagate(hsv, overworld);
        Assert.True(AboveLight(overworld) > 0, "Ordinary engine propagation must work for the overworld lantern control.");
        vanillaAccessor.SetBlock(0, overworld);
        Remove(hsv, overworld);
        Assert.Equal(0, AboveLight(overworld));

        CommandResult transit = await player.ExecuteCommand("/rt debug fractureddeep");
        Assert.True(transit.Ok, transit.Message);
        await World.Until(() => player.Position.dimension > 0, timeoutTicks: 1200);
        BlockPos deep = new(1075, 90, 1075, player.Position.dimension);
        await World.Until(() => World.Api.WorldManager.GetChunk(deep) != null, timeoutTicks: 1200);
        BlockEntity deepBe = InstallLantern(deep);
        Assert.Same(deepBe, vanillaAccessor.GetBlockEntity(deep));
        hsv = blocks.GetBlock(deep).GetLightHsv(vanillaAccessor, deep);
        Assert.Equal(18, hsv[2]); // The item/inventory is valid; this is not a missing-light asset.
        Assert.Equal(0, AboveLight(deep));

        raw = new BlockPos(0).Set(deep.X, deep.InternalY, deep.Z);
        Assert.Equal(0, raw.dimension);
        Assert.NotEqual(deep.Y, raw.Y);
        Assert.Null(vanillaAccessor.GetBlockEntity(raw));
        Assert.Equal(0, blocks.GetBlock(deep).GetLightHsv(vanillaAccessor, raw)[2]);
        BlockPos normalized = new(raw.X, raw.InternalY, raw.Z);
        Assert.Equal(deep, normalized);
        Assert.Same(deepBe, vanillaAccessor.GetBlockEntity(normalized));
        Assert.Equal(18, blocks.GetBlock(deep).GetLightHsv(vanillaAccessor, normalized)[2]);

        // Even when supplied the correct initial HSV, vanilla re-reads the source
        // using its malformed position and cannot spread the inventory's light.
        Propagate(hsv, deep);
        Assert.Equal(0, AboveLight(deep));
        Console.WriteLine($"BASELINE: dimension={deep.dimension}; normalized HSV=18; encoded-Y/raw-dimension-0 HSV=0; vanilla lantern neighbor light={AboveLight(deep)}.");
        vanillaAccessor.SetBlock(0, deep);
        Remove(hsv, deep);

        // Static block emission avoids inventory lookup: same propagator/dimension.
        Block torch = World.Api.World.GetBlock(new AssetLocation("game:torch-basic-lit-up"))!;
        Assert.NotNull(torch);
        vanillaAccessor.SetBlock(torch.Id, deep);
        byte[] torchHsv = torch.GetLightHsv(vanillaAccessor, raw);
        Assert.True(torchHsv[2] > 0);
        Propagate(torchHsv, deep);
        Assert.True(AboveLight(deep) > 0, "Torch control must propagate in the same custom dimension.");
        vanillaAccessor.SetBlock(0, deep);
        Remove(torchHsv, deep);
        Assert.Equal(0, AboveLight(deep));
        Assert.Equal(0, AboveLight(overworld));
    }
}
