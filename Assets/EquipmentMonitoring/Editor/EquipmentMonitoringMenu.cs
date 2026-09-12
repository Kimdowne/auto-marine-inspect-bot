using UnityEditor;
using UnityEditor.SceneManagement;

namespace ShipRobot.EquipmentMonitoring.Editor
{
    public static class EquipmentMonitoringMenu
    {
        [MenuItem("Tools/Ship Robot/Open Equipment Monitoring Prototype")]
        public static void Open()
        {
            if (EditorApplication.isPlaying || !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene("Assets/EquipmentMonitoring/EquipmentMonitoring.unity");
        }
    }
}
