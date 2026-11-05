using Baballonia.Services.Inference.Enums;
using OpenCvSharp;
using System;

namespace Baballonia.Services.Inference;

public class DualCameraSource : IVideoSource
{
    public IVideoSource? LeftCam;
    public IVideoSource? RightCam;

    private Mat? LastLeftImage;
    private Mat? LastRightImage;

    public bool Start()
    {
        return (LeftCam?.Start() ?? true) && (RightCam?.Start() ?? true);
    }

    public bool Stop()
    {
        return (LeftCam?.Stop() ?? true) && (RightCam?.Stop() ?? true);
    }

    // Here we try to acquire 2 images from both cameras and stitch them into a single image
    // if at least one image can be acquired, try to stitch it with last second image
    public Mat? GetFrame(ColorType? color = null)
    {
        var newLeft = LeftCam?.GetFrame(color);
        var newRight = RightCam?.GetFrame(color);

        if (newLeft == null && newRight == null)
            return null;

        var left = newLeft ?? LastLeftImage;
        var right = newRight ?? LastRightImage;

        Mat? clonedLeft = null;
        Mat? clonedRight = null;
        Mat? resizedLeft = null;
        Mat? resizedRight = null;

        try
        {
            if (left == null)
            {
                if (right == null)
                    return null;
                left = clonedLeft = right.Clone();
            }
            else if (right == null)
            {
                right = clonedRight = left.Clone();
            }

            if (left.Empty() || right.Empty())
                return null;

            int minHeight = Math.Min(left.Rows, right.Rows);
            int minWidth = Math.Min(left.Cols, right.Cols);

            resizedLeft = new Mat();
            resizedRight = new Mat();
            Cv2.Resize(left, resizedLeft, new Size(minWidth, minHeight));
            Cv2.Resize(right, resizedRight, new Size(minWidth, minHeight));

            int height = Math.Max(resizedRight.Rows, resizedLeft.Rows);
            int width = resizedRight.Cols + resizedLeft.Cols;

            Mat result = new Mat(height, width, resizedLeft.Type(), Scalar.All(0));

            using (var leftRoi = new Mat(result, new Rect(0, 0, resizedLeft.Cols, resizedRight.Rows)))
                resizedLeft.CopyTo(leftRoi);
            using (var rightRoi = new Mat(result, new Rect(resizedLeft.Cols, 0, resizedRight.Cols, resizedRight.Rows)))
                resizedRight.CopyTo(rightRoi);

            // Update last images only for new frames.
            if (newLeft != null)
            {
                var cached = resizedLeft.Clone();
                LastLeftImage?.Dispose();
                LastLeftImage = cached;
            }

            if (newRight != null)
            {
                var cached = resizedRight.Clone();
                LastRightImage?.Dispose();
                LastRightImage = cached;
            }

            return result;
        }
        finally
        {
            newLeft?.Dispose();
            newRight?.Dispose();
            clonedLeft?.Dispose();
            clonedRight?.Dispose();
            resizedLeft?.Dispose();
            resizedRight?.Dispose();
        }
    }

    public void Dispose()
    {
        LeftCam?.Dispose();
        RightCam?.Dispose();
        LastLeftImage?.Dispose();
        LastRightImage?.Dispose();
    }
}
