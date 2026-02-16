using UnityEngine;

namespace SulfurMP.Players
{
    public struct Snapshot
    {
        public float Timestamp;
        public Vector3 Position;
        public float Yaw;
        public float Pitch;
        public Vector3 Velocity;
        public byte AnimationState;
        public bool IsGrounded;
        public byte Health;
    }

    /// <summary>
    /// Buffers timestamped snapshots and interpolates between them.
    /// Pure C# — no MonoBehaviour dependency.
    /// </summary>
    public class NetworkInterpolation
    {
        private const int MaxSnapshots = 20;
        private const float MaxExtrapolationTime = 0.05f; // 50ms max extrapolation
        private const float ClockOffsetSmoothing = 0.1f; // EMA factor per snapshot

        private readonly Snapshot[] _buffer = new Snapshot[MaxSnapshots];
        private int _count;
        private float _interpolationDelay;

        // Clock offset: localTime - remoteTimestamp, smoothed to handle jitter
        private float _clockOffset;
        private bool _clockOffsetInitialized;

        public int BufferCount => _count;
        public float LatestTimestamp => _count > 0 ? _buffer[_count - 1].Timestamp : 0f;

        public NetworkInterpolation(float interpolationDelay)
        {
            _interpolationDelay = interpolationDelay;
        }

        public void SetInterpolationDelay(float delay)
        {
            _interpolationDelay = delay;
        }

        /// <summary>
        /// Insert a snapshot, maintaining sorted order by timestamp.
        /// localReceiveTime should be the receiver's Time.unscaledTime when the snapshot arrived.
        /// </summary>
        public void AddSnapshot(Snapshot snapshot, float localReceiveTime)
        {
            // Update clock offset (maps local time → remote time)
            float newOffset = localReceiveTime - snapshot.Timestamp;
            if (!_clockOffsetInitialized)
            {
                _clockOffset = newOffset;
                _clockOffsetInitialized = true;
            }
            else
            {
                _clockOffset = Mathf.Lerp(_clockOffset, newOffset, ClockOffsetSmoothing);
            }

            // Drop snapshots older than the newest (out-of-order arrivals that are too stale)
            if (_count > 0 && snapshot.Timestamp < _buffer[_count - 1].Timestamp - 1f)
                return;

            // Find insertion index (sorted by timestamp)
            int insertIdx = _count;
            for (int i = _count - 1; i >= 0; i--)
            {
                if (_buffer[i].Timestamp <= snapshot.Timestamp)
                {
                    insertIdx = i + 1;
                    break;
                }
                if (i == 0)
                    insertIdx = 0;
            }

            // If buffer is full, shift out the oldest
            if (_count >= MaxSnapshots)
            {
                if (insertIdx == 0)
                    return; // Older than everything we have, discard

                // Shift left to drop oldest
                for (int i = 0; i < _count - 1; i++)
                    _buffer[i] = _buffer[i + 1];
                _count--;
                insertIdx--;
            }

            // Shift right to make room
            for (int i = _count; i > insertIdx; i--)
                _buffer[i] = _buffer[i - 1];

            _buffer[insertIdx] = snapshot;
            _count++;
        }

        /// <summary>
        /// Sample the interpolation buffer at the current time.
        /// Returns false if no valid state can be produced (empty buffer).
        /// </summary>
        public bool Sample(float currentTime, out Snapshot result)
        {
            result = default;

            if (_count == 0)
                return false;

            if (_count == 1)
            {
                // Only one snapshot — use it directly
                result = _buffer[0];
                return true;
            }

            // Render time advances smoothly with local clock, offset to remote timeline
            float renderTime = currentTime - _clockOffset - _interpolationDelay;

            // Find the two snapshots bracketing renderTime
            int beforeIdx = -1;
            for (int i = _count - 1; i >= 0; i--)
            {
                if (_buffer[i].Timestamp <= renderTime)
                {
                    beforeIdx = i;
                    break;
                }
            }

            if (beforeIdx < 0)
            {
                // All snapshots are after renderTime — use the oldest
                result = _buffer[0];
                return true;
            }

            int afterIdx = beforeIdx + 1;
            if (afterIdx >= _count)
            {
                // No snapshot after renderTime — extrapolate briefly from the latest
                ref var latest = ref _buffer[_count - 1];
                float timeSinceLatest = renderTime - latest.Timestamp;

                if (timeSinceLatest > MaxExtrapolationTime)
                    timeSinceLatest = MaxExtrapolationTime;

                result = latest;
                result.Position = latest.Position + latest.Velocity * timeSinceLatest;
                return true;
            }

            // Interpolate between before and after
            ref var a = ref _buffer[beforeIdx];
            ref var b = ref _buffer[afterIdx];

            float span = b.Timestamp - a.Timestamp;
            float t = span > 0.0001f ? (renderTime - a.Timestamp) / span : 0f;
            t = Mathf.Clamp01(t);

            result.Timestamp = Mathf.Lerp(a.Timestamp, b.Timestamp, t);
            result.Position = Vector3.Lerp(a.Position, b.Position, t);
            result.Yaw = Mathf.LerpAngle(a.Yaw, b.Yaw, t);
            result.Pitch = Mathf.Lerp(a.Pitch, b.Pitch, t);
            result.Velocity = Vector3.Lerp(a.Velocity, b.Velocity, t);

            // Discrete fields — nearest neighbor
            result.AnimationState = t < 0.5f ? a.AnimationState : b.AnimationState;
            result.IsGrounded = t < 0.5f ? a.IsGrounded : b.IsGrounded;
            result.Health = t < 0.5f ? a.Health : b.Health;

            return true;
        }

        public void Clear()
        {
            _count = 0;
            _clockOffsetInitialized = false;
        }
    }
}
