# Human avoidance training

1. Stop Unity Play Mode.
2. Run `Tools > Ship Robot > Training > Enable Human Avoidance Training`.
3. In a terminal opened at the project root, start the trainer:

   ```powershell
   mlagents-learn config/human_avoidance_ppo.yaml --run-id=HumanAvoidance_v1
   ```

4. When the trainer prints `Listening on port 5004`, enter Play Mode in Unity.

The in-game RL panel must show `connected True`, `train True`, and `apply True`. The Safety
panel must show `control MONITOR ONLY`: hazard state still supplies penalties and episode
termination, but it does not override the avoidance controller during training. If
`connected False` remains visible, stop immediately and reconnect instead of leaving Unity
running without a trainer.

## Weights & Biases monitoring

In the same Python environment that contains ML-Agents, install the compatible SDK:

```powershell
pip install -r config/requirements-wandb.txt
```

W&B 0.16.6 rejects the newer `wandb_v1_...` key in its interactive login prompt even though
the service can authenticate it. Do not use `wandb login`. Delete any key exposed in a
screenshot or terminal transcript, create a new key, and load it only into the current
PowerShell process with the masked prompt:

```powershell
. .\tools\set_wandb_key.ps1
```

Start a new monitored run through the wrapper instead of calling `mlagents-learn` directly:

```powershell
python tools/train_human_avoidance_wandb.py config/human_avoidance_ppo.yaml --run-id=HumanAvoidance_wandb_v1
```

The wrapper preserves the normal ML-Agents arguments. Append `--resume` when continuing the
same run. Set `WANDB_PROJECT` or `WANDB_ENTITY` before launching if a different W&B destination
is required.

The wrapper registers a native W&B stats writer and does not use W&B's hosted TensorBoard tab.
After the first summary interval (currently 2,000 trainer steps), the dashboard receives standard
PPO reward/loss charts plus every custom `HumanAvoidance/*` statistic. Each completed episode
also records `episode/final_reward`, `episode/failure_reason`, success, collision,
emergency-stop, lane-exit and timeout flags, minimum distance, the latched STOP/LEFT/RIGHT/REVERSE
decision, and whether a blocked side forced that decision back to STOP. The `trend/*_100`
charts are rolling 100-episode rates; use `trend/active_avoidance_success_rate_100` rather than the
raw per-episode 0/1 success line to judge learning progress.
The hosted TensorBoard tab retained on an older run is not used.

To continue an interrupted run, append `--resume`. Before normal mission validation, stop
Play Mode and run `Tools > Ship Robot > Training > Restore Safe Validation Mode`.

The W&B-hosted TensorBoard tab is optional and is not required for these native charts. An
error from that tab does not mean the trainer is connected; use the in-game `connected` value
and the trainer's step output as the source of truth.

Each episode creates one of three encounters. `ActiveAvoidanceRequired` sends the pedestrian
through the stationary robot footprint. `YieldRequired` synchronizes the pedestrian with the
nominal straight-driving robot at a future encounter point. `NaturalPass` misses both paths.
Distance, angle, speed, and extra travel distance are randomized. Collision, emergency
stop, or timeout ends the episode with a penalty. Persistent lane loss interrupts and excludes
the episode because PPO cannot directly control the lane detector. A completed pedestrian
route followed by the 0.5-second hold is scored as success or safe pass.

The initial foundation curriculum samples 80% active avoidance, 20% yield, and no natural
passes. Generated paths are verified against both a stationary robot and a time-aligned nominal
straight trajectory, with up to eight regeneration attempts. After 100 active-avoidance episodes
reach a rolling 70% success rate, the scene automatically advances to the 60/25/15 mixed
curriculum. Active avoidance earns +2 only after LEFT/RIGHT/REVERSE succeeds; passive STOP is a
-1 failure. Correct yield earns +1.2 for STOP or +1.0 for a moving avoidance, while natural pass
earns +0.05. W&B records both counterfactual clearances and separate active/yield success rates.

PPO now makes one high-level discrete decision per encounter: `0=STOP`, `1=LEFT`,
`2=RIGHT`, or `3=REVERSE`. That decision is latched for the encounter. It never writes wheel torque, raw
steering, or speed. The deterministic layer validates the selected ToF side, derives speed
from distance/TTC, and asks the normal HSV controller to follow a bounded virtual lane-centre
offset. LEFT/RIGHT retain the same PPO action but automatically use Small (0.28), Medium (0.45),
or Large (0.60) offset from ToF distance, TTC, and RGB box height. During a Large manoeuvre,
a temporary lane loss executes a short outward arc and then continues straight for a bounded
grace period. A blocked selected side is forced to STOP. REVERSE uses a rear-clearance observation,
backs straight at a bounded speed for at most one second, and then stops. The simulated rear
ray must be replaced by a physical rear ToF input on the real robot. Lane-perception thresholds relaxed for
accelerated training are runtime overrides and do not alter the normal mission profile.

The trainer configuration uses `engine_settings.time_scale: 10`. RGB, HSV lane, simulated
marker, ToF, robot control, and pedestrian motion all schedule against Unity simulation time;
changing `time_scale` therefore no longer changes their intended simulated-time intervals.

The active scene starts with curriculum stage `AvoidanceOnly`. An episode succeeds after the
pedestrian safely completes its route and the completion hold elapses; lane reacquisition is
not required in this first stage. After this policy is stable, switch the agent to
`AvoidanceAndLaneRecovery` and initialize the new run from the stage-1 checkpoint.
