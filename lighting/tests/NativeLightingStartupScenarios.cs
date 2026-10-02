using Atlas.XUnit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace RiftTraveler.AtlasTests;

[AtlasDataFiles("Fixtures/NativeLighting", TargetPath = "ModConfig")]
public sealed class NativeLightingStartupScenarios : AtlasScenarioBase
{
    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task EnabledCandidate_ShouldStartBeforeLegacyWriterAndLightWithoutRepair()
    {
        object candidate = World.Api.ModLoader.GetModSystem("RiftTraveler.DimensionLightCompatibilitySystem");
        Assert.True((bool)candidate.GetType().GetProperty("IsServerActive",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(candidate)!);
        object legacy = World.Api.ModLoader.GetModSystem("RiftTraveler.FracturedDeepBlockLightAdapter");
        Assert.Equal(0L, AccessTools.Field(legacy.GetType(), "listener").GetValue(legacy));
        Assert.Equal(0L, AccessTools.Field(legacy.GetType(), "priorityListener").GetValue(legacy));
        Assert.Null(AccessTools.Field(legacy.GetType(), "illuminator").GetValue(legacy));
        Assert.Null(AccessTools.Field(legacy.GetType(), "harmony").GetValue(legacy));
        var player = await World.JoinPlayer("NativeStartup");
        Assert.True((await player.ExecuteCommand("/rt debug fractureddeep")).Ok);
        await World.Until(() => player.Position.dimension > 0, timeoutTicks: 1200);
        var pos = new BlockPos(1075, 90, 1075, player.Position.dimension);
        await World.Until(() => World.Api.WorldManager.GetChunk(pos) != null, timeoutTicks: 1200);
        object server = World.Api.World;
        object map = AccessTools.Field(server.GetType(), "WorldMap").GetValue(server)!;
        IBlockAccessor fixture = (IBlockAccessor)Activator.CreateInstance(server.GetType().Assembly
            .GetType("Vintagestory.Common.BlockAccessorRelaxed", true)!, map, server, false, false)!;
        Block storage = World.Api.World.GetBlock(new AssetLocation("game:groundstorage"))!;
        Block small = World.Api.World.GetBlock(new AssetLocation("game:lantern-small-up"))!;
        for(int dy = 0; dy <= 2; dy++) fixture.SetBlock(0, pos.Copy().Add(0, dy, 0));
        fixture.SetBlock(World.Api.World.GetBlock(new AssetLocation("game:rock-granite"))!.Id, pos.Copy().Add(0, -1, 0));
        fixture.SetBlock(storage.Id, pos);
        BlockEntity be = fixture.GetBlockEntity(pos);
        var inventory = (IInventory)be.GetType().GetProperty("Inventory")!.GetValue(be)!;
        inventory[0]!.Itemstack = new ItemStack(small);
        inventory[0]!.Itemstack!.Attributes.SetString("material", "bismuth");
        inventory[0]!.Itemstack!.Attributes.SetString("lining", "plain");
        inventory[0]!.Itemstack!.Attributes.SetString("glass", "plain");
        be.GetType().GetMethod("LightUpdate")!.Invoke(be, new object[] { inventory[0]!.Itemstack! });
        BlockPos above = pos.Copy().Add(0, 1, 0);
        int Light() => fixture.GetLightLevel(above, EnumLightLevelType.OnlyBlockLight);
        await World.Until(() => Light() > 0, timeoutTicks: 120);
        be.OnBlockBroken(player.Player);
        fixture.SetBlock(0, pos);
        await World.Until(() => Light() == 0, timeoutTicks: 120);
        Block seamstone = World.Api.World.GetBlock(new AssetLocation("rifttraveler:fractureddeepthresholdlight"))!;
        World.Api.World.BlockAccessor.SetBlock(seamstone.Id, pos);
        await World.Until(() => Light() > 0, timeoutTicks: 120);
        World.Api.World.BlockAccessor.SetBlock(0, pos);
        await World.Until(() => Light() == 0, timeoutTicks: 120);
        candidate.GetType().GetMethod("Dispose")!.Invoke(candidate, null);
        Assert.False((bool)candidate.GetType().GetProperty("IsServerActive",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(candidate)!);
    }
}
