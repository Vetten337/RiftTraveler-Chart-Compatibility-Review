# Chart 0.3.0 compatibility findings — review for Léon

October 2, 2026. Tested with official Chart 0.3.0, Manifold 0.6.0,
Vintage Story 1.22.7, and Rift Traveler 0.7.4 development builds.

This is a focused **source snapshot for review**, not a standalone mod or a
release. No Chart/Manifold binaries, decompiled upstream source, player logs,
savegames, or lighting adapters are included. Our intent is to keep using
official upstream builds and retire temporary adapters when fixes ship.

## Files

- [Reproduction steps, diagnostic excerpts, and test limitations](REPRODUCTION.md)
- [Map lifecycle adapter](source/DimensionMapLifecycleCompatibility.cs)
- [Map lifecycle Atlas regression](tests/MapLifecycleScenarios.cs)
- [Map lifecycle implementation notes](notes/CHART_MAP_LIFECYCLE_ADAPTER.md)
- [Map-click waypoint adapter](source/DimensionWaypointCompatibility.cs)
- [Waypoint Atlas regression](tests/DimensionWaypointScenarios.cs)
- [Waypoint implementation notes](notes/DIMENSION_WAYPOINT_ADAPTER.md)

These files are copied from the tested working project. They require its
Vintage Story/Harmony/Manifold references and Atlas test setup; the tests are
not independently runnable from this snapshot. The existing project uses
.NET 10, Pixnop.Manifold 0.6.0, and Pixnop.Atlas.XUnit 0.15.1. Engine assemblies
are referenced from the local game installation.

## 1. Map database lifecycle — reproduced, adapter tested

Reproduction: open a save and its map, return to the main menu, reopen the same
save without exiting the application, then open the map/minimap again.

The startup failure precedes the GUI crash:

```text
System.IO.IOException: Cannot open worldmap database file ... not writable!
  at SQLiteDBConnection.OpenOrCreate(...)
  at ChunkMapLayer..ctor(...)
  at WorldMapManager.OnLvlFinalize()
```

Opening the map after failed initialization then throws
`ArgumentOutOfRangeException` in `GuiDialogWorldMap.ComposeDialog`.

Inspection of the installed Chart removal callback shows that the exact vanilla
`ChunkMapLayer` is removed from `WorldMapManager.MapLayers` without closing its
database. Vanilla's LeaveWorld handler shuts down only the remaining layers.
The removed layer therefore misses the call which disposes its SQLite MapDB.

Our temporary adapter records the vanilla layer actually detached by Chart,
then disposes only its database on LeaveWorld. It avoids layer-wide disposal
because that touches shared renderer resources. It is client-only, gated to
Chart 0.3.0, checks the expected members, and owns a separate Harmony ID.

Verification: the real MapDB Atlas regression reproduces the write-access
failure, verifies unlocking/reopening and idempotence, and leaves another active
database untouched. All 20 scenarios in our working suite passed. The user
subsequently repeated exploration/save/main-menu/reload several times without
map-opening crashes, and the log confirms detached database cleanup.

## 2. New terrain mapping — observed; suspected cause, not patched

While exploring the Fractured Deep, Chart stops adding new mapped areas. Across
the recent exploration/reload tests it continues saving **375 tiles**.

In the installed `DimensionAwareChunkMapLayer.ProcessChunk`, the initial
`BlockAccessor.GetMapChunk(cx, cz)` lookup must succeed before dimension-aware
chunk slices are fetched. A null result returns immediately.

Our Manifold tests separately establish that loaded custom-dimension terrain
can exist without an overworld map chunk at the corresponding X/Z. We suspect
this early requirement explains the mapping boundary. We have **not** captured
the failing branch in the running client, so this is a source-backed hypothesis,
not a complete runtime diagnosis. Slice availability and queue timing are also
worth checking. The lifecycle adapter does not claim to fix terrain mapping.

Does the upcoming cavern-mapping update remove that overworld map-chunk
dependency? We would prefer to test the upstream solution rather than implement
a replacement renderer or a second map store.

## 3. Map-click waypoint dimension — temporary adapter

As discussed, the map dialog can supply local Y to `/waypoint addati`, placing
the marker in the overworld according to Chart's encoded-Y filtering.

Our adapter clones a new dialog position and adds
`dimension * BlockPos.DimensionBoundary` only for a nonzero player dimension
and an unencoded, finite local Y. It leaves already encoded positions, vanilla
server commands/storage, existing markers, and editing untouched. It does not
fix older markers whose intended dimension is unknown.

The command workaround and adapted map-dialog creation have both worked in
game. Atlas checks encoding, idempotence, setter patch installation and vanilla
server storage, but does not render Chart pins. We understand the upstream fix
is planned and intend to remove this adapter after verifying it.

Thanks for reviewing — T0xx
