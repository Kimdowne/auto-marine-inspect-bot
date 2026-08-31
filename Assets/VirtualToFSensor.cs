using UnityEngine;

public class VirtualToFSensor : MonoBehaviour
{
    [Header("Sensor Settings")]
    public float maxDistance = 2.0f;

    [Tooltip("센서 FOV")]
    public float fieldOfView = 20f;

    [Tooltip("FOV 내부 샘플 ray 수")]
    public int rayCount = 5;

    [Header("Simulation")]
    public bool addNoise = true;
    public float noiseStdDev = 0.01f;
    public LayerMask detectionMask = ~0;
    public bool ignoreOwnHierarchy = true;

    [Header("Debug")]
    public bool drawDebugRay = true;

    private float lastDistance;

    public float ReadDistance()
    {
        float minDistance = maxDistance;

        if (rayCount <= 1)
        {
            minDistance = CastRay(transform.forward);
        }
        else
        {
            for (int i = 0; i < rayCount; i++)
            {
                float t = i / (float)(rayCount - 1);

                float angle =
                    Mathf.Lerp(
                        -fieldOfView * 0.5f,
                        fieldOfView * 0.5f,
                        t
                    );

                Vector3 direction =
                    Quaternion.AngleAxis(angle, transform.up)
                    * transform.forward;

                float distance = CastRay(direction);

                if (distance < minDistance)
                {
                    minDistance = distance;
                }
            }
        }

        if (addNoise)
        {
            minDistance += RandomGaussian() * noiseStdDev;
        }

        minDistance =
            Mathf.Clamp(
                minDistance,
                0f,
                maxDistance
            );

        lastDistance = minDistance;

        return minDistance;
    }

    private float CastRay(Vector3 direction)
    {
        RaycastHit[] hits = Physics.RaycastAll(
            transform.position,
            direction,
            maxDistance,
            detectionMask,
            QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];
            if (ignoreOwnHierarchy && hit.transform.root == transform.root)
                continue;
            if (drawDebugRay)
            {
                Debug.DrawRay(
                    transform.position,
                    direction * hit.distance,
                    Color.red
                );
            }
            return hit.distance;
        }

        if (drawDebugRay)
        {
            Debug.DrawRay(
                transform.position,
                direction * maxDistance,
                Color.green
            );
        }

        return maxDistance;
    }

    private float RandomGaussian()
    {
        float u1 =
            1.0f - Random.value;

        float u2 =
            1.0f - Random.value;

        return Mathf.Sqrt(
            -2.0f * Mathf.Log(u1)
        ) *
        Mathf.Sin(
            2.0f * Mathf.PI * u2
        );
    }

    public float GetLastDistance()
    {
        return lastDistance;
    }
}
