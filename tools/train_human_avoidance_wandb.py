"""Run ML-Agents with native Weights & Biases metric reporting."""

import os
import re
import sys
from collections import deque
from typing import Any, Dict, Optional

import wandb


class WandbStatsWriter:
    """ML-Agents StatsWriter-compatible adapter that logs directly to W&B."""

    def __init__(self, run: Any) -> None:
        self.run = run
        self.episode_index = 0
        self.recent_success = deque(maxlen=100)
        self.recent_collision = deque(maxlen=100)
        self.recent_emergency = deque(maxlen=100)
        self.recent_natural_pass = deque(maxlen=100)
        self.recent_intervention_success = deque(maxlen=100)
        self.recent_intervention_required = deque(maxlen=100)
        self.recent_yield_success = deque(maxlen=100)
        self.recent_yield_required = deque(maxlen=100)
        self.recent_stop_decisions = deque(maxlen=100)
        self.recent_required_stop_success = deque(maxlen=100)
        self.recent_active_stop_decisions = deque(maxlen=100)
        self.recent_yield_stop_decisions = deque(maxlen=100)
        self.recent_active_action_success = {
            name: deque(maxlen=100) for name in ("left", "right", "reverse")
        }

    @staticmethod
    def _finite_float(value: Any) -> Optional[float]:
        try:
            result = float(value)
        except (TypeError, ValueError):
            return None
        return result if result == result and abs(result) != float("inf") else None

    @staticmethod
    def _find(values: Dict[str, Any], suffix: str) -> Optional[Any]:
        for key, summary in values.items():
            if key == suffix or key.endswith("/" + suffix):
                return summary
        return None

    @staticmethod
    def _item(summary: Optional[Any], index: int) -> Optional[float]:
        if summary is None or index >= len(summary.full_dist):
            return None
        return WandbStatsWriter._finite_float(summary.full_dist[index])

    def on_add_stat(self, category: str, key: str, value: float, aggregation: Any = None) -> None:
        # Per-episode rows are assembled in write_stats, where the trainer step and
        # every outcome flag in the same summary window are available together.
        return

    def write_stats(self, category: str, values: Dict[str, Any], step: int) -> None:
        aggregate: Dict[str, Any] = {"trainer/step": step}
        for key, summary in values.items():
            value = self._finite_float(summary.aggregated_value)
            if value is not None:
                clean_key = key.replace(" ", "_")
                aggregate[f"mlagents/{category}/{clean_key}"] = value

        rewards = values.get("Environment/Cumulative Reward")
        if rewards is not None:
            mean_reward = self._finite_float(rewards.mean)
            std_reward = self._finite_float(rewards.std)
            if mean_reward is not None:
                aggregate["train/reward_mean"] = mean_reward
                aggregate["reward/mean"] = mean_reward
            if std_reward is not None:
                aggregate["train/reward_std"] = std_reward
                aggregate["reward/std"] = std_reward
            aggregate["train/episodes_completed"] = len(rewards.full_dist)

            success = self._find(values, "Episode/SuccessRate")
            avoidance_success = self._find(values, "Episode/AvoidanceSuccessRate")
            safe_pass = self._find(values, "Episode/SafePassRate")
            collision = self._find(values, "Episode/CollisionRate")
            emergency = self._find(values, "Episode/EmergencyStopRate")
            lane_exit = self._find(values, "Episode/LaneExitRate")
            timeout = self._find(values, "Episode/TimeoutRate")
            passive_failure = self._find(values, "Episode/PassiveFailureRate")
            minimum_distance = self._find(values, "Episode/MinimumDistance")
            maximum_forward_speed = self._find(values, "Episode/MaximumForwardSpeed")
            experienced = self._find(values, "Episode/ExperiencedAvoidance")
            intervention_required = self._find(values, "Episode/InterventionRequired")
            intervention_success = self._find(values, "Episode/InterventionSuccessRate")
            active_required = self._find(values, "Episode/ActiveAvoidanceRequired")
            active_success = self._find(values, "Episode/ActiveAvoidanceSuccessRate")
            yield_required = self._find(values, "Episode/YieldRequired")
            yield_success = self._find(values, "Episode/YieldSuccessRate")
            natural_safe_pass = self._find(values, "Episode/NaturalSafePassRate")
            stationary_path_clearance = self._find(values, "Episode/StationaryPathClearance")
            nominal_path_clearance = self._find(values, "Episode/NominalPathClearance")
            magnitude_small = self._find(values, "Episode/MaxMagnitudeSmallRate")
            magnitude_medium = self._find(values, "Episode/MaxMagnitudeMediumRate")
            magnitude_large = self._find(values, "Episode/MaxMagnitudeLargeRate")
            decision_stop = self._find(values, "Decision/StopRate")
            decision_left = self._find(values, "Decision/LeftRate")
            decision_right = self._find(values, "Decision/RightRate")
            decision_reverse = self._find(values, "Decision/ReverseRate")
            decision_none = self._find(values, "Decision/NoneRate")
            forced_stop = self._find(values, "Decision/ForcedStopRate")
            avoidance_only = self._find(values, "Curriculum/AvoidanceOnly")
            lane_recovery = self._find(values, "Curriculum/LaneRecovery")
            scenario_left = self._find(values, "Scenario/ApproachLeftRate")
            scenario_centre = self._find(values, "Scenario/ApproachCentreRate")
            scenario_right = self._find(values, "Scenario/ApproachRightRate")
            approach_angle = self._find(values, "Scenario/ApproachAngleDegrees")
            scenario_intervention = self._find(values, "Scenario/InterventionMix")
            scenario_moderate = self._find(values, "Scenario/ModerateMix")
            scenario_natural = self._find(values, "Scenario/NaturalPassMix")
            classification_match = self._find(values, "Scenario/ClassificationMatch")
            scenario_foundation = self._find(values, "Curriculum/ScenarioFoundation")
            scenario_mixed = self._find(values, "Curriculum/ScenarioMixed")

            summaries = {
                "success_rate": success,
                "avoidance_success_rate": avoidance_success,
                "safe_pass_rate": safe_pass,
                "collision_rate": collision,
                "emergency_stop_rate": emergency,
                "lane_exit_rate": lane_exit,
                "timeout_rate": timeout,
                "passive_failure_rate": passive_failure,
                "minimum_distance_mean": minimum_distance,
                "maximum_forward_speed_mean": maximum_forward_speed,
                "experienced_avoidance_rate": experienced,
                "intervention_required_rate": intervention_required,
                "intervention_success_all_episode_rate": intervention_success,
                "active_avoidance_required_rate": active_required,
                "active_avoidance_success_all_episode_rate": active_success,
                "yield_required_rate": yield_required,
                "yield_success_all_episode_rate": yield_success,
                "natural_safe_pass_rate": natural_safe_pass,
                "stationary_path_clearance_mean": stationary_path_clearance,
                "nominal_path_clearance_mean": nominal_path_clearance,
                "max_magnitude_small_rate": magnitude_small,
                "max_magnitude_medium_rate": magnitude_medium,
                "max_magnitude_large_rate": magnitude_large,
                "decision_stop_rate": decision_stop,
                "decision_left_rate": decision_left,
                "decision_right_rate": decision_right,
                "decision_reverse_rate": decision_reverse,
                "decision_none_rate": decision_none,
                "forced_stop_rate": forced_stop,
                "curriculum_avoidance_only": avoidance_only,
                "curriculum_lane_recovery": lane_recovery,
                "scenario_approach_left_rate": scenario_left,
                "scenario_approach_centre_rate": scenario_centre,
                "scenario_approach_right_rate": scenario_right,
                "approach_angle_degrees_mean": approach_angle,
                "scenario_intervention_mix": scenario_intervention,
                "scenario_moderate_mix": scenario_moderate,
                "scenario_natural_pass_mix": scenario_natural,
                "scenario_classification_match_rate": classification_match,
                "scenario_curriculum_foundation": scenario_foundation,
                "scenario_curriculum_mixed": scenario_mixed,
            }
            for name, summary in summaries.items():
                if summary is not None:
                    summary_value = self._finite_float(summary.aggregated_value)
                    if summary_value is not None:
                        aggregate[f"train/{name}"] = summary_value

            for index in range(len(rewards.full_dist)):
                reward = self._item(rewards, index)
                flags = {
                    "success": self._item(success, index) or 0.0,
                    "avoidance_success": self._item(avoidance_success, index) or 0.0,
                    "safe_pass": self._item(safe_pass, index) or 0.0,
                    "collision": self._item(collision, index) or 0.0,
                    "emergency_stop": self._item(emergency, index) or 0.0,
                    "lane_exit": self._item(lane_exit, index) or 0.0,
                    "timeout": self._item(timeout, index) or 0.0,
                    "passive_failure": self._item(passive_failure, index) or 0.0,
                }
                reason = "success"
                if flags["safe_pass"] > 0.5:
                    reason = "safe_pass"
                elif flags["collision"] > 0.5:
                    reason = "collision"
                elif flags["emergency_stop"] > 0.5:
                    reason = "emergency_stop"
                elif flags["lane_exit"] > 0.5:
                    reason = "lane_exit"
                elif flags["timeout"] > 0.5:
                    reason = "timeout"
                elif flags["passive_failure"] > 0.5:
                    reason = "passive_failure"
                elif flags["success"] <= 0.5:
                    reason = "unknown"
                failure_code = {
                    "success": 0,
                    "safe_pass": 0,
                    "collision": 1,
                    "emergency_stop": 2,
                    "lane_exit": 3,
                    "timeout": 4,
                    "passive_failure": 5,
                    "unknown": 6,
                }[reason]

                scenario_flags = {
                    "approach_left": self._item(scenario_left, index) or 0.0,
                    "approach_centre": self._item(scenario_centre, index) or 0.0,
                    "approach_right": self._item(scenario_right, index) or 0.0,
                }
                scenario_name = max(scenario_flags, key=scenario_flags.get)
                difficulty_flags = {
                    "active_avoidance": self._item(scenario_intervention, index) or 0.0,
                    "yield": self._item(scenario_moderate, index) or 0.0,
                    "natural_pass": self._item(scenario_natural, index) or 0.0,
                }
                difficulty_name = max(difficulty_flags, key=difficulty_flags.get)
                decision_flags = {
                    "stop": self._item(decision_stop, index) or 0.0,
                    "left": self._item(decision_left, index) or 0.0,
                    "right": self._item(decision_right, index) or 0.0,
                    "reverse": self._item(decision_reverse, index) or 0.0,
                    "none": self._item(decision_none, index) or 0.0,
                }
                decision_name = max(decision_flags, key=decision_flags.get)
                magnitude_flags = {
                    "small": self._item(magnitude_small, index) or 0.0,
                    "medium": self._item(magnitude_medium, index) or 0.0,
                    "large": self._item(magnitude_large, index) or 0.0,
                }
                magnitude_name = max(magnitude_flags, key=magnitude_flags.get)
                if max(magnitude_flags.values()) <= 0.5:
                    magnitude_name = "none"
                required = self._item(active_required, index) or self._item(intervention_required, index) or 0.0
                required_success = self._item(active_success, index) or self._item(intervention_success, index) or 0.0
                requires_yield = self._item(yield_required, index) or 0.0
                yielded_successfully = self._item(yield_success, index) or 0.0
                natural_pass = self._item(natural_safe_pass, index) or 0.0

                self.recent_success.append(flags["success"])
                self.recent_collision.append(flags["collision"])
                self.recent_emergency.append(flags["emergency_stop"])
                self.recent_natural_pass.append(natural_pass)
                self.recent_intervention_required.append(required)
                self.recent_yield_required.append(requires_yield)
                self.recent_stop_decisions.append(decision_flags["stop"])
                if required > 0.5:
                    self.recent_intervention_success.append(required_success)
                    self.recent_active_stop_decisions.append(decision_flags["stop"])
                    if decision_flags["stop"] > 0.5:
                        self.recent_required_stop_success.append(required_success)
                    for action_name, history in self.recent_active_action_success.items():
                        if decision_flags[action_name] > 0.5:
                            history.append(required_success)
                if requires_yield > 0.5:
                    self.recent_yield_success.append(yielded_successfully)
                    self.recent_yield_stop_decisions.append(decision_flags["stop"])

                self.episode_index += 1
                episode_row: Dict[str, Any] = {
                    "episode/index": self.episode_index,
                    "episode/trainer_step": step,
                    "episode/failure_reason": reason,
                    "episode/failure_code": failure_code,
                    "episode/scenario": scenario_name,
                    "episode/scenario_difficulty": difficulty_name,
                    "episode/decision": decision_name,
                    "episode/max_avoidance_magnitude": magnitude_name,
                    "episode/forced_stop": self._item(forced_stop, index) or 0.0,
                    "episode/intervention_required": required,
                    "episode/intervention_success": required_success,
                    "episode/active_avoidance_required": required,
                    "episode/active_avoidance_success": required_success,
                    "episode/yield_required": requires_yield,
                    "episode/yield_success": yielded_successfully,
                    "episode/natural_safe_pass": natural_pass,
                    "episode/curriculum_avoidance_only": self._item(avoidance_only, index) or 0.0,
                    "episode/curriculum_lane_recovery": self._item(lane_recovery, index) or 0.0,
                    **{f"episode/decision_{name}": value for name, value in decision_flags.items()},
                    **{f"episode/max_magnitude_{name}": value for name, value in magnitude_flags.items()},
                    **{f"episode/scenario_{name}": value for name, value in scenario_flags.items()},
                    **{f"episode/difficulty_{name}": value for name, value in difficulty_flags.items()},
                    **{f"episode/{name}": value for name, value in flags.items()},
                }
                if reward is not None:
                    episode_row["episode/final_reward"] = reward
                    episode_row["reward/episode_final"] = reward
                distance = self._item(minimum_distance, index)
                if distance is not None:
                    episode_row["episode/minimum_distance"] = distance
                max_speed = self._item(maximum_forward_speed, index)
                if max_speed is not None:
                    episode_row["episode/maximum_forward_speed"] = max_speed
                avoided = self._item(experienced, index)
                if avoided is not None:
                    episode_row["episode/experienced_avoidance"] = avoided
                angle = self._item(approach_angle, index)
                if angle is not None:
                    episode_row["episode/approach_angle_degrees"] = angle
                clearance = self._item(stationary_path_clearance, index)
                if clearance is not None:
                    episode_row["episode/stationary_path_clearance"] = clearance
                drive_clearance = self._item(nominal_path_clearance, index)
                if drive_clearance is not None:
                    episode_row["episode/nominal_path_clearance"] = drive_clearance
                match = self._item(classification_match, index)
                if match is not None:
                    episode_row["episode/scenario_classification_match"] = match
                self.run.log(episode_row)

            aggregate["train/episodes_total"] = self.episode_index
            aggregate.update(self._rolling_metrics())

        self.run.log(aggregate)

    @staticmethod
    def _mean(values: deque) -> Optional[float]:
        return sum(values) / len(values) if values else None

    def _rolling_metrics(self) -> Dict[str, float]:
        sources = {
            "trend/overall_success_rate_100": self.recent_success,
            "trend/collision_rate_100": self.recent_collision,
            "trend/emergency_stop_rate_100": self.recent_emergency,
            "trend/natural_safe_pass_rate_100": self.recent_natural_pass,
            "trend/intervention_required_rate_100": self.recent_intervention_required,
            "trend/intervention_success_rate_100": self.recent_intervention_success,
            "trend/active_avoidance_success_rate_100": self.recent_intervention_success,
            "trend/yield_required_rate_100": self.recent_yield_required,
            "trend/yield_success_rate_100": self.recent_yield_success,
            "trend/stop_selection_rate_100": self.recent_stop_decisions,
            "trend/required_stop_success_rate_100": self.recent_required_stop_success,
            "trend/active_stop_selection_rate_100": self.recent_active_stop_decisions,
            "trend/yield_stop_selection_rate_100": self.recent_yield_stop_decisions,
        }
        for action_name, history in self.recent_active_action_success.items():
            sources[f"trend/active_{action_name}_success_rate_100"] = history
        result: Dict[str, float] = {}
        for key, values in sources.items():
            mean = self._mean(values)
            if mean is not None:
                result[key] = mean
        return result

    def add_property(self, category: str, property_type: Any, value: Any) -> None:
        if getattr(property_type, "value", "") == "hyperparameters" and isinstance(value, dict):
            self.run.config.update({f"mlagents/{category}": value}, allow_val_change=True)


