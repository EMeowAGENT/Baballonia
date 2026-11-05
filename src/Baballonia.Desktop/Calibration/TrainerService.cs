using Baballonia.Contracts;
using Microsoft.Extensions.Logging;
using OverlaySDK.Packets;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Baballonia.Desktop.Calibration;

public partial class TrainerService(ILogger<TrainerService> logger) : ITrainerService
{
    private readonly object _lock = new();

    private Process? _trainerProcess;
    public event Action<TrainerProgressReportPacket>? OnProgress;

    [GeneratedRegex(@"Batch\s+(\d+)/(\d+),\s+Loss:\s+([0-9.]+)")]
    private static partial Regex ParseBatchRegex();
    TrainerProgressReportPacket? ParseBatch(string line)
    {
        var match = ParseBatchRegex().Match(line);
        if (!match.Success)
            return null;
        logger.LogDebug(line);

        var currentBatch = int.Parse(match.Groups[1].Value);
        var totalBatches = int.Parse(match.Groups[2].Value);
        var loss = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

        return new TrainerProgressReportPacket("Batch", currentBatch, totalBatches, loss);
    }
    [GeneratedRegex(@"Epoch\s+(\d+)/(\d+)\s+completed\s+in\s+([\d.]+)s\.\s+Average\s+loss:\s+([\d.eE+-]+)")]
    private static partial Regex ParseEpochRegex();
    TrainerProgressReportPacket? ParseEpoch(string line)
    {
        var match = ParseEpochRegex().Match(line);
        if (!match.Success)
            return null;

        logger.LogDebug(line);

        var currentEpoch = int.Parse(match.Groups[1].Value);
        var totalEpochs = int.Parse(match.Groups[2].Value);
        var loss = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

        return new TrainerProgressReportPacket("Epoch", currentEpoch, totalEpochs, loss);
    }

    [GeneratedRegex(@"\s*Training\s+completed\s+successfully!\s*")]
    private static partial Regex ParseTrainingCompleteRegex();
    bool ParseTrainingComplete(string line)
    {
        var match = ParseTrainingCompleteRegex().Match(line);

        logger.LogDebug(line);

        return match.Success;
    }

    void NewLineEventHandler(object sender, DataReceivedEventArgs dataReceivedEventArgs)
    {
        logger.LogDebug(dataReceivedEventArgs.Data);
        if (dataReceivedEventArgs.Data == null)
            return;

        var progress = ParseBatch(dataReceivedEventArgs.Data);
        if (progress == null)
            progress = ParseEpoch(dataReceivedEventArgs.Data);

        if (progress != null)
        {
            OnProgress?.Invoke(progress);
        }

        var isCompleted = ParseTrainingComplete(dataReceivedEventArgs.Data);
        if (isCompleted)
            return;
    }

    public void RunTraining(string usercalbinPath, string outputfilePath)
    {
        if (!File.Exists(usercalbinPath))
            throw new FileNotFoundException(usercalbinPath + " not found");

        var isWindows = OperatingSystem.IsWindows();
        var basePath = Path.Combine(AppContext.BaseDirectory, "Calibration", isWindows ? "Windows" : "Linux", "Trainer");
        var trainerPath = Path.Combine(basePath, isWindows ? $"BabbleTrainer.exe" : $"BabbleTrainer");
        if (!File.Exists(trainerPath))
            throw new FileNotFoundException(trainerPath + " not found");

        // The application is commonly installed in a read-only location (e.g.
        // /opt or /usr). The trainer writes checkpoints (model_best.pt and
        // friends) into its working directory, so give it a persistent,
        // writable per-user workspace instead of the install directory.
        var workspacePath = Path.Combine(Baballonia.Utils.PersistentDataDirectory, "Trainer");
        Directory.CreateDirectory(workspacePath);

        // Keep the shipped baseline checkpoints available without overwriting
        // a warm-start checkpoint left behind by a previous training run.
        foreach (var baseline in Directory.GetFiles(basePath, "baseline_*.pth"))
        {
            var destination = Path.Combine(workspacePath, Path.GetFileName(baseline));
            if (!File.Exists(destination))
                File.Copy(baseline, destination);
        }

        lock (_lock)
        {
            if (_trainerProcess != null && _trainerProcess.HasExited)
                _trainerProcess = null;

            if (_trainerProcess != null)
                throw new Exception("Training process already running");

            // Wrap both args in quotes to handle spaces in user names
            var launchArgs = $"\"{usercalbinPath}\" \"{outputfilePath}\"";
            var startInfo = new ProcessStartInfo
            {
                FileName = trainerPath,
                Arguments = launchArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                WorkingDirectory = workspacePath,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            // Baballonia sets OMP_NUM_THREADS=1 for its ONNX/OpenCV threads.
            // Child processes inherit the environment, which makes PyTorch
            // single-threaded and training several times slower. Give the
            // trainer its own thread budget.
            var threadCount = Math.Max(1, Environment.ProcessorCount)
                .ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["OMP_NUM_THREADS"] = threadCount;
            startInfo.Environment["MKL_NUM_THREADS"] = threadCount;

            logger.LogInformation(
                "Starting trainer in {Workspace} with {Threads} CPU threads",
                workspacePath, threadCount);

            _trainerProcess = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };
            _trainerProcess.OutputDataReceived += NewLineEventHandler;
            _trainerProcess.Exited += (sender, args) => { Interlocked.Exchange(ref _trainerProcess, null); };

            _trainerProcess.Start();
            _trainerProcess.BeginOutputReadLine();
        }
    }

    public Task WaitAsync()
    {
        Process? process;
        lock (_lock)
        {
            process = _trainerProcess;
        }

        return process != null
            ? process.WaitForExitAsync()
            : Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _trainerProcess?.Kill();
        }
    }
}
