using System;
using UnityEngine;

namespace ShipRobot.EquipmentMonitoring
{
    // Standalone scene entry point. No robot, navigation, training or wheel dependencies.
    public sealed class EquipmentMonitoringPrototype : MonoBehaviour
    {
        [Serializable]
        public sealed class Profile
        {
            public string equipmentId;
            public string capacity = "2.2kW";
            public string sourceEquipmentId;
            public string faultLabel;
            public bool startWithFault;
        }
        [SerializeField] private string dataRoot = "data";
        [SerializeField] private bool enableNetwork = true;
        [SerializeField] private int networkPort = 8765;
        [SerializeField] private Profile equipmentA = new Profile
            { equipmentId = "A", sourceEquipmentId = "L-DSF-01", faultLabel = "축정렬불량" };
        [SerializeField] private Profile equipmentB = new Profile
            { equipmentId = "B", sourceEquipmentId = "L-SF-04", faultLabel = "베어링불량", startWithFault = true };
        private void Awake()
        {
            var a = CreateSource(equipmentA); var b = CreateSource(equipmentB);
            gameObject.AddComponent<EquipmentMonitorPanel>().SetSources(a, b);
            if (enableNetwork) gameObject.AddComponent<EquipmentNetworkBridge>().Configure(networkPort, a, b);
            var cameraObject = new GameObject("Monitor Camera");
            cameraObject.transform.SetParent(transform, false);
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.035f, 0.05f, 0.075f);
            camera.cullingMask = 0;
        }
        private CsvReplayDataSource CreateSource(Profile profile)
        {
            var child = new GameObject("Equipment " + profile.equipmentId);
            child.transform.SetParent(transform, false);
            var source = child.AddComponent<CsvReplayDataSource>();
            source.Configure(profile.equipmentId, dataRoot, profile.capacity,
                profile.sourceEquipmentId, profile.faultLabel, profile.startWithFault);
            return source;
        }
    }
}
