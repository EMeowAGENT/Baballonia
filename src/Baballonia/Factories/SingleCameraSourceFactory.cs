using Baballonia.Contracts;
using Baballonia.SDK;
using Baballonia.Services.Inference.Platforms;
using Baballonia.Services.Inference.VideoSources;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Baballonia.Services.Inference;

public class SingleCameraSourceFactory
{
    private readonly ILogger<SingleCameraSourceFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IDeviceEnumerator _deviceEnumerator;
    private readonly IPlatformConnector _platformConnector;

    public SingleCameraSourceFactory(ILogger<SingleCameraSourceFactory> logger, ILoggerFactory loggerFactory, IDeviceEnumerator deviceEnumerator, IPlatformConnector platformConnector)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _deviceEnumerator = deviceEnumerator;
        _platformConnector = platformConnector;
    }

    public SingleCameraSource? Create(string address, string providerName)
    {
        ICaptureFactory? provider;
        if (!string.IsNullOrEmpty(providerName))
        {
            provider = _platformConnector.GetCaptureFactories()
                .FirstOrDefault(factory => factory.GetProviderName() == providerName && factory.CanConnect(address));
            if(provider == null)
                throw new ArgumentNullException($"No provider \"{provider}\" is not compatible with \"{address}\"");

        }
        else
        {
            provider = SelectProvider(address);
        }

        var capture = provider.Create(address);

        return new SingleCameraSource(_loggerFactory.CreateLogger<SingleCameraSource>(), capture, address);
    }

    public Task<SingleCameraSource?> CreateStart(string address)
    {
        var camera = address;
        _deviceEnumerator.Cameras ??= _deviceEnumerator.UpdateCameras();
        if (_deviceEnumerator.Cameras.TryGetValue(camera, out var mappedAddress))
        {
            camera = mappedAddress;
        }

        var provider = SelectProvider(camera);
        return CreateStart(address, provider.GetProviderName());
    }

    private ICaptureFactory SelectProvider(string address)
    {
        var factories = _platformConnector.GetCaptureFactories();

        // On Linux /dev/video* is shared by the V4L2, OpenCV and (for actual
        // Vive facial trackers) VFT backends. Prefer the native V4L2 backend;
        // it handles UVC/MJPEG far more reliably than OpenCV and avoids the
        // VFT module claiming an ordinary camera.
        if (OperatingSystem.IsLinux())
        {
            var v4l2 = factories.FirstOrDefault(factory =>
                factory.CanConnect(address) && factory.GetProviderName() == "V4L2 Camera");
            if (v4l2 != null)
                return v4l2;
        }

        return factories.FirstOrDefault(factory => factory.CanConnect(address))
               ?? throw new ArgumentNullException($"No suitable provider for {address} found");
    }

    public Task<SingleCameraSource?> CreateStart(string address, string providerName)
    {
        var camera = address;

        _deviceEnumerator.Cameras ??= _deviceEnumerator.UpdateCameras();
        if (_deviceEnumerator.Cameras.TryGetValue(camera, out var mappedAddress))
        {
            camera = mappedAddress;
        }

        return Task.Run<SingleCameraSource?>(() =>
        {
            var cameraSource = Create(camera, providerName);
            if (cameraSource == null)
                return null;

            if (!cameraSource.Start())
            {
                _logger.LogError("Could not initialize {}", address);
                return null;
            }

            Stopwatch sw = Stopwatch.StartNew();
            var timeout = TimeSpan.FromSeconds(13);
            while (sw.Elapsed < timeout)
            {
                var testFrame = cameraSource.GetFrame();
                if (testFrame != null)
                    return cameraSource;
            }

            _logger.LogError("No data was received from {}, with {}, closing... Maybe the camera is opened somewhere else?", address, providerName);
            cameraSource.Dispose();
            return null;
        });
    }
}
