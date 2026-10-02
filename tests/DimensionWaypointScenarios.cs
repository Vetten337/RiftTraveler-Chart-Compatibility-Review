using System.Reflection;
using System.Runtime.CompilerServices;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Xunit;

namespace RiftTraveler.AtlasTests;

public sealed class DimensionWaypointScenarios : AtlasScenarioBase
{
    [AtlasScenario(TimeoutMs = 120_000)]
    public async Task NewMapMarkers_ShouldEncodeDimensionWithoutChangingExistingPositions()
    {
        ITestPlayer player = await World.JoinPlayer("MarkerTraveler");
        Assembly assembly = World.Api.ModLoader.GetModSystem("RiftTraveler.RiftTravelerModSystem").GetType().Assembly;
        Type adapter = assembly.GetType("RiftTraveler.DimensionWaypointCompatibility")!;
        MethodInfo encode = adapter.GetMethod("EncodeNewMarkerPosition", BindingFlags.Static | BindingFlags.NonPublic)!;
        Vec3d Encode(Vec3d pos, int dim) => (Vec3d)encode.Invoke(null, new object[] { pos, dim })!;

        Vec3d original = new(1035.5, 3, 968.5);
        Assert.Same(original, Encode(original, 0));
        Assert.Same(original, Encode(original, -1));
        Assert.Null(Encode(null!, 10));
        Vec3d encoded = Encode(original, 10);
        Assert.NotSame(original, encoded);
        Assert.Equal(3, original.Y);
        Assert.Equal(original.X, encoded.X);
        Assert.Equal(original.Z, encoded.Z);
        Assert.Equal(10 * (double)BlockPos.DimensionBoundary + 3, encoded.Y);
        Assert.Same(encoded, Encode(encoded, 10));
        Assert.Same(encoded, Encode(encoded, 11));
        Vec3d negative = new(1, -1, 2);
        Vec3d invalid = new(1, double.NaN, 2);
        Assert.Same(negative, Encode(negative, 10));
        Assert.Same(invalid, Encode(invalid, 10));

        CommandResult result = await player.ExecuteCommand("/rt debug fractureddeep");
        Assert.True(result.Ok, result.Message);
        await World.Until(() => player.Position.dimension > 0, timeoutTicks: 1200);
        int dimension = player.Position.dimension;
        // Exercise the actual patched dialog setter without creating a window.
        // Only its API field and auto-property are needed for this headless check.
        ICoreClientAPI clientApi = WaypointApiProxy.Create<ICoreClientAPI>(method => method.Name switch
        {
            "get_World" => null,
            "get_ModLoader" => World.Api.ModLoader,
            "get_Logger" => World.Api.Logger,
            _ => throw new NotSupportedException(method.Name)
        });
        ModSystem system = (ModSystem)Activator.CreateInstance(adapter)!;
        try
        {
            system.StartClientSide(clientApi);
            Assert.NotNull(adapter.GetField("harmony", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(system));
            Type dialogType = mapDialogType();
            object dialog = RuntimeHelpers.GetUninitializedObject(dialogType);
            Type? baseType = dialogType;
            FieldInfo? apiField = null;
            while(baseType != null && apiField == null)
            {
                apiField = baseType.GetField("capi", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                baseType = baseType.BaseType;
            }
            apiField!.SetValue(dialog, clientApi);
            PropertyInfo position = dialogType.GetProperty("WorldPos")!;
            position.SetValue(dialog, original);
            // A headless client has no player/world. The installed hook must
            // bind successfully and leave that position alone, not crash.
            Assert.Same(original, position.GetValue(dialog));
            encoded = Encode(original, dimension);
            position.SetValue(dialog, encoded);
            Assert.Same(encoded, position.GetValue(dialog));
            Assert.Equal(3, original.Y);
        }
        finally { system.Dispose(); }

        Type mapDialogType() => World.Api.ModLoader.GetModSystem("Vintagestory.GameContent.WorldMapManager")
            .GetType().Assembly.GetType("Vintagestory.GameContent.GuiDialogAddWayPoint")!;
        result = await player.ExecuteCommand(FormattableString.Invariant(
            $"/waypoint addati circle ={encoded.X} ={encoded.Y} ={encoded.Z} false green DimensionTest"));
        Assert.True(result.Ok, result.Message);

        // Read vanilla's actual saved waypoint model, not a parallel registry.
        object map = World.Api.ModLoader.GetModSystem("Vintagestory.GameContent.WorldMapManager");
        var layers = (System.Collections.IEnumerable)map.GetType().GetField("MapLayers")!.GetValue(map)!;
        object waypointLayer = layers.Cast<object>().Single(layer => layer.GetType().Name == "WaypointMapLayer");
        var waypoints = (System.Collections.IEnumerable)waypointLayer.GetType().GetField("Waypoints")!.GetValue(waypointLayer)!;
        object saved = waypoints.Cast<object>().Single(w => (string)w.GetType().GetField("Title")!.GetValue(w)! == "DimensionTest");
        Vec3d savedPos = (Vec3d)saved.GetType().GetField("Position")!.GetValue(saved)!;
        Assert.Equal(dimension, (int)(savedPos.Y / BlockPos.DimensionBoundary));
        Assert.Equal(encoded.Y, savedPos.Y);
        byte[] bytes = SerializerUtil.Serialize(savedPos);
        Assert.Equal(savedPos.Y, SerializerUtil.Deserialize<Vec3d>(bytes).Y);
    }
}

public class WaypointApiProxy : DispatchProxy
{
    private System.Func<MethodInfo, object?> handler = null!;
    public static T Create<T>(System.Func<MethodInfo, object?> handler) where T : class
    {
        T proxy = DispatchProxy.Create<T, WaypointApiProxy>();
        ((WaypointApiProxy)(object)proxy).handler = handler;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => handler(targetMethod!);
}
