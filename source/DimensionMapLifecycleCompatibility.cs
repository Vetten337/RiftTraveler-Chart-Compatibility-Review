using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace RiftTraveler;

/// <summary>Release the vanilla database orphaned by Chart 0.3.0's layer removal.</summary>
public sealed class DimensionMapLifecycleCompatibility : ModSystem
{
    private const string HarmonyId = "rifttraveler.chart-map-lifecycle";
    private static readonly FieldInfo DatabaseField = AccessTools.Field(typeof(ChunkMapLayer), "mapdb");
    private readonly List<ChunkMapLayer> detachedLayers = new();
    private ICoreClientAPI client;
    private Harmony harmony;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        // Deliberately version-gated: newer Chart releases must own their lifecycle.
        var chart = api.ModLoader.GetMod("chart");
        if(chart?.Info.Version != "0.3.0") return;
        try
        {
            Type chartType = api.ModLoader.GetModSystem("Chart.ChartModSystem")?.GetType();
            MethodInfo finalize = chartType == null ? null : AccessTools.DeclaredMethod(chartType,
                "OnLevelFinalize", new[] { typeof(ICoreClientAPI), typeof(WorldMapManager),
                    typeof(Manifold.Api.Client.IManifoldClient) });
            if(finalize == null || DatabaseField == null ||
                !typeof(SQLiteDBConnection).IsAssignableFrom(DatabaseField.FieldType))
                throw new MissingMemberException("Chart/vanilla map lifecycle signature changed");
            client = api;
            harmony = new Harmony(HarmonyId);
            harmony.Patch(finalize,
                prefix: new HarmonyMethod(typeof(DimensionMapLifecycleCompatibility), nameof(BeforeRemoval)),
                postfix: new HarmonyMethod(typeof(DimensionMapLifecycleCompatibility), nameof(AfterRemoval)));
            api.Event.LeaveWorld += ReleaseDatabases;
            api.Logger.Notification("[RiftTraveler] Chart 0.3.0 detached map database cleanup installed.");
        }
        catch(Exception exception)
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;
            client = null;
            api.Logger.Warning("[RiftTraveler] Chart lifecycle adapter skipped: {0}", exception.Message);
        }
    }

    private static void BeforeRemoval(WorldMapManager __1, out ChunkMapLayer __state)
    {
        __state = __1.MapLayers.Find(layer => layer.GetType() == typeof(ChunkMapLayer)) as ChunkMapLayer;
    }

    private static void AfterRemoval(ICoreClientAPI __0, WorldMapManager __1, ChunkMapLayer __state)
    {
        if(__state != null && !__1.MapLayers.Contains(__state))
            __0.ModLoader.GetModSystem<DimensionMapLifecycleCompatibility>()?.TrackDetachedLayer(__state);
    }

    internal void TrackDetachedLayer(ChunkMapLayer layer)
    {
        if(!detachedLayers.Contains(layer)) detachedLayers.Add(layer);
    }

    internal void ReleaseDatabases()
    {
        // WorldMapManager's earlier LeaveWorld handler stops its worker. Do not
        // call layer.Dispose/OnShutDown: they also touch shared renderer textures.
        foreach(ChunkMapLayer layer in detachedLayers.ToArray())
        {
            try
            {
                if(DatabaseField.GetValue(layer) is SQLiteDBConnection database)
                {
                    database.Dispose();
                    DatabaseField.SetValue(layer, null);
                    detachedLayers.Remove(layer);
                    client?.Logger.Notification("[RiftTraveler] Released detached vanilla map database.");
                }
                else detachedLayers.Remove(layer);
            }
            catch(Exception exception)
            {
                client?.Logger.Warning("[RiftTraveler] Detached map database cleanup failed: {0}", exception.Message);
            }
        }
    }

    public override void Dispose()
    {
        if(client != null) client.Event.LeaveWorld -= ReleaseDatabases;
        ReleaseDatabases();
        harmony?.UnpatchAll(HarmonyId);
        harmony = null;
        client = null;
        base.Dispose();
    }
}
