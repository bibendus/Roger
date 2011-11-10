namespace Rabbus
{
    /// <summary>
    /// Represents an addressable endpoint which can receive messages
    /// </summary>
    public struct RabbusEndpoint
    {
        public RabbusEndpoint(string queue)
        {
            Queue = queue;
        }

        public readonly string Queue;

        public bool IsEmpty()
        {
            return string.IsNullOrWhiteSpace(Queue);
        }

        public static implicit operator string(RabbusEndpoint endpoint)
        {
            return endpoint.Queue;
        }

        public override string ToString()
        {
            return this;
        }
    }
}