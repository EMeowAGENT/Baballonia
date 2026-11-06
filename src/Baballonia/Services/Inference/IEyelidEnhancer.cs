using OpenCvSharp;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Baballonia.Services.Inference;

/// <summary>
/// Describes components that can improve eyelid estimates with extra inference passes.
/// </summary>
public interface IEyelidEnhancer : IDisposable
{
    /// <summary>
    /// Captures the latest transformed dual-eye frame to prepare inputs for a
    /// dedicated eyelid model. The frame is owned by the caller and must be
    /// cloned if it needs to outlive this call.
    /// </summary>
    void CaptureEyeImages(Mat transformedEyes);

    /// <summary>
    /// Adjusts the raw inference outputs in-place.
    /// </summary>
    void Enhance(float[] expressions);

    /// <summary>
    /// Collects dedicated eyelid samples for the provided duration and updates
    /// internal open/closed thresholds.
    /// </summary>
    Task<bool> CalibrateAsync(TimeSpan duration, CancellationToken cancellationToken);
}
