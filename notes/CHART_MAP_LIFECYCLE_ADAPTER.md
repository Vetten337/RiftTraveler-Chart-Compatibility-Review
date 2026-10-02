# Chart 0.3.0 map database lifecycle adapter

The October 2 test reproduced a successful initial map followed by a crash
after returning to the main menu and reopening the same save. The earlier
LevelFinalize error was `Cannot open worldmap database ... not writable`.
WorldMapManager consequently had no initialized map layers, and opening either
the full map or minimap failed in GuiDialogWorldMap.ComposeDialog.

Inspection of the installed Chart 0.3.0 assembly identifies a concrete leak:
Chart removes the exact vanilla ChunkMapLayer at LevelFinalize without closing
its MapDB. Vanilla only shuts down layers still present in MapLayers on
LeaveWorld, so that detached SQLite connection misses cleanup.

`DimensionMapLifecycleCompatibility` patches Chart's existing removal callback
to remember only the exact vanilla layer actually removed. On LeaveWorld,
after vanilla's worker shutdown handler, it disposes that layer's private
database and clears the field. It intentionally does not dispose the layer's
shared renderer resources, manipulate active layers, delete cache files, change
waypoint storage, or alter the lighting adapter.

The adapter is client-only, enabled only for Chart version 0.3.0, signature
checked, idempotent, and uses its own removable Harmony ID. Future Chart
versions are not patched. Disable/remove this adapter when upstream cleanup is
available; do not replace Chart or Manifold with forks.

Atlas regression uses real engine MapDB/SQLite connections to reproduce the
write-access failure, verifies the detached connection unlocks and reopens,
checks repeated cleanup, and confirms an unrelated active connection stays
open. This is not a real client UI or full menu/reload integration test.

Manual acceptance: start fresh, open both maps, return to menu, reopen the same
save without quitting the application, and open both maps again. Repeat twice,
including a save located far from the Fractured Deep threshold. Expected log:
`[RiftTraveler] Released detached vanilla map database.` No purge is needed.

The separate observation that Chart stops drawing newly explored terrain at a
distance remains unverified and is not claimed fixed by database cleanup.

Verification: all 20 Atlas scenarios passed (October 2, 2026); Debug build
completed with zero warnings/errors and was deployed only to the local test
folder. The previous DLL is preserved at
`backups/chart-map-lifecycle-2026-10-02/RiftTraveler.previous.dll`.
Live server, official Chart ZIP, releases, and GitHub were not changed.
The subsequent client test repeated exploration, saving, and main-menu/reload
several times without map-opening crashes. Logs confirm database cleanup.
New terrain mapping still fails and remains a separate investigation.
