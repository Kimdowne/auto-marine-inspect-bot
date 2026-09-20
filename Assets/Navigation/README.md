# Plant navigation graph

The six supplied marker locations are modeled as a 3-by-2 ladder graph:

```text
UpperLeft --- UpperMid --- UpperRight
    |             |             |
UnderLeft --- UnderMid --- UnderRight
```

`UnderMid` is the initial base node. Default mission routes are:

- Perimeter: UnderMid, UnderLeft, UpperLeft, UpperMid, UpperRight, UnderRight, UnderMid
- Equipment A: UnderMid, UnderLeft, UpperLeft, UpperMid, UnderMid
- Equipment B: UnderMid, UpperMid, UpperRight, UnderRight, UnderMid
- Return: shortest path from the last localized node to UnderMid

## Unity setup

Stop Play Mode and run `Tools > Ship Robot > Setup AprilTag Navigation`. The editor tool attaches markers directly to the six named scene objects:

- upper_left: UpperLeft
- upper_right: UpperRight
- upper_mid: UpperMid
- under_left: UnderLeft
- under_right: UnderRight
- under_mid: UnderMid

It creates six floor signs containing a 0.3 m tag and white detection margin, plus `NavigationSystem`, `PlantRouteGraph`, and `MissionRoutePlanner`. It is safe to run again; existing generated markers are updated rather than duplicated.

The setup also attaches `SimulatedMarkerObservationSource` to `front_camera`. During Play Mode it draws a green box, marker ID, node name, distance, and confidence over the camera viewport. The `SIM MARKER` prefix is intentional: this validates scene visibility and placement, not pixel decoding by the physical AprilTag library.

## First executable mission

`NavigationCoordinator` provides a single-edge mission from UnderMid (6) to UpperMid (3). Press `START MISSION` in Play Mode; lane following starts, marker 3 is confirmed within 1.1 m for three frames, and the robot brakes with state `Completed`. `RESET / STOP` immediately stops and resets the mission.

The perimeter mission uses the requested base route `6 > 3 > 1 > 4 > 6 > 5 > 2 > 3`. From any other localized node it rotates the cycle `3 > 1 > 4 > 6 > 5 > 2 > 3` to start there, visits all six nodes, and returns to its start. At each confirmed marker, the coordinator uses the graph edge heading to rotate in place and then resumes HSV lane following.

After a marker is confirmed, the robot continues straight while independently observing the left and right entry boundaries. Rotation starts only after either side has disappeared for 30 consecutive frames. Wide horizontal intersection paint is excluded from side-boundary evidence. A minimum travel distance rejects momentary detection noise, while a maximum distance stops the robot if both entry boundaries never disappear. The robot then searches for the exit pair and performs virtual-centre-line alignment.

If the boundary pair remains lost during visual alignment, or normal lane following loses the lane for the configured number of frames, the coordinator enters `StraightToNextMarker`. It drives slowly and accepts only the expected next marker ID. The normal marker arrival logic resumes inside the decision distance. Maximum fallback distance and time prevent unlimited blind travel.

The editor setup explicitly creates maneuver profiles for all six perimeter transitions: `6-3-1`, `3-1-4`, `1-4-6`, `4-6-5`, `6-5-2`, and `5-2-3`. Each triple means entry node, current junction node, and exit node. The 30-frame one-side-loss rule is global and therefore applies to every profile.

Marker image decoding and branch-transition control are deliberately behind `IMarkerObservationSource`; an AprilTag implementation can be attached without changing the graph or mission planner.
