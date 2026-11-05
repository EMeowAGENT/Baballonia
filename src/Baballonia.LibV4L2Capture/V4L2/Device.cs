using System.Runtime.InteropServices;

namespace Baballonia.LibV4L2Capture.V4L2;


public class Device : IDisposable {
    public void Dispose()
    {
        StopCapture();

        for (var i = 0; i < _bufferStarts.Length; i++)
        {
            if (_bufferStarts[i] != IntPtr.Zero && _bufferLengths[i] > 0)
                NativeMethods.munmap(_bufferStarts[i], _bufferLengths[i]);
        }

        Data.v4l2_requestbuffers req = default;
        req.count = 0;
        req.type = v4l2_buf_type.V4L2_BUF_TYPE_VIDEO_CAPTURE;
        req.memory = v4l2_memory.V4L2_MEMORY_MMAP;
        NativeMethods.v4l2_ioctl_safe(_fileDescriptor, Ioctl.VIDIOC_REQBUFS, ref req);

        _bufferCount = 0;

        if (_fileDescriptor >= 0)
        {
            NativeMethods.v4l2_close(_fileDescriptor);
            _fileDescriptor = -1;
        }


    }

    private static readonly v4l2_pix_fmt[] SupportedFormats = [v4l2_pix_fmt.V4L2_PIX_FMT_MJPEG, v4l2_pix_fmt.V4L2_PIX_FMT_YUYV];

    private const int O_RDWR = 2;

    public string Address { get; private set; }
    public v4l2_pix_fmt PixelFormat { get; private set; }
    public Data.v4l2_format CurrentFormat { get; private set; }

    private int _fileDescriptor = -1;

    private IntPtr[] _bufferStarts = [];
    private uint[] _bufferLengths = [];
    private uint _bufferCount;

    public static Device? Connect(string address)
    {
        var device = new Device
        {
            Address = address
        };

        if (!device.AttemptOpen())
            return null;
        var caps = device.GetCapabilities();

        if (!caps.HasFlag(V4L2Capabilities.VIDEO_CAPTURE)) throw new Exception("Device cannot capture video");

        if (!caps.HasFlag(V4L2Capabilities.STREAMING)) throw new Exception("Device does not support streaming (required for mmap or userptr buffers)");

        var formats = device.GetFormats().Where(f => SupportedFormats.Contains(f.pixelformat)).ToList();

        if (formats.Count <= 0) throw new Exception($"Device does not support {string.Join(", ", SupportedFormats.Select(f => Enum.GetName(typeof(v4l2_pix_fmt), f)))}");

        formats.Sort((a, b) =>
        {
            var indexA = Array.IndexOf(SupportedFormats, a.pixelformat);
            var indexB = Array.IndexOf(SupportedFormats, b.pixelformat);
            return indexA.CompareTo(indexB);
        });

        var format = formats[0];
        device.PixelFormat = format.pixelformat;

        var candidates = new List<(uint Width, uint Height, double MaxFps)>();
        foreach (var size in device.EnumerateFrameSizes(device.PixelFormat))
        {
            // Stepwise/continuous sizes use the same union but a different
            // layout; reading the discrete fields returns min/max values as
            // if they were width/height. Only discrete entries are usable here.
            if (size.type != (uint)v4l2_frmsizetypes.V4L2_FRMSIZE_TYPE_DISCRETE)
                continue;
            if (size.discrete.width == 0 || size.discrete.height == 0)
                continue;

            var intervals = device.EnumerateFrameIntervals(
                device.PixelFormat, size.discrete.width, size.discrete.height);

            var maxFps = intervals
                .Select(interval => interval.type switch
                {
                    v4l2_frmivaltypes.V4L2_FRMIVAL_TYPE_DISCRETE =>
                        (double)interval.discrete.denominator / interval.discrete.numerator,
                    v4l2_frmivaltypes.V4L2_FRMIVAL_TYPE_CONTINUOUS or
                        v4l2_frmivaltypes.V4L2_FRMIVAL_TYPE_STEPWISE =>
                        (double)interval.stepwise.min.denominator / interval.stepwise.min.numerator,
                    _ => 0d
                })
                .Where(fps => fps > 0 && !double.IsInfinity(fps))
                .DefaultIfEmpty(0d)
                .Max();

            candidates.Add((size.discrete.width, size.discrete.height, maxFps));
        }

        // The face/eye models consume 128-224 px crops. Decoding a 1080p or
        // 4K MJPEG stream just wastes CPU, so prefer the largest mode up to
        // 640x640 and fall back to the smallest mode if the camera only
        // exposes larger resolutions.
        var chosen = candidates
            .Where(c => c.Width <= 640 && c.Height <= 640)
            .OrderByDescending(c => (ulong)c.Width * c.Height)
            .ThenByDescending(c => c.MaxFps)
            .FirstOrDefault();

        if (chosen == default)
        {
            chosen = candidates
                .OrderBy(c => (ulong)c.Width * c.Height)
                .ThenByDescending(c => c.MaxFps)
                .FirstOrDefault();
        }

        if (chosen == default)
        {
            // Some drivers do not implement ENUM_FRAMESIZES at all. Keep the
            // mode the driver currently reports instead of asking for a
            // zero-sized format (which makes VIDIOC_S_FMT fail).
            device.CurrentFormat = device.GetCurrentFormat();
            device.PixelFormat = device.CurrentFormat.pix.pixelformat;
            return device;
        }

        var bestInterval = new Data.v4l2_frmivalenum
        {
            width = chosen.Width,
            height = chosen.Height,
            pixel_format = device.PixelFormat
        };

        try
        {
            device.SetFormat(bestInterval);
        }
        catch
        {
            // The driver rejected the enumerated size; keep its current one.
            device.CurrentFormat = device.GetCurrentFormat();
            device.PixelFormat = device.CurrentFormat.pix.pixelformat;
        }

        return device;
    }