def argument_value(name: str, default: str) -> str:
    prefix = name + "="
    for index, argument in enumerate(sys.argv[1:]):
        if argument.startswith(prefix):
            return argument[len(prefix):]
        if argument == name and index + 2 < len(sys.argv):
            return sys.argv[index + 2]
    return default


def keep_training_when_onnx_export_fails(run: Any) -> None:
    """Make ONNX export best-effort while preserving PyTorch checkpoints.

    ML-Agents writes both checkpoint.pt and the numbered .pt file before it
    invokes TorchModelSaver.export().  Some PyTorch versions import
    ``onnxscript`` only at that point.  A missing/blocked ONNX dependency must
    not tear down the Unity connection, the trainer, and the W&B run after an
    otherwise successful checkpoint save.
    """
    from mlagents.trainers.model_saver.torch_model_saver import TorchModelSaver

    original_export = TorchModelSaver.export
    failure_count = 0

    def best_effort_export(self: Any, output_filepath: str, behavior_name: str) -> None:
        nonlocal failure_count
        try:
            original_export(self, output_filepath, behavior_name)
        except Exception as error:
            failure_count += 1
            error_text = f"{type(error).__name__}: {error}"
            print(
                "[WARNING] PyTorch checkpoint was saved, but ONNX export failed. "
                "Training will continue. " + error_text,
                file=sys.stderr,
                flush=True,
            )
            try:
                run.summary["diagnostic/onnx_export_failures"] = failure_count
                run.summary["diagnostic/last_onnx_export_error"] = error_text[:1000]
            except Exception as reporting_error:
                # Diagnostics must never turn a recoverable export failure back
                # into a trainer failure when W&B is temporarily unavailable.
                print(
                    "[WARNING] Could not report ONNX failure to W&B: "
                    f"{reporting_error}",
                    file=sys.stderr,
                    flush=True,
                )

    TorchModelSaver.export = best_effort_export


