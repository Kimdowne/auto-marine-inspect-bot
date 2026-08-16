using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace Unity.Templates.IndustryFundamentals
{
    public class RobotVariant : MonoBehaviour
    {
        [FormerlySerializedAs("variants"), SerializeField]
        private RobotVariantConfig[] _variants = Array.Empty<RobotVariantConfig>();
        
        private RobotDataSO _dataSo;

        public int GetVariantNumber() => _variants.Length;
    
        private void Start()
        {
            _dataSo = GetComponent<Robot>().RobotData;
        }

        private void Update()
        {
            MovePartTo(_dataSo.ArmRange);
        }

        public void CycleThroughVariants()
        {
            int newVariantIndex = (_dataSo.currentVariant + 1) % _variants.Length;
            ChangeVariant(newVariantIndex);
        }

        public void ChangeVariant(int newVariant)
        {
            if (newVariant >= _variants.Length)
            {
                Debug.LogError($"The requested variant n. {newVariant} that is not in the variants list.");
                return;
            }
        
            _variants[_dataSo.currentVariant].objectToEnable.SetActive(false);
        
            _dataSo.currentVariant = newVariant;
        
            _variants[_dataSo.currentVariant].objectToEnable.SetActive(true);
        }

        public void MovePartTo(float ratio)
        {
            if (_dataSo.currentVariant >= _variants.Length) return;
        
            Transform partThatMoves = _variants[_dataSo.currentVariant].partThatMoves;
            float relativeValue = Mathf.Lerp(_variants[_dataSo.currentVariant].startingValue, _variants[_dataSo.currentVariant].endingValue, ratio);
        
            partThatMoves.localPosition = GetRelativePosition(partThatMoves.localPosition, relativeValue,
                _variants[_dataSo.currentVariant].movementAxis);
        }

        private Vector3 GetRelativePosition(Vector3 originalPosition, float relativeValue, MovementAxis axis)
        {
            Vector3 newPosition = originalPosition;
            switch (axis)
            {
                case MovementAxis.X:
                    newPosition.x = relativeValue;
                    break;
                case MovementAxis.Y:
                    newPosition.y = relativeValue;
                    break;
                case MovementAxis.Z:
                    newPosition.z = relativeValue;
                    break;
            }

            return newPosition;
        }
    }

    [Serializable]
    public struct RobotVariantConfig
    {
        [Tooltip("The robot part to turn on/off when the user changes Robot variant in the UI.")] public GameObject objectToEnable;
        [Tooltip("The robot part to move when the user drags the slider in the UI.")] public Transform partThatMoves;
        [Tooltip("The axis on which the robot part moves. Relative to its parent.")] public MovementAxis movementAxis;
        [Tooltip("The starting value on the movementAxis.")]  public float startingValue;
        [Tooltip("The ending value on the movementAxis.")] public float endingValue;
    }

    [Serializable]
    public enum MovementAxis
    {
        [Tooltip("Right")] X,
        [Tooltip("Up")] Y,
        [Tooltip("Forward")] Z
    }
}