using System.Collections;
using UnityEngine;
using UnityEngine.AI;

namespace Unity.Templates.IndustryFundamentals
{
    public class Robot : MonoBehaviour
    {
        [SerializeField] private RobotDataSO _data;
    
        public RobotDataSO RobotData => _data;
        public NavMeshAgent NavMeshAgent => _navMeshAgent;
        public RobotVariant VariantScript => _variantScript;
    
        private NavMeshAgent _navMeshAgent;
        private NavMeshObstacle _activeObstacle;
        private RobotVariant _variantScript;

        private void Awake()
        {
            _navMeshAgent = GetComponent<NavMeshAgent>();
            _variantScript = GetComponent<RobotVariant>();
            gameObject.layer = LayerMask.NameToLayer("Bot");
        }

        private void OnEnable()
        {
            RobotManager.Instance.ActiveRobotChanged += ActiveRobotChanged;
        }
    
        /// <summary>
        /// Invoked by the UI buttons Auto/Manual
        /// </summary>
        /// <param name="newMode"></param>
        public void SetOperationMode(OperationMode newMode)
        {
            _data.OperationMode = newMode;

            HandleNavigationComponents(_data.OperationMode);
        }

        /// <summary>
        /// Invoked in response to the <see cref="RobotManager.ActiveRobotChanged"/> event.
        /// </summary>
        private void ActiveRobotChanged(Robot newActiveRobot)
        {
            _navMeshAgent.enabled = _data.OperationMode == OperationMode.Auto;
        }

        public void AssignNewDataSO(RobotDataSO robotDataSO)
        {
            _data = robotDataSO;
        }
    
        private void HandleNavigationComponents(OperationMode operationMode)
        {
            _activeObstacle = GetComponent<NavMeshObstacle>();

            if (operationMode == OperationMode.Manual)
            {
                _navMeshAgent.enabled = false;
            
                if (_activeObstacle == null)
                    _activeObstacle = gameObject.AddComponent<NavMeshObstacle>();

                _activeObstacle.shape = NavMeshObstacleShape.Capsule; // Or Box based on your bot
                _activeObstacle.radius = 0.3f;
                _activeObstacle.carving = true;
                _activeObstacle.carveOnlyStationary = false; // Carve even while moving
            }
            else if (_activeObstacle != null)
            {
                Destroy(_activeObstacle);
                StartCoroutine(ReEnableNavMeshAgent());
            }
        }

        private IEnumerator ReEnableNavMeshAgent()
        {
            // This wait gives time to the NavMesh system to remove the NavMeshObstacle, 
            // avoiding the "jump" when the robot resumes Auto behaviour
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            
            _navMeshAgent.enabled = true;
        }

        private void OnDisable()
        {
            RobotManager.Instance.ActiveRobotChanged -= ActiveRobotChanged;
        }
    }
}