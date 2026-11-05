using OpenCvSharp;
using System;

namespace Baballonia.Services.events;

public class FacePipelineEvents
{
    public record NewFrameEvent(Mat image);

    public record NewTransformedFrameEvent(Mat image);

    public record NewFilteredResultEvent(float[] result);

    public record ExceptionEvent(Exception exception);
}
public class EyePipelineEvents
{
    /// <summary>
    /// Processed eye gaze sample, in degrees and OpenXR-friendly lid semantics
    /// (0 = closed, 1 = open). Angles are right-positive yaw and up-positive
    /// pitch; the sender converts them to the OpenXR right-handed quaternion.
    /// </summary>
    public record EyeGazeSampleEvent(
        float CombinedYawDeg,
        float CombinedPitchDeg,
        float LeftYawDeg,
        float LeftPitchDeg,
        float RightYawDeg,
        float RightPitchDeg,
        float LeftLid,
        float RightLid);

    public record NewFrameEvent(Mat image);

    public record NewTransformedFrameEvent(Mat image);

    public record NewFilteredResultEvent(float[] result);

    public record ExceptionEvent(Exception exception);
}
