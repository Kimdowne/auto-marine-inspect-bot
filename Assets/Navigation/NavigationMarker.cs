using UnityEngine;

namespace ShipRobot.Navigation
{
    [DisallowMultipleComponent]
    public sealed class NavigationMarker : MonoBehaviour
    {
        [SerializeField] private PlantNodeId nodeId;
        [SerializeField, Min(0.01f)] private float physicalSizeMetres = 0.30f;

        public PlantNodeId NodeId => nodeId;
        public float PhysicalSizeMetres => physicalSizeMetres;

        private void OnDrawGizmos()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(transform.position, Vector3.one * physicalSizeMetres);
        }
    }
}
