using System.Reflection;
using System.Runtime.CompilerServices;
using Atlas.XUnit;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Xunit;

namespace RiftTraveler.AtlasTests;

public sealed class DimensionLightPacketScenarios : AtlasScenarioBase
{
    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task RemovalPacket_ShouldPreserveOptedInDimensionThroughNativeClientHandler()
    {
        World.Api.ModLoader.GetModSystem("RiftTraveler.FracturedDeepBlockLightAdapter").Dispose();
        var player = await World.JoinPlayer("PacketLight");
        Assert.True((await player.ExecuteCommand("/rt debug fractureddeep")).Ok);
        await World.Until(() => player.Position.dimension > 0, timeoutTicks: 1200);
        object server = World.Api.World;
        Assembly engine = server.GetType().Assembly;
        object map = server.GetType().GetField("WorldMap")!.GetValue(server)!;
        var pos = new BlockPos(1075, 90, 1075, player.Position.dimension);
        await World.Until(() => World.Api.WorldManager.GetChunk(pos) != null, timeoutTicks: 1200);
        byte[] hsv = { 4, 2, 18 };
        using var prototype = new ScopedRemovalPacketPrototype(engine, server, map, pos.dimension);
        MethodInfo remove = map.GetType().GetMethod("RemoveBlockLight")!;

        BlockPos Receive(BlockPos at)
        {
            prototype.LastPacket = null;
            remove.Invoke(map, new object[] { hsv, at });
            Assert.True(prototype.LastPacket != null, $"No packet captured; scopes={prototype.Entries}, broadcasts={prototype.Broadcasts}");
            // Round-trip the real outgoing packet with the engine's wire serializer.
            Type serializer = engine.GetType("Packet_ServerSerializer", true)!;
            byte[] wire = (byte[])serializer.GetMethod("SerializeToBytes")!
                .Invoke(null, new[] { prototype.LastPacket })!;
            object received = serializer.GetMethod("DeserializeBuffer")!.Invoke(null,
                new object[] { wire, wire.Length, Activator.CreateInstance(engine.GetType("Packet_Server", true)!)! })!;

            // Run the actual client handler, replacing only its block accessor sink.
            // This tests packet decoding/position construction, not GPU rendering.
            var sink = DispatchProxy.Create<IBlockAccessor, LightRemovalSink>();
            object client = RuntimeHelpers.GetUninitializedObject(engine.GetType("Vintagestory.Client.NoObf.ClientMain", true)!);
            object clientMap = RuntimeHelpers.GetUninitializedObject(engine.GetType("Vintagestory.Client.NoObf.ClientWorldMap", true)!);
            clientMap.GetType().GetField("RelaxedBlockAccess")!.SetValue(clientMap, sink);
            client.GetType().GetField("WorldMap")!.SetValue(client, clientMap);
            Type handlerType = engine.GetType("Vintagestory.Client.NoObf.GeneralPacketHandler", true)!;
            object handler = RuntimeHelpers.GetUninitializedObject(handlerType);
            AccessTools.Field(handlerType, "game").SetValue(handler, client);
            AccessTools.Method(handlerType, "RemoveBlockLight").Invoke(handler, new[] { received });
            var capture = (LightRemovalSink)(object)sink;
            Assert.Equal(hsv, capture.Hsv);
            Assert.NotNull(capture.Position);
            return capture.Position!;
        }

        // Characterize the installed engine: FD removal is misrouted to overworld.
        prototype.Enabled = false;
        BlockPos baseline = Receive(pos);
        Assert.Equal(0, baseline.dimension);
        Assert.Equal(pos.Y, baseline.Y);

        prototype.Enabled = true;
        BlockPos corrected = Receive(pos);
        Assert.Equal(pos, corrected);
        Assert.Equal(90, pos.Y); // Never rewrite the caller/server-task position.
        Assert.Equal(player.Position.dimension, pos.dimension);
        Assert.Equal(hsv, new byte[] { 4, 2, 18 });
        Assert.Equal(new BlockPos(1075, 90, 1075), Receive(new BlockPos(1075, 90, 1075)));

        var excluded = new BlockPos(1075, 90, 1075, pos.dimension + 1);
        Assert.Equal(0, Receive(excluded).dimension); // Explicit opt-in only.
        prototype.Enabled = false;
        Assert.Equal(0, Receive(pos).dimension);
        using(var native = new NativeLightTestBinding(map, pos.dimension, dispatch: false, lookups: false))
        {
            Assert.Equal(pos, Receive(pos));
            Assert.True(native.CorrectedPackets > 0);
            Assert.Equal(0, Receive(excluded).dimension);
            Assert.Equal(0, Receive(new BlockPos(pos.X, pos.Y, pos.Z)).dimension);
        }
        Assert.Equal(0, Receive(pos).dimension); // Runtime teardown restores the baseline.
        prototype.Dispose();
        Assert.DoesNotContain("rifttraveler.atlas.dimension-light-packet-prototype",
            Harmony.GetPatchInfo(remove)?.Owners.ToArray() ?? Array.Empty<string>());
    }
}

