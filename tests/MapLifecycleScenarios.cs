using System.Reflection;
using System.Runtime.CompilerServices;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Xunit;

namespace RiftTraveler.AtlasTests;

public sealed class MapLifecycleScenarios : AtlasScenarioBase
{
    [AtlasScenario(TimeoutMs = 120_000)]
    public Task DetachedVanillaMapDatabase_ShouldUnlockWithoutTouchingActiveLayers()
    {
        Assembly mod = World.Api.ModLoader.GetModSystem("RiftTraveler.RiftTravelerModSystem").GetType().Assembly;
        Type adapterType = mod.GetType("RiftTraveler.DimensionMapLifecycleCompatibility")!;
        Type layerType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("Vintagestory.GameContent.ChunkMapLayer"))
            .First(t => t != null)!;
        Type dbType = layerType.Assembly.GetType("Vintagestory.GameContent.MapDB")!;
        FieldInfo dbField = layerType.GetField("mapdb", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo track = adapterType.GetMethod("TrackDetachedLayer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo release = adapterType.GetMethod("ReleaseDatabases", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ModSystem adapter = (ModSystem)Activator.CreateInstance(adapterType)!;
        string directory = Path.Combine(Path.GetTempPath(), "RiftTraveler-MapLifecycle-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string detachedPath = Path.Combine(directory, "detached.db");
        string activePath = Path.Combine(directory, "active.db");
        SQLiteDBConnection Open(string path)
        {
            var database = (SQLiteDBConnection)Activator.CreateInstance(dbType, World.Api.Logger)!;
            string error = null!;
            Assert.True(database.OpenOrCreate(path, ref error, true, true, false), error);
            Assert.Null(error);
            return database;
        }
        using SQLiteDBConnection detached = Open(detachedPath);
        using SQLiteDBConnection active = Open(activePath);
        object removedLayer = RuntimeHelpers.GetUninitializedObject(layerType);
        object activeLayer = RuntimeHelpers.GetUninitializedObject(layerType);
        dbField.SetValue(removedLayer, detached);
        dbField.SetValue(activeLayer, active);
        try
        {
            // Reproduce the engine's exact write-access preflight while SQLite
            // still owns the connection removed from MapLayers by Chart.
            Assert.False(SQLiteDBConnection.HaveWriteAccessFile(new FileInfo(detachedPath)));
            track.Invoke(adapter, new[] { removedLayer });
            track.Invoke(adapter, new[] { removedLayer });
            release.Invoke(adapter, null);
            Assert.Null(dbField.GetValue(removedLayer));
            Assert.Same(active, dbField.GetValue(activeLayer));
            Assert.False(SQLiteDBConnection.HaveWriteAccessFile(new FileInfo(activePath)));
            Assert.True(SQLiteDBConnection.HaveWriteAccessFile(new FileInfo(detachedPath)));
            using SQLiteDBConnection reopened = Open(detachedPath);
            release.Invoke(adapter, null); // Repeat LeaveWorld/Dispose safely.
            adapter.Dispose();
            Assert.False(SQLiteDBConnection.HaveWriteAccessFile(new FileInfo(detachedPath)));
        }
        finally { adapter.Dispose(); }
        // Keep the uniquely named scratch DBs for diagnosis; never touch user maps.
        return Task.CompletedTask;
    }
}
