"""Export the selected local PPO checkpoint without starting training."""
from pathlib import Path
import functools
import hashlib
import json
import torch
import onnx
from mlagents_envs.base_env import BehaviorSpec, ObservationSpec, DimensionProperty, ObservationType, ActionSpec
from mlagents.trainers.settings import NetworkSettings
from mlagents.trainers.policy.torch_policy import TorchPolicy
from mlagents.trainers.torch_entities.networks import SimpleActor
from mlagents.trainers.torch_entities.model_serialization import ModelSerializer


def main():
    root = Path(__file__).resolve().parents[1]
    source = root / "results/continuous_action_v2/HumanAvoidance/HumanAvoidance-2749897.pt"
    target = root / "Assets/Resources/ShipRobotVision/HumanAvoidanceDemo.onnx"
    checkpoint = torch.load(source, map_location="cpu", weights_only=False)
    spec = BehaviorSpec(
        [ObservationSpec((80,), (DimensionProperty.NONE,), ObservationType.DEFAULT, "VectorSensor")],
        ActionSpec(2, ()),
    )
    policy = TorchPolicy(0, spec, NetworkSettings(normalize=True, hidden_units=256, num_layers=2),
                         SimpleActor, {"conditional_sigma": False, "tanh_squash": False})
    policy.actor.load_state_dict(checkpoint["Policy"], strict=True)
    policy.actor.eval()
    target.parent.mkdir(parents=True, exist_ok=True)
    # ML-Agents' exporter targets the legacy exporter; newer torch defaults to dynamo.
    original = torch.onnx.export
    try:
        torch.onnx.export = functools.partial(original, dynamo=False)
        ModelSerializer(policy).export_policy_model(str(target.with_suffix("")))
    finally:
        torch.onnx.export = original
    model = onnx.load(str(target))
    onnx.checker.check_model(model)
    outputs = {x.name for x in model.graph.output}
    assert "deterministic_continuous_actions" in outputs, outputs
    info = {"source": str(source.relative_to(root)), "checkpoint_step": 2749897,
            "source_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
            "observations": 20, "stack": 4, "continuous_actions": 2,
            "outputs": sorted(outputs)}
    target.with_suffix(".provenance.json").write_text(json.dumps(info, indent=2), encoding="utf-8")
    print(f"Validated ONNX: {target}")


if __name__ == "__main__":
    main()
