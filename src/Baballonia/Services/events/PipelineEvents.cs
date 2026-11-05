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
    /// Raw post-filter eye values in 0..1, ordered
    /// leftPitch, leftYaw, leftLid, rightPitch, rightYaw, rightLid.
    /// </summary>
    public record EyeGazeSampleEvent(float[] Sample);

    public record NewFrameEvent(Mat image);

    public record NewTransformedFrameEvent(Mat image);

    public record NewFilteredResultEvent(float[] result);

    public record ExceptionEvent(Exception exception);
}
