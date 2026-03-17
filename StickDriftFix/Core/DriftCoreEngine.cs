using System.Numerics;

namespace DriftCore.Core;

/// <summary>
/// DriftCore Engine — Real-time stick drift correction pipeline.
///
/// Processing stages:
///   1. Learned-center offset correction
///   2. Idle detection + adaptive center learning
///   3. Intermittent drift spike suppression
///   4. Deadzone with hysteresis
///   5. Exponential moving average smoothing
/// </summary>
public class DriftCoreEngine
{
    // ═══════════════════════════════════════════════════════════════════
    // Configuration — all tunable at runtime
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Radius of the dead zone in normalised [0,1] space.</summary>
    public float DeadzoneRadius { get; set; } = 0.08f;

    /// <summary>
    /// Hysteresis band around the deadzone boundary.
    /// Activate threshold = DeadzoneRadius + HysteresisMargin.
    /// Deactivate threshold = DeadzoneRadius - HysteresisMargin.
    /// Eliminates jitter at the edge.
    /// </summary>
    public float HysteresisMargin { get; set; } = 0.025f;

    /// <summary>
    /// EMA smoothing coefficient [0 = off, 0.95 = very heavy].
    /// Output = alpha * previous + (1-alpha) * current.
    /// </summary>
    public float SmoothingFactor { get; set; } = 0.30f;

    /// <summary>
    /// Rate at which the engine adapts its learned center.
    /// Lower = slower adaptation, more stable but less responsive to aging hardware.
    /// </summary>
    public float LearningRate { get; set; } = 0.0008f;

    /// <summary>
    /// Any sudden jump from near-zero larger than this is classified as a drift spike.
    /// </summary>
    public float SpikeThreshold { get; set; } = 0.35f;

    /// <summary>When false the engine passes input straight through untouched.</summary>
    public bool IsEnabled { get; set; } = true;

    // ═══════════════════════════════════════════════════════════════════
    // Internal state
    // ═══════════════════════════════════════════════════════════════════
    private Vector2 _learnedCenter = Vector2.Zero;
    private Vector2 _smoothedOutput = Vector2.Zero;
    private bool    _isActive       = false;          // hysteresis latch
    private float   _idleTimer      = 0f;

    private const float IdleThreshold  = 0.04f;       // magnitude below which we consider stick idle
    private const float IdleLearnDelay = 1.2f;        // seconds to wait before learning idle center

    // ═══════════════════════════════════════════════════════════════════
    // Telemetry — updated each frame, safe to read from UI thread
    // ═══════════════════════════════════════════════════════════════════
    public Vector2 RawInput        { get; private set; }
    public Vector2 CorrectedOutput { get; private set; }
    public Vector2 LearnedCenter   => _learnedCenter;
    public float   RawMagnitude    { get; private set; }
    public bool    IsSpikeActive   { get; private set; }

    // ═══════════════════════════════════════════════════════════════════
    // Main processing entry point — call once per input poll (~60/s)
    // ═══════════════════════════════════════════════════════════════════

    /// <param name="raw">Normalised stick input in range [-1, 1].</param>
    /// <param name="deltaTime">Elapsed seconds since last call.</param>
    /// <returns>Corrected stick position in range [-1, 1].</returns>
    public Vector2 Process(Vector2 raw, float deltaTime)
    {
        RawInput    = raw;
        RawMagnitude = raw.Length();
        IsSpikeActive = false;

        // ── Bypass mode ──────────────────────────────────────────────
        if (!IsEnabled)
        {
            CorrectedOutput = raw;
            return raw;
        }

        // ── Stage 1: Learned-center correction ───────────────────────
        Vector2 centered = raw - _learnedCenter;

        // ── Stage 2: Idle detection + adaptive learning ───────────────
        if (RawMagnitude < IdleThreshold)
        {
            _idleTimer += deltaTime;
            if (_idleTimer >= IdleLearnDelay)
            {
                // Slowly converge learned center toward the true idle position
                _learnedCenter = Vector2.Lerp(_learnedCenter, raw, LearningRate);
            }
        }
        else
        {
            _idleTimer = 0f;
        }

        // ── Stage 3: Spike suppression ───────────────────────────────
        // A spike is a sudden large jump while the smoothed output is near zero.
        // This catches intermittent contact inside a worn potentiometer.
        float jumpLen = (centered - _smoothedOutput).Length();
        if (jumpLen > SpikeThreshold && _smoothedOutput.Length() < 0.12f)
        {
            IsSpikeActive = true;
            centered = _smoothedOutput;   // hold last good output
        }

        // ── Stage 4: Deadzone with hysteresis ────────────────────────
        float mag = centered.Length();

        float activateAt   = DeadzoneRadius + HysteresisMargin;
        float deactivateAt = Math.Max(0f, DeadzoneRadius - HysteresisMargin);

        if (!_isActive && mag >= activateAt)
            _isActive = true;
        else if (_isActive && mag <= deactivateAt)
            _isActive = false;

        Vector2 dzOutput;
        if (!_isActive)
        {
            dzOutput = Vector2.Zero;
        }
        else
        {
            // Rescale so the deadzone edge maps to 0 and the physical edge maps to 1
            float remaining = 1f - DeadzoneRadius;
            float scaledMag = remaining > 0f
                ? Math.Clamp((mag - DeadzoneRadius) / remaining, 0f, 1f)
                : 1f;

            dzOutput = mag > 0.001f
                ? (centered / mag) * scaledMag
                : Vector2.Zero;
        }

        // ── Stage 5: Exponential moving average smoothing ────────────
        float alpha  = Math.Clamp(SmoothingFactor, 0f, 0.97f);
        _smoothedOutput = alpha * _smoothedOutput + (1f - alpha) * dzOutput;

        // Clamp to physical maximum
        _smoothedOutput = new Vector2(
            Math.Clamp(_smoothedOutput.X, -1f, 1f),
            Math.Clamp(_smoothedOutput.Y, -1f, 1f));

        CorrectedOutput = _smoothedOutput;
        return _smoothedOutput;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Utility methods
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Reset all learned state. Call when switching controllers.</summary>
    public void Reset()
    {
        _learnedCenter  = Vector2.Zero;
        _smoothedOutput = Vector2.Zero;
        _isActive       = false;
        _idleTimer      = 0f;
        RawInput        = Vector2.Zero;
        CorrectedOutput = Vector2.Zero;
        RawMagnitude    = 0f;
        IsSpikeActive   = false;
    }

    /// <summary>
    /// Immediately set the learned center to the current stick reading.
    /// Use this for manual one-shot calibration.
    /// </summary>
    public void CalibrateNow(Vector2 currentIdle)
    {
        _learnedCenter  = currentIdle;
        _smoothedOutput = Vector2.Zero;
        _isActive       = false;
        _idleTimer      = 0f;
    }
}