    public Data.v4l2_capability GetCapabilities()
    {
        Data.v4l2_capability cap = default;
        var ret = NativeMethods.v4l2_ioctl_safe(_fileDescriptor, Ioctl.VIDIOC_QUERYCAP, ref cap);

        if (ret < 0) throw new Exception($"VIDIOC_QUERYCAP failed: errno={Marshal.GetLastWin32Error()}");
        return cap;
    }

    public List<Data.v4l2_fmtdesc> GetFormats()
    {
        var formats = new List<Data.v4l2_fmtdesc>();
        uint index = 0;
        var type = v4l2_buf_type.V4L2_BUF_TYPE_VIDEO_CAPTURE;

        while (true)
        {
            // declare fmt inside unsafe
            Data.v4l2_fmtdesc fmt = default;
            fmt.index = index;
            fmt.type = type;
            fmt.flags = 0;
            fmt.pixelformat = 0;

            var ret = NativeMethods.v4l2_ioctl_safe(_fileDescriptor, Ioctl.VIDIOC_ENUM_FMT, ref fmt);
            if (ret < 0) break; // no more formats

            formats.Add(fmt);
            index++;
        }

        return formats;
    }

    private Data.v4l2_format GetCurrentFormat()
    {
        Data.v4l2_format fmt = default;
        fmt.type = v4l2_buf_type.V4L2_BUF_TYPE_VIDEO_CAPTURE;

        var ret = NativeMethods.v4l2_ioctl_safe(_fileDescriptor, Ioctl.VIDIOC_G_FMT, ref fmt);
        if (ret < 0) throw new Exception($"VIDIOC_G_FMT failed: errno={Marshal.GetLastWin32Error()}");

        return fmt;
    }

    public void SetFormat(Data.v4l2_frmivalenum format)
    {
        Data.v4l2_format fmt = default;
        fmt.type = v4l2_buf_type.V4L2_BUF_TYPE_VIDEO_CAPTURE;
        fmt.pix.width = format.width;
        fmt.pix.height = format.height;
        fmt.pix.pixelformat = format.pixel_format;

        var ret = NativeMethods.v4l2_ioctl_safe(_fileDescriptor, Ioctl.VIDIOC_S_FMT, ref fmt);
        if (ret < 0) throw new Exception($"VIDIOC_S_FMT failed: errno={Marshal.GetLastWin32Error()}");

        CurrentFormat = fmt;
        PixelFormat = fmt.pix.pixelformat;
    }

