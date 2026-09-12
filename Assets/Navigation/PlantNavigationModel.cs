using System;
using UnityEngine;

namespace ShipRobot.Navigation
{
    public enum PlantNodeId
    {
        UpperLeft = 1,
        UpperRight = 2,
        UpperMid = 3,
        UnderLeft = 4,
        UnderRight = 5,
        UnderMid = 6
    }

    public enum PlantMission
    {
        PerimeterPatrol,
        InspectEquipmentA,
        InspectEquipmentB,
        ReturnToBase
    }

    [Serializable]
    public struct MarkerObservation
    {
        public PlantNodeId nodeId;
        public Vector3 cameraRelativePosition;
        public Quaternion cameraRelativeRotation;
        [Range(0f, 1f)] public float confidence;
        public double timestamp;
    }

    /// <summary>
    /// Implemented later by the Unity simulation detector and the physical AprilTag detector.
    /// Navigation code depends only on this interface, not on a particular tag library.
    /// </summary>
    public interface IMarkerObservationSource
    {
        bool TryGetLatestObservation(out MarkerObservation observation);
    }

}
