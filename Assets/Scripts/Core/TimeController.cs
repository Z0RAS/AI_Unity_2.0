using System;
using UnityEngine;

[DisallowMultipleComponent]
public class TimeController : MonoBehaviour
{
    public static TimeController Instance { get; private set; }

    [Tooltip("Target tick interval in seconds (e.g. 0.02 = 50 TPS).")]
    public float tickInterval = 0.02f;

    public float LastTickDelta { get; private set; }
    public float InterpolationAlpha { get; private set; }

    // Global tick index (monotonic). Use this to make tick-driven systems deterministic
    // and to avoid realtime/backlog races.
    public static long GlobalTickIndex { get; private set; } = 0;

    public static event Action<float> OnTick;

    float accumulator = 0f;

    void Awake()
    {
        if (Instance != null && Instance != this) Destroy(this);
        Instance = this;
        LastTickDelta = tickInterval;
    }

    void Update()
    {
        accumulator += Time.deltaTime;
        while (accumulator >= tickInterval)
        {
            LastTickDelta = tickInterval;

            // Advance global tick index before invoking subscribers
            GlobalTickIndex++;

            try { OnTick?.Invoke(LastTickDelta); } catch { }
            accumulator -= tickInterval;
        }

        InterpolationAlpha = Mathf.Clamp01(accumulator / Mathf.Max(tickInterval, 1e-6f));
    }
}