# Equipment inspection demo

Selected checkpoint: continuous_action_v2/HumanAvoidance-2749897.pt.
This is the closest stored checkpoint to the chart point at 2,776,000; the
chart's smoothed training success is not a measured demo success rate.
Model asset: Assets/Resources/ShipRobotVision/HumanAvoidanceDemo.onnx.
Its provenance JSON records the source hash. Export uses strict weight loading,
80 stacked vector inputs and 2 continuous outputs, and ONNX checker validation.

Open the original Assets/jetbot_env.unity and press Play. The Equipment A+B
mission starts automatically; no Python trainer, W&B login, or UI button is needed.
The robot follows the Equipment A route, pauses 3 seconds at inspect_point_A1
and inspect_point_A2, travels right from UnderMid to UnderRight, turns up the
right aisle, and pauses at inspect_point_B2 and inspect_point_B1 in that order.
It then travels UpperRight -> UpperMid -> UnderMid. The B points
use the left-to-right route-marker offset from A at runtime if the scene does not yet contain B markers;
running the Demo setup command saves them into the scene. Inspection is a timed demo; no
equipment diagnostic measurements are produced by this timer.

Control order: explicit stop/inspection and ADAS emergency brake, PPO avoidance
and recovery, then lane/route assistance. ADAS scales ordinary lane speed and
can emergency-brake at 0.35 m or TTC 0.75 s. PPO owns move/turn during avoidance;
its commands are not attenuated by the lane confidence or ADAS slowdown factor.
This emergency brake may stop PPO in very close encounters.
Mission timers and junction progress pause during avoidance/ADAS stops.
At a corner, exit-lane alignment must finish before advancing to the next
marker. The robot turns toward the planned exit yaw, then probes forward at
low speed for at most 1.2 m if the exit boundaries are not yet visible. It
does not continue spinning beyond the exit heading. If alignment fails, it
can retry twice and then stops with a visible mission fault. A blind straight
fallback is only used on an already active lane leg, not to complete a corner.
Every corner begins that turn when an entry boundary disappears after the
minimum approach, or at the common 1.5 m approach limit if both remain visible.
The UnderMid -> UnderRight bottom crossing is guided toward the scene's ID 5
QR marker instead of holding its previous heading. Once the bottom-right
approach reaches the action radius around the intended turn area, it enters
the same approach, boundary-loss, exit-turn, and lane-alignment states as the
other corners. No corner has a direct QR-triggered turn bypass.
Training resets and training track-exit termination are disabled in this scene.
The demo reuses Handyman_ver_1 for every encounter; any extra Handyman_ver_2
in the scene is hidden. The first encounter always appears far ahead on
UnderMid -> UpperMid and walks toward and past the robot. The same person is
then parked far outside the camera view, without deactivating the object, and
teleported ahead of randomly selected later long
straights: UpperLeft -> UnderLeft, UnderRight -> UpperRight, and UpperMid ->
UnderMid. At least one later leg is selected per Play run, so there are 2-4
encounters in total. Start position across the aisle and walking speed vary.
The scripted sequence is disabled in training mode.

After the person is no longer detected and front clearance stays >=1.6 m for
0.6 seconds, lane recovery takes over. The current recovery stops after 12 seconds
without alignment. Restarting Play clears an avoidance fault.
Camera loss does not verify side/rear clearance, and recovery still needs demo
testing with the selected model.

To apply the model to the open original scene, use Tools > Ship Robot > Demo >
Apply RL Model To Current Jetbot Scene. This saves the current jetbot_env in place
and does not open/copy another scene. Model export is available through
tools/export_avoidance_demo.py. Build with jetbot_env as the startup scene.
The older EquipmentInspectionDemo copy is no longer needed for this workflow.
Open Equipment Monitoring Prototype opens a different CSV monitoring scene;
it is not the robot driving demo.

Rehearsal checklist:
1. The first straight has one encounter; after it ends the person disappears.
2. At least one later long straight reuses the same person at a new position.
3. A1, A2, B2 and B1 each pause once for 3 seconds; mission completes.
4. During inspection: PPO must not start movement.
5. Close obstacle: ADAS brakes; clearing it resumes the pending mission.
6. RESET / STOP and mission completion stop all drive commands.
7. Loss of lane during recovery reaches a visible stopped recovery fault.
