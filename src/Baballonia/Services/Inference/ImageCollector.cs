using OpenCvSharp;
using System.Collections.Generic;
using System.Linq;

namespace Baballonia.Services.Inference;

public class ImageCollector : IImageTransformer
{
    private Queue<Mat> ImageQueue = new();
    public Mat? Apply(Mat image)
    {
        Mat[] split = image.Split();
        var merged = new Mat();
        try
        {
            foreach (var mat in split)
            {
                Cv2.EqualizeHist(mat, mat);
            }

            // Keep the per-frame channel order here; the 4-frame layout is
            // assembled below. The merged ONNX model expects the first four
            // channels to be one eye's temporal frames and the last four the
            // other eye's, not interleaved frames.
            Cv2.Merge(split, merged);
        }
        finally
        {
            foreach (var mat in split)
                mat.Dispose();
        }

        ImageQueue.Enqueue(merged);

        if (ImageQueue.Count < 5)
            return null;

        var removed = ImageQueue.Dequeue();
        removed.Dispose();

        // Newest first, matching the trainer's frame order.
        var last4 = ImageQueue.Skip(ImageQueue.Count - 4).Take(4).Reverse().ToArray();

        // The capture/trainer pipeline stores channel 1 as LeftJpeg and
        // channel 0 as RightJpeg (see FrameCollector.AddFrame(images[1],
        // images[0])). MultiInputMergedMicroChad expects all four left-eye
        // frames followed by all four right-eye frames.
        var leftChannels = new List<Mat>(4);
        var rightChannels = new List<Mat>(4);
        var octoMatrix = new Mat();
        try
        {
            foreach (var m in last4)
            {
                var splitChannels = Cv2.Split(m);
                leftChannels.Add(splitChannels[1]);
                rightChannels.Add(splitChannels[0]);
            }

            var channels = new List<Mat>(8);
            channels.AddRange(leftChannels);
            channels.AddRange(rightChannels);

            Cv2.Merge(channels.ToArray(), octoMatrix);
        }
        catch
        {
            octoMatrix.Dispose();
            throw;
        }
        finally
        {
            foreach (var channel in leftChannels)
                channel.Dispose();
            foreach (var channel in rightChannels)
                channel.Dispose();
        }

        return octoMatrix;
    }
}