def main() -> None:
    run_id = argument_value("--run-id", "HumanAvoidance")
    wandb_run_id = re.sub(r"[^A-Za-z0-9_-]", "-", run_id)[:128]
    project = os.environ.get("WANDB_PROJECT", "ship-robot-human-avoidance")
    entity = os.environ.get("WANDB_ENTITY") or None
    run = wandb.init(
        project=project,
        entity=entity,
        name=run_id,
        id=wandb_run_id,
        resume="allow",
        job_type="ml-agents-training",
        # Direct reporting avoids the failing hosted-TensorBoard pod entirely.
        sync_tensorboard=False,
        config={
            "mlagents_run_id": run_id,
            "trainer_config": sys.argv[1] if len(sys.argv) > 1 else "",
        },
    )
    try:
        from mlagents.trainers.stats import StatsReporter

        wandb.define_metric("trainer/step")
        wandb.define_metric("train/*", step_metric="trainer/step")
        wandb.define_metric("mlagents/*", step_metric="trainer/step")
        wandb.define_metric("trend/*", step_metric="trainer/step")
        wandb.define_metric("episode/index")
        wandb.define_metric("episode/*", step_metric="episode/index")
        wandb.define_metric("reward/mean", step_metric="trainer/step")
        wandb.define_metric("reward/std", step_metric="trainer/step")
        wandb.define_metric("reward/episode_final", step_metric="episode/index")
        StatsReporter.add_writer(WandbStatsWriter(run))

        keep_training_when_onnx_export_fails(run)
        from mlagents.trainers.learn import main as mlagents_main

        mlagents_main()
    finally:
        run.finish()


if __name__ == "__main__":
    main()
