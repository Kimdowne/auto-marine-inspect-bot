namespace ShipRobot.ObstacleAvoidance
{
    public struct PersonBearingObservation
    {
        public float normalizedHorizontalPosition;
        public float confidence;
        public float normalizedBoxWidth;
        public float normalizedBoxHeight;
        public double timestamp;
    }

    public interface IPersonBearingSource
    {
        bool TryGetPersonBearing(out PersonBearingObservation observation);
    }
}