    public List<Data.v4l2_frmsizeenum> EnumerateFrameSizes(v4l2_pix_fmt pixelformat)
    {
        var sizes = new List<Data.v4l2_frmsizeenum>();
        uint index = 0;

        while (true)
        {
            Data.v4l2_frmsizeenum fsize = default;
            fsize.index = index;
            fsize.pixel_format = pixelformat;

            var ret = NativeMethods.v4l2_ioctl_safe(_fileDescriptor, Ioctl.VIDIOC_ENUM_FRAMESIZES, ref fsize);

            if (ret < 0) break;
            sizes.Add(fsize);
            index++;
        }

        return sizes;
    }

    public List<Data.v4l2_frmivalenum> EnumerateFrameIntervals(v4l2_pix_fmt pixelformat, uint width, uint height)
    {
        var intervals = new List<Data.v4l2_frmivalenum>();
        uint index = 0;

        while (true)
        {
            Data.v4l2_frmivalenum fival = default;
            fival.index = index;
            fival.pixel_format = pixelformat;
            fival.width = width;
            fival.height = height;

            var ret = NativeMethods.v4l2_ioctl_safe(_fileDescriptor, Ioctl.VIDIOC_ENUM_FRAMEINTERVALS, ref fival);

            if (ret < 0) break;
            intervals.Add(fival);
            index++;
        }

        return intervals;
    }

    private Data.v4l2_requestbuffers GetBuffers()
    {
        Data.v4l2_requestbuffers req = default;
        req.count = 3;
        req.type = v4l2_buf_type.V4L2_BUF_TYPE_VIDEO_CAPTURE;
        req.memory = v4l2_memory.V4L2_MEMORY_MMAP;

        var ret = NativeMethods.v4l2_ioctl_safe(_fileDescriptor, Ioctl.VIDIOC_REQBUFS, ref req);
        if (ret < 0) throw new Exception($"VIDIOC_REQBUFS failed: errno={Marshal.GetLastWin32Error()}");

        return req;
    }

    public void InitMMapBuffers()
    {
        var req = GetBuffers();
        _bufferCount = req.count;

        _bufferStarts = new IntPtr[_bufferCount];
        _bufferLengths = new uint[_bufferCount];

        for (uint i = 0; i < _bufferCount; i++)
        {
            Data.v4l2_buffer buf = default;
            buf.type = v4l2_buf_type.V4L2_BUF_TYPE_VIDEO_CAPTURE;
            buf.memory = v4l2_memory.V4L2_MEMORY_MMAP;
            buf.index = i;

            var ret = NativeMethods.v4l2_ioctl_safe(_fileDescriptor, Ioctl.VIDIOC_QUERYBUF, ref buf);
            if (ret < 0) throw new Exception($"VIDIOC_QUERYBUF failed: errno={Marshal.GetLastWin32Error()}");

            _bufferLengths[i] = buf.length;
            _bufferStarts[i] = NativeMethods.mmap(
                IntPtr.Zero, buf.length,
                Prot.PROT_READ | Prot.PROT_WRITE,
                MapFlags.MAP_SHARED,
                _fileDescriptor, new IntPtr(buf.offset));

            if (_bufferStarts[i] == -1) throw new Exception($"mmap failed: errno={Marshal.GetLastWin32Error()}");
        }
    }

    public void QueueAllBuffers()
    {
        for (uint i = 0; i < _bufferCount; i++)
        {
            Data.v4l2_buffer buf = default;
            buf.type = v4l2_buf_type.V4L2_BUF_TYPE_VIDEO_CAPTURE;
            buf.memory = v4l2_memory.V4L2_MEMORY_MMAP;
            buf.index = i;

            var ret = NativeMethods.v4l2_ioctl_safe(_fileDescriptor, Ioctl.VIDIOC_QBUF, ref buf);
            if (ret < 0) throw new Exception($"VIDIOC_QBUF failed: errno={Marshal.GetLastWin32Error()}");
        }
    }

