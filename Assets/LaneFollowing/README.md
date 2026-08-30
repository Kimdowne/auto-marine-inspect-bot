# HSV lane-following prototype

This first implementation deliberately separates RGB perception from wheel control.

## Scene setup

1. Add a child Camera to the JetBot and point it forward/down so both lane markings are visible.
2. Add `HsvLaneDetector` to that camera. Leave its Camera field empty to use the same GameObject's Camera.
3. Add `LaneFollowerController` to the JetBot root and assign the detector and four WheelColliders.
4. Disable `JetBotAgent` and `TrackedRobotController` while testing. Two components must never command the same wheels.
5. Start at low torque. Confirm the green debug mask contains only lane paint, and the red/blue crosses lie near the lane centre.

The default HSV range targets yellow paint. Unity hue is normalized to 0..1; an OpenCV hue value can be converted with `unityHue = opencvHue / 179`.

## Calibration order

1. Camera pose and field of view.
2. HSV min/max, saturation, and value using the debug mask.
3. ROI bottom/top so the robot body and horizon are excluded.
4. Lateral and heading gains at low `cruiseCommand`.
5. Derivative gain only after the steering direction is confirmed.

`TwoBoundaries` requires a visible marking on both sides. Use `SingleCentreLine` if the course is built around one coloured guide line.

The controller stops when confidence is below `minimumConfidence` or the last result is older than `maximumDetectionAge`. This is only a simulation fail-safe and does not replace an independent physical emergency stop.

Junctions are not inferred from lane geometry. A separate navigation layer should identify a fiducial marker, update the current graph node, and follow the outgoing edge chosen by the mission planner.