public class LightRemovalSink : DispatchProxy
{
    public BlockPos? Position;
    public byte[]? Hsv;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Assert.Equal("RemoveBlockLight", targetMethod!.Name);
        Hsv = (byte[])((byte[])args![0]!).Clone();
        Position = ((BlockPos)args[1]!).Copy();
        return null;
    }
}

/// <summary>Test-only wire correction. No extra packets or lighting writes.</summary>
internal sealed class ScopedRemovalPacketPrototype : IDisposable
{
    private const string Id = "rifttraveler.atlas.dimension-light-packet-prototype";
    private readonly Harmony harmony = new(Id);
    private static ScopedRemovalPacketPrototype? active;
    private readonly object server;
    private readonly object map;
    private readonly int dimension;
    [ThreadStatic] private static BlockPos? removal;
    public bool Enabled;
    public object? LastPacket;
    public int Entries;
    public int Broadcasts;

    public ScopedRemovalPacketPrototype(Assembly engine, object server, object map, int dimension)
    {
        this.server = server;
        this.map = map;
        this.dimension = dimension;
        active = this;
        try
        {
            MethodInfo broadcast = server.GetType().GetMethods().Single(m => m.Name == "BroadcastPacket"
                && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.Name == "Packet_Server");
            harmony.Patch(broadcast,
                prefix: new HarmonyMethod(typeof(ScopedRemovalPacketPrototype), nameof(PreparePacket)),
                postfix: new HarmonyMethod(typeof(ScopedRemovalPacketPrototype), nameof(ObservePacket)));
            // Patch the callee first so the rebuilt removal method cannot inline
            // the tiny, otherwise unpatched BroadcastPacket wrapper.
            harmony.Patch(map.GetType().GetMethod("RemoveBlockLight"),
                prefix: new HarmonyMethod(typeof(ScopedRemovalPacketPrototype), nameof(Enter)),
                finalizer: new HarmonyMethod(typeof(ScopedRemovalPacketPrototype), nameof(Exit)));
        }
        catch { Dispose(); throw; }
    }

    private static void Enter(object __instance, BlockPos __1, out BlockPos? __state)
    {
        __state = removal;
        if(active != null) active.Entries++;
        removal = ReferenceEquals(active?.map, __instance) ? __1.Copy() : null;
    }

    private static void Exit(BlockPos? __state) => removal = __state;

    private static void PreparePacket(object __instance, object __0)
    {
        var owner = active;
        BlockPos? pos = removal;
        if(owner == null || !owner.Enabled || !ReferenceEquals(owner.server, __instance)
            || pos == null || pos.dimension != owner.dimension) return;
        if((int)__0.GetType().GetField("Id")!.GetValue(__0)! != 72) return;
        object payload = __0.GetType().GetField("RemoveBlockLight")!.GetValue(__0)!;
        Type type = payload.GetType();
        if((int)type.GetField("PosX")!.GetValue(payload)! != pos.X
            || (int)type.GetField("PosZ")!.GetValue(payload)! != pos.Z
            || (int)type.GetField("PosY")!.GetValue(payload)! != pos.Y) return;
        type.GetField("PosY")!.SetValue(payload, pos.InternalY);
    }

    private static void ObservePacket(object __instance, object __0)
    {
        if(active != null) active.Broadcasts++;
        if(active is { } owner && ReferenceEquals(owner.server, __instance) && removal != null
            && (int)__0.GetType().GetField("Id")!.GetValue(__0)! == 72) owner.LastPacket = __0;
    }

    public void Dispose()
    {
        harmony.UnpatchAll(Id);
        if(ReferenceEquals(active, this)) active = null;
    }
}