    public void StartStreaming()
    {
        var type = v4l2_buf_type.V4L2_BUF_TYPE_VIDEO_CAPTURE;
        var ret = NativeMethods.v4l2_ioctl_safe(_fileDescriptor, Ioctl.VIDIOC_STREAMON, ref type);
        if (ret < 0) throw new Exception($"VIDIOC_STREAMON failed: errno={Marshal.GetLastWin32Error()}");
    }

    public bool FrameReady(int timeoutMs = 0)
    {
        Data.pollfd[] fds =
        [
            new()
            {
                fd = _fileDescriptor,
                events = Data.POLLIN
            }
        ];

        var ret = NativeMethods.poll(fds, 1, timeoutMs);
        if (ret < 0)
            throw new Exception($"poll failed: errno={Marshal.GetLastWin32Error()}");

        var revents = fds[0].revents;

        if ((revents & Data.POLLERR) != 0 || (revents & Data.POLLHUP) != 0)
            throw new Exception("Device disconnected or error on file descriptor");

        return (revents & Data.POLLIN) != 0;
    }

    public bool CaptureFrame(out byte[]? frame) {
        frame = null;
        if (!FrameReady())
            return false;

        Data.v4l2_buffer buf = default;
        buf.type = v4l2_buf_type.V4L2_BUF_TYPE_VIDEO_CAPTURE;
        buf.memory = v4l2_memory.V4L2_MEMORY_MMAP;

        var ret = NativeMethods.v4l2_ioctl_safe(_fileDescriptor, Ioctl.VIDIOC_DQBUF, ref buf);
        if (ret != 0)
            throw new Exception($"VIDIOC_DQBUF failed: errno={Marshal.GetLastWin32Error()}");

        const uint v4l2BufFlagError = 0x00000040;

        if (buf.index >= _bufferCount)
            throw new Exception($"VIDIOC_DQBUF returned invalid index {buf.index}");

        // Corrupted/short frames must be requeued, not handed to the decoder.
        if ((buf.flags & v4l2BufFlagError) != 0 || buf.bytesused > _bufferLengths[buf.index])
        {
            ret = NativeMethods.v4l2_ioctl_safe(_fileDescriptor, Ioctl.VIDIOC_QBUF, ref buf);
            if (ret < 0) throw new Exception($"VIDIOC_QBUF failed: errno={Marshal.GetLastWin32Error()}");
            return false;
        }

        frame = new byte[buf.bytesused];
        Marshal.Copy(_bufferStarts[buf.index], frame, 0, (int)buf.bytesused);

        ret = NativeMethods.v4l2_ioctl_safe(_fileDescriptor, Ioctl.VIDIOC_QBUF, ref buf);
        if (ret < 0) throw new Exception($"VIDIOC_QBUF failed: errno={Marshal.GetLastWin32Error()}");
        return true;
    }

    public void StopStreaming()
    {
        var type = v4l2_buf_type.V4L2_BUF_TYPE_VIDEO_CAPTURE;
        var ret = NativeMethods.v4l2_ioctl_safe(_fileDescriptor, Ioctl.VIDIOC_STREAMOFF, ref type);
        //if (ret < 0) throw new Exception($"VIDIOC_STREAMOFF failed: errno={Marshal.GetLastWin32Error()}");
    }

    public void StartCapture()
    {
        var f = GetCurrentFormat();
        InitMMapBuffers();
        QueueAllBuffers();
        StartStreaming();
    }

    public void StopCapture()
    {
        if (_fileDescriptor >= 0 && _bufferCount > 0)
            StopStreaming();
    }

    private bool AttemptOpen()
    {
        _fileDescriptor = NativeMethods.v4l2_open(Address, O_RDWR);
        return _fileDescriptor >= 0;
    }
}
