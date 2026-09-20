# Continuous avoidance training

PPO outputs two continuous commands in [-1, 1]: signed forward/reverse and yaw.
These are normalized motor commands, not m/s. Zero permits stopping or pivoting.
Existing torque limits and slew limiting apply. Decisions occur every 0.2
simulation seconds, held between decisions, throughout training episodes.

20 observations are stacked four times. Observation 18 now contains signed
forward velocity / 2, not rear clearance. Scenarios and time_scale=10 are unchanged.
Lane confidence, offsets and ToF speed scaling do not modify policy commands.
Explicit safety-stop and drive-disable remain authoritative; train with the
existing monitor-only SafetySupervisor. No side/rear sensing is included.
This experimental configuration is not suitable for unrestricted real driving.

Avoidance-only training keeps existing encounter completion and collision rules.
Deployment uses the existing no-person/front-clear hold before low-speed lane
alignment. Without a lane it reorients to the entry heading without translating.
Recovery times out after 8 seconds and holds stopped. A new threat can interrupt
recovery. Camera disappearance cannot prove rear clearance in this scope.

Rebuild the player and start a NEW run without --resume or --initialize-from.
Discrete checkpoints and previous 18-observation checkpoints are incompatible.
Setup menus also use continuous actions.

Training bounds follow the yellow stripe centre lines in jetbot_env, in the
Line object's local XZ coordinates: the outer rectangle minus two equipment
islands. These coordinates are mapped from the actual stripe transforms.
The physical robot centre may cross a stripe by up to 0.5 world metres.
Greater distance from the permitted track terminates with LaneExit and -3.
Track interior and all junctions remain legal regardless of episode start.
Observations 19/20 give the robot-local direction/displacement back to the track,
normalized by 0.5 m (zero inside). Previous displacement observations are no
longer used; start a fresh run. Bounds/OutsideTrackMetres logs the distance.
Select the agent in Scene view to see the mapped outer and inner boundaries.
If individual yellow stripes are edited, update the serialized track rectangles
to match; moving/rotating the Line root moves the mapped region with it.
The penalty is training-only; these observations also exist at inference.
Inspect HumanAvoidance/Policy/MoveCommand and TurnCommand and actual movement.
Legacy direction labels summarize final commands, not categorical policy choices.

Validate in Unity: both speed/yaw signs, zero commands, occluded-lane motion,
collision termination, deployment recovery and recovery timeout.
Missing-model heuristic commands zero motion. Runtime training has not been
validated by the code change alone.
